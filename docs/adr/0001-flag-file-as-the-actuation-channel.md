# 0001: Change the flag by rewriting flagd's flag file, atomically, with optimistic concurrency

- Status: accepted
- Date: 2026-11-13

## Context

The controller has to change what flagd serves: move the canary from 10% to 25%, promote it, or turn it off for
everyone. flagd itself is read-only. It evaluates flags from *sync sources* (a file, an HTTP URL polled on an interval,
a gRPC sync stream, a Kubernetes `FeatureFlag` resource, object storage) and has no write or management API
([flagd syncs](https://flagd.dev/concepts/syncs/), [`flagd start --help` of v0.18.2](https://flagd.dev/reference/flagd-cli/flagd_start/)).
So "the controller changes the flag" really means "the controller changes a sync source".

Requirements:

1. A rollback must reach every evaluation quickly and must not depend on the controller staying alive afterwards.
2. The flag definition must stay something a human can read, review and edit. The controller manages one flag; the
   same file holds others it must not touch.
3. A change by a human (restarting a canary, fixing a typo) must never be silently overwritten by the controller.
4. flagd must never read a half-written file.

## Options considered

1. **Controller serves the flag JSON over HTTP; flagd polls it (`provider: http`).** The controller becomes the source
   of truth. If it is down, flagd keeps its last good state, which is fine, but humans now edit flags through the
   controller instead of a file, and the polling interval (whole seconds) adds to every rollback.
2. **Controller implements flagd's gRPC sync service.** Push-based and fast, but the controller then owns all flag
   state and must implement a protocol that flagd 0.16 changed (incremental updates, content-hash deduplication). Too
   much surface for a "small controller".
3. **Kubernetes `FeatureFlag` custom resource via the OpenFeature operator.** The production-shaped answer on
   Kubernetes. Out of scope for a `docker compose` demo, and the actuation logic would be the same patch, just through
   the API server.
4. **Rewrite the flag file flagd already syncs from (chosen).** The file is the source of truth for humans and the
   controller alike. It maps directly to a ConfigMap or a GitOps-managed file in production.

## Decision

The controller edits the flag file that flagd reads with `--uri file:/flags/flags.json`:

- **Only three edits**, all in `FlagDocument`: set the canary percentage (a top-level `fractional` rule), promote
  (default variant = canary, targeting removed) and roll back (default variant = baseline, targeting removed). The
  document is edited as a JSON tree, so other flags, `$evaluators` and unknown properties survive byte-for-byte in
  meaning (golden tests in `tests/FlagsRollout.UnitTests/Golden`). Targeting the controller does not understand
  (`if` trees, segments) is refused, not reinterpreted.
- **State in the file.** Each change writes `rolloutPhase`, `rolloutReason` and `rolloutUpdatedAt` into the flag's
  `metadata`, which flagd returns with every evaluation. A restarted controller resumes from the file, and the reason for
  a rollback travels with the flag.
- **Atomic replace.** Write a temporary file in the same directory, flush it to disk, rename it over the original.
- **Optimistic concurrency.** A write carries the SHA-256 of the content it was derived from and is refused if the file
  changed in between. The controller then re-derives its state from the new content on the next tick. The one
  exception is a rollback: it is re-applied to whatever the file says now, because turning the canary off is safe on
  any version and waiting a tick would keep serving failures.

## Consequences

- Measured propagation from rename to flagd serving the new value is 12-996 ms, median 597 ms over 40 changes
  ([benchmarks](../benchmarks.md#flag-propagation)). flagd v0.18.2 watches the file with a stat poller (its log says
  `"sync": "fileinfo"`) at a one-second cadence, so the median is about half a second whatever the controller does.
- The rename is atomic, but the version check before it is not: a human edit that lands in the few milliseconds between
  check and rename is overwritten, and nothing detects it. There is no lock every text editor honours; the README lists
  this as a limitation.
- Kubernetes ConfigMap volumes are updated through a symlink swap and only every kubelet sync period (about a minute by
  default), and `subPath` mounts never update ([Kubernetes ConfigMap update behaviour](https://kubernetes.io/docs/concepts/configuration/configmap/#mounted-configmaps-are-updated-automatically)).
  A production port would patch the `FeatureFlag` resource or the ConfigMap through the API instead of a shared volume.
- The controller needs write access to the flag file and nothing else; flagd mounts the same volume read-only.
