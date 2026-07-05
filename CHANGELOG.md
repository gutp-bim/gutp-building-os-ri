# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project intends to adopt [Semantic Versioning](https://semver.org/) once
the first tagged release (`v0.1.0`) is cut. `harbor-push.yml` already builds and
publishes images on `v*.*.*` tags.

## [Unreleased]

### Fixed

- Hardcoded Swagger Basic Auth password, CORS wide-open by default, broken `harbor-push` build
  context, and other pre-publication findings (see `docs/oss-readiness-review.md`).
- Swagger/Aspida type generation was silently broken by a schemaId collision between two
  same-named nested DTOs; fixed, and a CI drift check now guards against regressions.
- Device control modal (`point-control-modal.tsx`) was sending a request body shape the API no
  longer accepts (`{ controlType, body }` instead of `{ value }`) for BACnet points.

### Added

- CI: CodeQL, Dependabot, a lightweight external-PR gate (`pr-check.yml`), coverage reporting,
  weekly scheduled integration/golden test runs, and a Swagger/Aspida drift check.
- `docs/cost-quality-backlog.md`: cost-optimization and quality-improvement backlog (A-1..A-9,
  B-1..B-10), largely implemented incrementally after the initial readiness review.
- `CODE_OF_CONDUCT.md`, `CODEOWNERS`, `.github/ISSUE_TEMPLATE/`, `.github/PULL_REQUEST_TEMPLATE.md`.

## [0.0.0] - Initial public release

Initial OSS release of Building OS: NATS-based ingest → validate → Parquet lake pipeline, OxiGraph
digital twin, Keycloak auth, REST + gRPC API, Next.js dashboard.
