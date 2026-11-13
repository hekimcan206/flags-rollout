# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-11-13

### Added

- Rollout controller: reads per-variant request counters from Prometheus, judges the canary every tick, advances it
  through configurable steps after a bake time, promotes it to 100% and rolls it back after consecutive unhealthy
  verdicts. Status endpoint, health checks, JSON-lines audit log and OpenTelemetry metrics.
- Health policies: fixed threshold with minimum sample size, one-sided two-proportion z-test against the baseline with
  an effect-size floor, and the combined policy (default).
- Exact counter windows from cumulative counters with per-series reset handling (Prometheus `increase()` semantics
  without extrapolation).
- flagd flag-file rewrites (set canary percentage, promote, roll back) that keep other flags, evaluators and the
  bucketing expression intact, record the rollout state in flag metadata, and are written atomically with optimistic
  concurrency.
- Demo checkout API with OpenFeature and the flagd RPC provider, per-variant OTLP metrics following the feature-flag
  semantic conventions, and a key-protected fault-injection endpoint.
- Load generator that starts the canary, breaks it mid-rollout and reports rollback timings as JSON.
- `docker compose up` demo: flagd, Prometheus (OTLP receiver), demo API, controller and load generator.
- Lab: policy comparison over six simulated traffic scenarios (300 seeds each) on the production analysis code, and a
  flag-propagation measurement against real flagd.
- Unit, FsCheck property, golden and Testcontainers integration tests (flagd v0.18.2, Prometheus v3.15.1).
- ADRs 0001-0006, threat model, benchmarks with measured results.
- CI: build, NuGet vulnerability audit, format check, tests with coverage, container images, compose demo; CodeQL.

[Unreleased]: https://github.com/hekimcan206/flags-rollout/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/hekimcan206/flags-rollout/releases/tag/v1.0.0
