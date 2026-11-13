# 0006: Test against real flagd and Prometheus, compare policies in a simulation that runs production code

- Status: accepted
- Date: 2026-11-13

## Context

The risky parts of this project are integration details and statistics, not business logic:

- Does flagd accept the files the controller writes, and does it evaluate them as intended?
- Does the OTLP-to-Prometheus translation produce the labels the controller queries, with exact counts?
- How often does a policy roll back a healthy canary, and how fast does it catch a bad one? A handful of example-based
  tests cannot answer a question about rates.

## Options considered

1. **Mocks for flagd and Prometheus.** Fast, and they would encode exactly the assumptions that need checking.
2. **Only the compose demo as an end-to-end check.** Real, but one scenario, slow to iterate, and it does not measure
   rates.
3. **Layers (chosen):** unit, property and golden tests for the pure core; Testcontainers integration tests against the
   real images; a simulation lab for rates; the compose demo as the user-facing proof, asserted in CI.

## Decision

- **Unit and property tests** (FsCheck) for `CounterWindow`, the policies, `RolloutEngine` and `FlagDocument`. Properties
  cover what must hold for every input: counts never negative and never over-counted across restarts, the relative
  policy never blames a canary that fails less often, rewrites round-trip the percentage, and the state machine never
  moves backwards except to 0 and never acts after a rollback.
- **Golden files** for the three flag rewrites, reviewed like code (`UPDATE_GOLDEN=1` regenerates them). The
  integration suite feeds the same approved files to real flagd.
- **Integration tests** start `ghcr.io/open-feature/flagd:v0.18.2` and `prom/prometheus:v3.15.1` once per run
  (assembly fixture) with the configuration compose uses, host the demo API in-process with `WebApplicationFactory`,
  and run the real controller against them. Test classes run serially because they share the flag file.
- **The lab** (`benchmarks/FlagsRollout.Lab policies`) simulates only the traffic (Poisson arrivals, Bernoulli
  failures, deterministic per seed) and runs it through the production `CounterWindow`, policies and `RolloutEngine`.
  Its `propagation` mode measures file-to-flagd latency against a real flagd.
- **CI** runs all of the above except the lab, builds the images and runs the compose demo, asserting the load
  generator's JSON verdict.

## Consequences

- The lab's times are simulated seconds with zero metric and flag propagation delay; real delays are measured
  separately (propagation lab, compose runs) and documented next to the simulated numbers.
- The integration suite needs Docker and pulls two small images (flagd about 180 MB, Prometheus about 380 MB uncompressed).
- A flagd release that changes evaluation or file handling fails the integration suite rather than the demo.
