# Benchmarks

Three measurements, each answering one question:

| | Question | Method |
|---|---|---|
| [Policy lab](#policy-lab) | Which health policy rolls back bad canaries without rolling back good ones? | Simulated traffic through the production analysis code, 300 seeds per cell |
| [Flag propagation](#flag-propagation) | How long after the controller writes the file does flagd serve the change? | 40 writes against a real flagd, polled every 5 ms |
| [End-to-end rollback](#end-to-end-rollback) | In the running demo, how long from "the new code breaks" to "the controller turns it off", and how many customers see an error meanwhile? | 5 runs of the compose demo's load generator |

Every number below was measured on this machine. Raw outputs are in [`benchmark-results/`](../benchmark-results).

## Environment

| | |
|---|---|
| Host | Windows 11 Home, 13th Gen Intel Core i7-13620H (10 cores, 16 threads), 15.7 GB RAM |
| Docker | Docker Desktop, engine 29.9.1, Linux VM with 16 CPUs and 7.6 GB RAM |
| .NET | SDK 10.0.401, Release builds |
| Images | `ghcr.io/open-feature/flagd:v0.18.2`, `prom/prometheus:v3.15.1`, `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` |
| Host load | Not idle. About 13 containers were running, including another agent's build and test workloads; Windows reported 45% CPU load at the time of the compose runs |

Host load matters most for the end-to-end numbers (request latency, timer jitter). The policy lab is deterministic
per seed and independent of load; the propagation numbers are dominated by flagd's poll interval, not by CPU.

## Policy lab

```bash
dotnet run -c Release --project benchmarks/FlagsRollout.Lab -- policies 300
```

**Method.** Each run simulates 900 seconds of one rollout in one-second steps. Requests arrive as a Poisson process at
the scenario's rate; each request goes to the canary with the current canary share and fails with its variant's true
error rate (a Bernoulli trial). Every 5 simulated seconds the cumulative counters are fed to the production
`CounterWindow` (60 s window), judged by the policy, and passed to the production `RolloutEngine` (steps 10, 25, 50%,
120 s bake, then 100%). A rollback or a promotion takes effect on the next simulated second. The random stream depends
only on the seed and scenario, so all four policies judge the same traffic until their decisions diverge. 300 seeds x
6 scenarios x 4 policies ran in 8.7 s.

What is *not* simulated: metric export lag and flag propagation (both measured below), and anything about latency.

**Policies** (ADR [0003](adr/0003-combined-threshold-and-relative-health-policy.md)):

- naive threshold: canary error rate > 5%, any number of requests, one breach rolls back;
- guarded threshold: > 5%, at least 100 canary requests in the window, two consecutive breaches;
- relative: one-sided two-proportion z-test, z > 3.09, at least 1 point worse, at least 100 requests per variant, two
  consecutive breaches;
- combined: guarded threshold AND relative, two consecutive breaches (the default).

**Results** (300 runs per row; detection is measured from the fault onset, in simulated seconds; "errors served" counts
failed canary responses from the onset until the rollback or the end of the run):

| scenario | policy | rolled back | promoted | detection p50 / p95 (s after onset) | canary errors served after onset (p50) |
|---|---|---:|---:|---:|---:|
| healthy | naive threshold (5%, any n, 1 breach) | 4.3% | 95.7% | - | 0 |
| healthy | guarded threshold (5%, n>=100, 2 breaches) | 0% | 100% | - | 0 |
| healthy | relative (z>3.09, +1 pt, n>=100, 2 breaches) | 4.7% | 95.3% | - | 0 |
| healthy | combined (guarded AND relative) | 0% | 100% | - | 0 |
| healthy-low-traffic | naive threshold (5%, any n, 1 breach) | 26.3% | 73.7% | - | 0 |
| healthy-low-traffic | guarded threshold (5%, n>=100, 2 breaches) | 0% | 0% | - | 0 |
| healthy-low-traffic | relative (z>3.09, +1 pt, n>=100, 2 breaches) | 0% | 0% | - | 0 |
| healthy-low-traffic | combined (guarded AND relative) | 0% | 0% | - | 0 |
| shared-incident | naive threshold (5%, any n, 1 breach) | 100% | 0% | - | 0 |
| shared-incident | guarded threshold (5%, n>=100, 2 breaches) | 100% | 0% | - | 0 |
| shared-incident | relative (z>3.09, +1 pt, n>=100, 2 breaches) | 3.3% | 96.7% | - | 0 |
| shared-incident | combined (guarded AND relative) | 0.3% | 99.7% | - | 0 |
| subtle-regression | naive threshold (5%, any n, 1 breach) | 46.7% | 53.3% | 10 / 95 | 923 |
| subtle-regression | guarded threshold (5%, n>=100, 2 breaches) | 13.7% | 86.3% | 35 / 115 | 957 |
| subtle-regression | relative (z>3.09, +1 pt, n>=100, 2 breaches) | 100% | 0% | 60 / 170 | 11 |
| subtle-regression | combined (guarded AND relative) | 13.3% | 86.7% | 35 / 115 | 957 |
| bad-canary | naive threshold (5%, any n, 1 breach) | 100% | 0% | 30 / 35 | 73 |
| bad-canary | guarded threshold (5%, n>=100, 2 breaches) | 100% | 0% | 35 / 40 | 86 |
| bad-canary | relative (z>3.09, +1 pt, n>=100, 2 breaches) | 100% | 0% | 15 / 25 | 44 |
| bad-canary | combined (guarded AND relative) | 100% | 0% | 35 / 40 | 86 |
| broken-canary | naive threshold (5%, any n, 1 breach) | 100% | 0% | 5 / 10 | 75 |
| broken-canary | guarded threshold (5%, n>=100, 2 breaches) | 100% | 0% | 10 / 15 | 138 |
| broken-canary | relative (z>3.09, +1 pt, n>=100, 2 breaches) | 100% | 0% | 10 / 10 | 133 |
| broken-canary | combined (guarded AND relative) | 100% | 0% | 10 / 15 | 138 |

Scenarios: *healthy* both variants fail 1% of 50 req/s; *healthy-low-traffic* the same at 5 req/s; *shared-incident*
both fail 12% for two minutes from t = 300 s; *subtle-regression* canary 3% vs 1% from the start; *bad-canary* canary
10% from t = 300 s; *broken-canary* canary 50% from t = 300 s.

**Reading it.**

- A fixed threshold cannot tell a bad canary from a bad day: both threshold policies rolled back 100% of healthy
  canaries during the shared incident. Comparing against the baseline brings that to 3.3% (relative) and 0.3%
  (combined).
- Repeated peeking is real. The relative test uses alpha 0.001 per check, yet it falsely rolled back 4.7% of healthy
  canaries: about 180 overlapping checks per rollout, plus the normal approximation at the 10% step's small counts.
  The threshold in the combined policy hides that; a sequential test would fix it (next steps in the README).
- The error budget is a real trade-off. The combined policy lets a 3%-vs-1% regression through 87% of the time, because
  3% is under the 5% budget. The relative policy caught all of them and served 11 errors instead of 957, at the price of
  the 4.7% false rollbacks above.
- Minimum samples stall low-traffic rollouts instead of guessing: at 5 req/s, 10% of traffic is about 30 requests per
  minute, so no guarded policy ever reached 100 requests in the window. The naive policy "decided" anyway and was wrong
  in 26% of runs.
- The second breach costs one tick: 138 vs 75 errors served for a canary failing 50% of requests.
- Detection of the 10% canary takes 35 s with the 60 s window because the window still holds 300 s of healthy history at
  the fault onset; the error rate inside the window has to climb past 5% first. A shorter window detects faster and
  judges smaller samples.

## Flag propagation

```bash
dotnet run -c Release --project benchmarks/FlagsRollout.Lab -- propagation 40
```

**Method.** A flagd container reads the flag file from a bind-mounted host directory, as in the integration tests.
The lab alternates a rollback and a restore 40 times through `FlagFileStore` (temporary file + rename), waits a random
0-1.5 s before each write so writes land at every phase of flagd's file check, then polls flagd's `ResolveBoolean`
endpoint every 5 ms until it returns the new value.

| samples | median | p95 | min | max |
|---:|---:|---:|---:|---:|
| 40 | 597 ms | 978 ms | 12 ms | 996 ms |

flagd v0.18.2 logs its file sync as `"sync": "fileinfo"`: it checks the file's metadata on a one-second cadence
instead of using inotify, on the bind mount and on the compose named volume alike. The spread (12-996 ms, median close to
500 ms) is that poll interval; the rename itself and the evaluation are a few milliseconds.

## End-to-end rollback

```bash
docker compose up -d --build                     # first run happens automatically
for i in 1 2 3 4 5; do docker compose run --rm loadgen | tail -1; done
```

**Method.** The full compose stack: demo API (flagd RPC provider, cache off), flagd, Prometheus with the OTLP receiver,
the controller (tick 2 s, window 20 s, bake 10 s, min 50 requests, combined policy) and the load generator at 100 req/s
from 10,000 synthetic customers. Each run resets the flag file to 10% (playing the operator), waits until the controller
advances to 25%, sets the new engine's failure rate to 30%, and waits for the rollback. Times use the controller's own
decision timestamp; all containers read the same Docker VM clock.

| run | fault -> rollback decision | fault -> last canary response | canary requests after fault | of which failed |
|---:|---:|---:|---:|---:|
| 1 | 6,004 ms | 5,881 ms | 131 | 35 |
| 2 | 4,006 ms | 4,059 ms | 85 | 22 |
| 3 | 6,009 ms | 5,982 ms | 142 | 38 |
| 4 | 4,002 ms | 4,127 ms | 92 | 29 |
| 5 | 4,001 ms | 4,114 ms | 92 | 24 |
| **median** | **4,006 ms** | **4,127 ms** | **92** | **29** |

Total traffic per run: 2,121-2,522 requests, of which 85-142 reached the canary after the fault.

**Reading it.**

- The decision takes two or three ticks: the window (20 s) is diluted by healthy history, so the first tick after the
  fault may still be under 5%, and the policy then needs two unhealthy ticks in a row. 4.0 s and 6.0 s are exactly two
  and three 2-second ticks.
- "Fault -> last canary response" is within about 125 ms of the decision in every run, sometimes slightly before it:
  in this stack the controller's 2 s tick and flagd's 1 s file check happen to keep a fixed phase (about 130 ms apart in
  the logs), and the canary only serves about 25 req/s, so the last canary response can precede the tick timestamp by
  one gap between canary requests. The phase is a coincidence of this run, not a guarantee: with an arbitrary phase,
  add the 12-996 ms measured in [flag propagation](#flag-propagation).
- 29 customers (median) saw a pricing error before the rollback. With the fault at 100% of traffic for the same four
  seconds it would be about four times as many; that number is arithmetic, not a measurement.

## Reproducing

- Policy lab: deterministic per seed; the table above is regenerated byte-for-byte by the command shown.
- Propagation: needs Docker; results vary with the random write phase.
- End-to-end: needs Docker; `benchmark-results/e2e-runs.jsonl` holds the five raw JSON summaries.
