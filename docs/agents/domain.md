# Domain docs

This repository currently uses a single domain context.

## Before exploring

- Read `CLAUDE.md` for the system architecture, project vocabulary, data flows,
  and coding conventions.
- Read `README.md` for supported deployment and operator-facing behavior.
- Read the ADRs under `docs/adr/` that apply to the area being changed.
- Read a root `CONTEXT.md` if one exists. Its absence is not an error; create it
  only when domain language or boundaries need clarification beyond the existing
  architecture documentation.

## Vocabulary and decisions

- Use established Building OS terms such as Building, Gateway, Equipment, Point,
  Telemetry, Device Control, Digital Twin, and Parquet lake in issue titles and
  behavior-focused test names.
- Do not silently contradict an ADR. Call out the conflict and resolve the design
  decision before implementation.
- If the repository later develops independently owned domain areas, introduce a
  root `CONTEXT-MAP.md` before splitting this configuration into multiple contexts.
