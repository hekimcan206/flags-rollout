# Threat model

A rollout controller is an automated actor with write access to production behaviour. The interesting threats are
less about stealing data (there is none worth stealing here; everything is synthetic) and more about making the
controller do the wrong thing: promote broken code, roll back good code, or flap.

## Assets

| Asset | Why it matters |
|---|---|
| Flag file (`/flags/flags.json`) | Decides which code path every customer runs. Write access is production change access |
| Request counters in Prometheus | The controller's only evidence. Whoever controls them controls the verdict |
| Controller decisions and audit log | The record of why production changed; needed after an incident |
| Fault-injection endpoint of the demo API | Can make the service fail on purpose |

## Actors

- **Operator**: edits the flag file, restarts a canary after a rollback, reads `/status` and the audit log.
- **Service instances**: push metrics, evaluate flags read-only.
- **Attacker on the internal network**: can reach services that listen on the compose network or a cluster network.
- **Faulty component**: Prometheus down, flagd down, controller crash-looping, clock skew.

## Threats and mitigations (STRIDE)

| # | Threat | Category | Mitigation | Residual risk |
|---|---|---|---|---|
| 1 | Forged metrics pushed to Prometheus' OTLP receiver make a broken canary look healthy (promote) or a healthy one look broken (rollback) | Spoofing / Tampering | Receiver reachable only on the compose network and `127.0.0.1`. Counts are read per `instance`; the policy needs the canary to be worse than the baseline measured at the same time, so inflating only one side is visible in `/status` and the audit entry, which record both counts | The receiver has no authentication. In production, put an OpenTelemetry Collector with mTLS or a bearer token in front of it, and allow only the service identities to write |
| 2 | Someone with write access to the flag volume edits weights or metadata | Tampering / Elevation | Only the controller mounts the volume read-write; flagd mounts it read-only; the controller runs as a non-root user in a chiseled image without a shell. Every controller change is logged and appended to the audit log with counts and reason | Edits by other writers are detected (hash changes) and logged, not prevented. Production: GitOps or the Kubernetes API with RBAC instead of a shared volume |
| 3 | A malformed or half-written flag file makes flagd serve defaults or fail | Denial of service | Atomic replace (temp file + flush + rename). The controller refuses to rewrite flags whose shape it does not understand and skips a tick when the file does not parse | flagd keeps its last good state on a parse error; a human typo still lands in the file |
| 4 | PromQL injection through the configured flag key or variant names | Tampering | Flag key and variants are validated against `^[A-Za-z0-9._-]+$` at startup (`ControllerOptions`), so they cannot leave the string literal | None known |
| 5 | Monitoring outage leads to rollbacks across all canaries, or to promotions without evidence | Denial of service | Fail static (ADR 0004): no metrics means no change in either direction; readiness turns degraded; tick failures are counted | A broken canary keeps its traffic share during the outage, bounded by the canary percentage |
| 6 | Controller flaps a flag on and off | Tampering (availability) | Rollback is terminal; the controller never re-enables a flag; two consecutive unhealthy verdicts are required; 0% is never treated as a step | None by design; restarting is a human decision |
| 7 | Fault-injection endpoint abused to break the demo API | Elevation | Disabled unless `Admin:ApiKey` is set; the key is compared in constant time; values are validated to 0..1. Compose binds ports to `127.0.0.1` | The demo key in `.env.example` is a development value; never expose the demo API publicly |
| 8 | Denial of service on the quote endpoint | Denial of service | Input validation (customer id pattern, items 1..100); no unbounded work per request | No rate limiting in the demo |
| 9 | Decisions cannot be reconstructed after an incident | Repudiation | Every change writes the reason, both variants' counts and the policy into the flag metadata, the structured log and the JSON-lines audit log | The audit log lives on the same volume as the flag file; ship it to central logging in production |
| 10 | Personal data in flags, logs or metrics | Information disclosure | Targeting key is a synthetic customer id; metrics carry only flag key, variant and outcome (no customer ids, no high-cardinality labels); logs carry no request bodies | Real deployments must keep customer identifiers out of metric labels too (cardinality and privacy) |

## Out of scope

- Authentication and TLS between demo services (compose network only).
- Supply-chain attacks on images: images are pinned by tag, not digest. CI runs CodeQL and a NuGet vulnerability audit.
- KVKK (Turkish personal data protection law) and GDPR obligations: the demo processes no personal data. A real
  deployment that buckets by customer id should treat that id as personal data in logs and traces.
