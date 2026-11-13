# Contributing

Thanks for taking the time to improve flags-rollout.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (integration tests start flagd and Prometheus through Testcontainers; `docker compose up` runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                 # must be warning-free: warnings are errors
   dotnet test                  # unit, property, golden and integration tests (Docker running)
   dotnet format --verify-no-changes
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required. They keep the history readable, make the
hand-written CHANGELOG easy to assemble at release time, and show which version bump a change needs:

- `feat:` new capability (minor bump)
- `fix:` bug fix (patch bump)
- `feat!:` or a `BREAKING CHANGE:` footer (major bump)
- `docs:`, `test:`, `build:`, `ci:`, `refactor:`, `perf:`, `chore:` for everything else

## Changing what the controller writes

The flag file is shared with humans and with flagd. A change to `FlagDocument`:

1. regenerates the approved files: `UPDATE_GOLDEN=1 dotnet test --project tests/FlagsRollout.UnitTests`, then review the
   diff in `tests/FlagsRollout.UnitTests/Golden/` like code;
2. keeps `Approved_rewrites_are_valid_flagd_input_and_evaluate_as_intended` green: the integration suite feeds the
   approved files to real flagd.

## Changing a health policy or the state machine

Re-run the lab and update the tables in `docs/benchmarks.md` and ADR 0003 with the new numbers:

```bash
dotnet run -c Release --project benchmarks/FlagsRollout.Lab -- policies 300
```

Never edit a number by hand. If a change makes a scenario worse, say so in the pull request.

## Dependencies

Versions are pinned in `Directory.Packages.props` and updated by hand in reviewed commits. CI fails on any package
with a known vulnerability (`dotnet list package --vulnerable --include-transitive`); fix it with a pinned upgrade,
including transitive pins where needed.

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one. Do not edit the old ADR in
place. Use the existing files as the format (MADR: context, options considered, decision, consequences).
