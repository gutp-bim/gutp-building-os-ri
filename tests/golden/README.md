# Golden File Tests

> **Status (2026-11): reference material only — nothing checks these files automatically.**
> They were written as the acceptance criteria for the Azure → OSS migration and have not changed
> since. The test project that compared code output against them (`BuildingOS.Functions.Test`, see
> "Running the tests" below) no longer exists, so the `golden-tests` workflow matched zero tests and
> was removed, as was `parity-harness`'s self-consistency job (it compared each file with itself).
> What these files describe is covered elsewhere: connector normalisation and control handling by the
> unit tests in `DotNet/BuildingOS.Shared.Test`, hierarchy traversal by the resolver unit and
> integration tests, and API response shapes by `swagger.yaml` plus `make openapi-breaking`.
> To compare real output by hand: `cd Tools/parity-harness && dotnet run -- golden --golden-dir
> ../../tests/golden/connector --actual-dir <dir with your JSON>`. The sections below describe the
> original setup and are kept for history.

This directory contains contract snapshots that define the **acceptance criteria** for OSS migration.
Phase 1 onwards: every OSS replacement must produce output matching these snapshots.

## Directory layout

```
tests/golden/
├── api/            # JSON Schema contracts for API responses (manual, spec-driven)
├── adt/            # Snapshots of DigitalTwinHierarchyResolver output (mock-based)
├── connector/      # Snapshots of Connector output JSON (mock-based, per connector)
└── control/        # Snapshots of PointControlConnector flow state transitions
```

## Running the tests

```bash
# Verify snapshots (normal CI run)
cd DotNet
dotnet test BuildingOS.Functions.Test --filter "FullyQualifiedName~Golden"

# Regenerate snapshots after an intentional contract change
UPDATE_GOLDEN=1 dotnet test BuildingOS.Functions.Test --filter "FullyQualifiedName~Golden"
```

## Updating snapshots

Only update when the change is **intentional**:

1. Make the code change
2. Run `UPDATE_GOLDEN=1 dotnet test ...` to regenerate affected snapshots
3. Review the diff with `git diff tests/golden/`
4. Commit both the code change and the updated snapshot together

Do **not** update snapshots to silence test failures from unintended regressions.

## Dynamic fields

The `id` field in connector output contains a Unix timestamp (`{pointId}.{unix_ms}`).
The `GoldenFile` helper normalizes it to `{pointId}.<timestamp>` before comparison,
so snapshots are deterministic.

## API contracts

Files in `tests/golden/api/` are JSON Schema documents that define expected API response
shapes. They are used as specifications for OSS parity testing — the OSS API server must
return responses that validate against these schemas.
