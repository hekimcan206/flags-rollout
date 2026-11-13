# 0004: Fail static without data, make rollback terminal, let humans restart

- Status: accepted
- Date: 2026-11-13

## Context

A controller that changes production on its own has to be explicit about what it does when its inputs are missing or
contradictory. The cases that matter:

1. Prometheus is down or slow, so there are no counts.
2. The canary receives too little traffic to judge (night time, a small segment).
3. The canary was rolled back, the error rate drops (of course: it serves nobody), and the next verdict is "healthy".
4. The controller restarts mid-rollout.
5. A human edits the flag file while the controller is running.

## Options considered

- **Fail closed (roll back when metrics are missing).** Safe for the canary, but every Prometheus restart becomes a
  production change, and an outage of the monitoring stack triggers rollbacks across every running canary at once.
- **Fail open (promote on schedule when metrics are missing).** Unacceptable: it ships unobserved code.
- **Fail static (chosen).** Without evidence, hold: no promotion, no rollback, and say so loudly.

## Decision

- **No metrics**: the tick is skipped, `/status` reports `metricsAvailable: false` with the error, readiness turns
  degraded, and the `rollout.controller.tick_failures` counter increases. The flag stays at its current percentage.
- **Too little traffic**: the policy returns *inconclusive*. Inconclusive never advances a step and never resets the
  breach streak, so a gap in data can neither promote a canary nor erase failures seen just before it.
- **Rollback is terminal.** The controller never re-enables a rolled-back flag, whatever later verdicts say. A
  rolled-back flag is the baseline variant for everyone with no targeting left in the file; starting again means a human
  writes a new canary weight, which the controller detects as an external change.
- **0% is "off", not a step.** The controller never turns a flag on by itself. A rollout starts when a human sets the
  first weight.
- **Restart**: the state is derived from the file (weight plus the `rolloutPhase` metadata). Only the counter window and
  the breach streak are in memory; losing them delays a decision by one window, it never causes one.
- **External edit**: when the file's hash differs from what the controller last wrote, state is re-derived from the file
  and the counter window is cleared, because counts from before the edit describe a rollout that no longer exists.

## Consequences

- During a monitoring outage a broken canary keeps serving its share of traffic. The canary percentage bounds that
  exposure, and the degraded readiness check is the alert hook.
- A canary on a low-traffic flag can sit at its first step indefinitely. That is visible in `/status` (`verdict:
  Inconclusive`, with the request counts) and is the honest outcome; the lab shows that guessing on small samples
  produces 26% false rollbacks.
- Rollback needs a human to restart the rollout, which is deliberate: something was wrong, and someone should look at
  the reason the controller wrote into the flag's metadata and the audit log before trying again.
