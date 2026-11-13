# 0002: Count requests per served variant, push them as OTLP into Prometheus, compute exact deltas in the controller

- Status: accepted
- Date: 2026-11-13

## Context

To judge a canary the controller needs, for the same time window, how many requests each variant served and how many
of those failed. Three details decide whether that number is trustworthy:

1. **Attribution.** A failure must be charged to the code path that produced it. The configured weight says what share
   of traffic *should* be on the canary; only the service knows which variant a given request actually got (including
   the coded default when flagd is unreachable).
2. **Exactness.** The health policies run significance tests on counts. At 10% of 50 req/s the canary serves about 300
   requests a minute, and an error count off by a few changes the verdict.
3. **Restarts.** Service instances restart and their counters start from zero again.

## Options considered

1. **Prometheus scrapes the service's `/metrics`.** The standard pull model. In .NET it needs either
   `OpenTelemetry.Exporter.Prometheus.AspNetCore` (still a prerelease package) or a second metrics library next to
   OpenTelemetry.
2. **The controller scrapes every service instance itself.** No Prometheus, but the controller then needs service
   discovery and becomes a metrics system.
3. **Services push OTLP to Prometheus' native OTLP receiver (chosen).** Prometheus 3 accepts OTLP metrics at
   `/api/v1/otlp/v1/metrics` when started with `--web.enable-otlp-receiver`
   ([Prometheus OpenTelemetry guide](https://prometheus.io/docs/guides/opentelemetry/)). The services use only the
   stable OpenTelemetry OTLP exporter, the same one that would feed a collector in production.

For reading, PromQL `increase(...[1m])` was rejected: it extrapolates to the window edges and returns fractional counts,
which overstates evidence on small samples.

## Decision

- The demo API records one counter, `rollout.requests`, per request, with the attributes from the OpenTelemetry
  feature-flag semantic conventions (`feature_flag.key`, `feature_flag.result.variant`) plus `outcome`
  (`success`/`error`). The variant label is the one the evaluation returned for that request.
- Metrics are exported every second in the demo (`Otel:MetricExportIntervalMs`), with cumulative temporality, which is
  what Prometheus stores. The OTLP translation (default `UnderscoreEscapingWithSuffixes`) turns this into
  `rollout_requests_total{feature_flag_key, feature_flag_result_variant, outcome, instance}`. The translation strategy is
  pinned in `deploy/prometheus/prometheus.yml` because the controller's query depends on it.
- The controller reads the **raw cumulative values** per `instance`, variant and outcome with one instant query and
  keeps the last window of snapshots. `CounterWindow` sums increments between consecutive snapshots per series and
  treats a value that went down as a restart from zero: the arithmetic of `increase()` without extrapolation. Instances
  are kept apart until after reset handling, because one instance restarting while others grow would hide the reset in
  a sum.

## Consequences

- Counts read back from Prometheus equal what the API served, request for request (integration test
  `Quotes_use_the_engine_the_flag_selects_and_are_counted_exactly_in_Prometheus`).
- Detection latency includes the export interval (up to 1 s) and the controller tick (2 s in the demo).
- A restart that happens and serves more requests than the old counter held between two controller reads is invisible
  to any reader of cumulative values, Prometheus included. It under-counts, it never over-counts (property test
  `Window_counts_equal_true_increments_across_restarts`). One read per tick keeps that window small.
- The Prometheus OTLP receiver has no authentication. In the demo it is reachable only on the compose network and on
  127.0.0.1; see the threat model.
