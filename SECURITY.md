# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security -> Report a vulnerability** on this repository. You can expect an acknowledgement within 5 working days. If
the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| Flag file | Only the controller mounts it read-write; flagd reads it read-only. Writes are atomic and refuse to overwrite a concurrent change |
| Controller authority | Never turns a flag on by itself, never re-enables a rolled-back flag, holds still when metrics are missing |
| Query safety | Flag key and variant names are validated at startup, so they cannot inject into PromQL |
| Fault injection | Disabled unless an admin key is configured; constant-time key comparison; inputs validated |
| Containers | Chiseled .NET images, non-root user, no shell; compose binds ports to 127.0.0.1 |
| Audit | Every flag change is recorded with reason, counts and policy in the flag metadata, the log and an audit file |
| Secrets | None in the repository. `.env.example` holds a clearly marked development-only key |
| Known gap | Prometheus' OTLP receiver is unauthenticated in the demo; put an authenticating collector in front of it in production |
