# 0003: Roll back when the canary is both over the error budget and significantly worse than the baseline

- Status: accepted
- Date: 2026-11-13

## Context

The project brief says: auto-disable the flag "when the error rate crosses a threshold". Taken literally, that is one
line of code, and it is wrong in ways that only show up under realistic traffic:

- With few requests, one failure is a large rate (1 of 3 is 33%).
- A downstream outage (database, payment provider) raises the error rate of *both* variants. A fixed threshold blames
  the canary and rolls back a perfectly good change in the middle of an incident, adding a deploy to the confusion.
- The verdict is recomputed every tick over overlapping windows, so a policy is effectively tested hundreds of times per
  rollout, and rare false alarms become likely.

Automated canary analysis systems compare canary against baseline instead of against a constant. Spinnaker's Kayenta
classifies a metric as failing only when a confidence interval of the difference lies outside a tolerance band and the
effect size exceeds the allowed increase ([Kayenta canary judge](https://spinnaker.io/docs/guides/user/canary/judge/)).
Argo Rollouts can delegate the same judgement to Kayenta ([Argo Rollouts Kayenta analysis](https://argo-rollouts.readthedocs.io/en/stable/analysis/kayenta/)).
Error rates are proportions, so this project uses a two-proportion test rather than Kayenta's Mann-Whitney test on time
series.

## Options considered

Four policies, all implemented in `src/FlagsRollout.Core/Analysis` and all compared in the lab
(`benchmarks/FlagsRollout.Lab`, 300 seeds per cell, production `CounterWindow`, policies and `RolloutEngine`):

1. **Naive threshold**: canary error rate above 5%, any sample size, one breach rolls back.
2. **Guarded threshold**: above 5%, at least 100 canary requests in the window, two consecutive breaches.
3. **Relative**: one-sided pooled two-proportion z-test, z > 3.09 (one-sided alpha 0.001), canary at least
   1 percentage point worse, at least 100 requests per variant, two consecutive breaches.
4. **Combined** (chosen): guarded threshold AND relative.

Measured results (simulated seconds; full table in [benchmarks](../benchmarks.md#policy-lab)):

| scenario | naive | guarded | relative | combined |
|---|---:|---:|---:|---:|
| healthy (1% vs 1%, 50 req/s): false rollbacks | 4.3% | 0% | 4.7% | **0%** |
| healthy, 5 req/s: false rollbacks / promoted | 26.3% / 73.7% | 0% / 0% | 0% / 0% | 0% / 0% |
| shared incident (both 12% for 2 min): false rollbacks | 100% | 100% | 3.3% | **0.3%** |
| subtle regression (3% vs 1%): rolled back | 46.7% | 13.7% | 100% | 13.3% |
| bad canary (10% from t=300 s): detection p50 | 30 s | 35 s | 15 s | 35 s |
| broken canary (50%): detection p50 / errors served p50 | 5 s / 75 | 10 s / 138 | 10 s / 133 | 10 s / 138 |

## Decision

The combined policy is the default (`Rollout:Policy = Combined`). The threshold answers "is this bad enough to act
on?" (an error budget a product owner can set), the relative test answers "is it the canary's fault?". Two consecutive
unhealthy verdicts are required before a rollback. `Threshold` and `Relative` remain selectable.

## Consequences

- No false rollback in 300 healthy rollouts and 1 in 300 during a shared incident, where both threshold policies rolled
  back every time.
- A regression that stays under the budget (3% against 1%) is promoted 87% of the time. That is the price of the budget,
  and it is a product decision: lower `MaxErrorRate`, or use `Relative`, which caught 100% of those but also produced
  4.7% false rollbacks on healthy traffic.
- The relative test alone has a 4.7% false-rollback rate although each single test is at alpha 0.001: about 180
  overlapping tests per simulated rollout and the normal approximation at small counts add up. A sequential test (SPRT)
  is listed as a next step instead of being hidden by the AND.
- Low traffic stalls the rollout instead of guessing: at 5 req/s no guarded policy collected 100 canary requests per
  minute at 10%, so nothing was promoted in 15 minutes. The naive policy "promoted" 74% of those rollouts and falsely
  rolled back 26%.
- The two-breach rule costs one tick (2 s in the demo, 5 s in the lab) of extra exposure for a broken canary, visible as
  138 vs 75 errors served in the broken-canary row.
