# 0005: Bucket by flag key and customer, keep the canary bucket first so raising the weight only adds customers

- Status: accepted
- Date: 2026-11-13

## Context

A canary that reshuffles who is in it on every step is worse than useless for a pricing change: a customer who saw the
new price at 10% and the old one at 25% gets two different totals for the same cart. Each step must be a superset of
the previous one, and a customer's assignment must not change between requests.

flagd's `fractional` operation hashes a bucketing value (by default the flag key concatenated with the targeting key)
with MurmurHash3 into a bucket and assigns variants by walking the listed weights in order
([flagd fractional operation](https://flagd.dev/reference/custom-operations/fractional-operation/)). Assignment is
deterministic for the same input.

## Options considered

1. **Let flagd's implicit default bucketing decide.** Works today, but the behaviour then depends on a default that is
   not visible in the file.
2. **Bucket on the targeting key only.** Every flag would put the same customers into its canary, so the same 10% of
   customers would test every new feature.
3. **Explicit `cat(flagKey, targetingKey)`, canary bucket listed first (chosen).**

## Decision

- `WithCanaryPercent` writes `{"fractional": [{"cat": [{"var": "$flagd.flagKey"}, {"var": "targetingKey"}]}, ["on", p],
  ["off", 100 - p]]}`. An existing bucketing expression in the file (for example bucketing by store instead of customer)
  is kept.
- The canary variant is always the first bucket. Since buckets are assigned in order, raising `p` extends the same range
  of hash values.
- The demo API uses the customer id as the OpenFeature targeting key.

## Consequences

- Verified against real flagd v0.18.2 with 2,000 synthetic customers: everyone on the canary at 10% is still on it at
  25% (integration test `Raising_the_canary_percentage_only_adds_customers`), and the measured shares are within a few
  points of the configured weights.
- Rollback and promotion remove the targeting rule, so a later restart of the canary at the same weight puts the same
  customers back on it. That is usually what an operator wants after a fix.
- Changing the bucketing expression in the middle of a rollout reshuffles everyone. The controller keeps whatever
  expression the file has and never changes it.
