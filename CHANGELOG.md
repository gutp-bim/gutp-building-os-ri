# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and this project follows [Semantic Versioning](https://semver.org/). The canonical
release version is the top-level [`VERSION`](./VERSION) file; a release is cut by
tagging `v<VERSION>` (e.g. `v1.0.0-rc.1`), which `harbor-push.yml` builds and
publishes images for (`v*.*.*`).

## [Unreleased]

_No unreleased changes yet._

## [1.0.0-rc.1] - 2026-07-17

First release candidate for **v1.0.0**. Consolidates the v1.0.0 readiness work tracked in #184
(external re-evaluation 2026-07-15): an operator-facing UI, a one-command demo, unified telemetry
tiering, and gateway point-list sync — on top of the initial `0.0.0` ingest→lake pipeline.

### Added

- **Operator home** (`/home`, #158): a fresh/stale/missing freshness summary, a worst-first
  "needs attention" list that links to point detail and shows space/device metadata (#179), and an
  admin-only registered-gateway panel (#181 Phase 1). All roles land here after login (#178, #191).
- **Point detail**: freshness badge (#158 Phase 2) and control-command audit history (#162 / #177).
- **Batch latest-sample endpoint** `POST /telemetries/query/batch-latest` (#182 / #189): replaces
  the per-point N+1 the freshness view did; consumed through the generated Aspida client.
- **One-command demo**: `make demo` brings up the OSS stack + web client + telemetry generator with
  an auto-seeded sample twin (`GW-SOS-001`, #124), plus `make doctor` self-diagnosis (#157) and a
  low-frequency `make demo` smoke workflow (#180 / #188).
- **Admin/platform reachability**: gateway / OIDC-client / twin admin screens added to the sidebar
  nav (#192); global `not-found` / `error` / `global-error` recovery pages (#190).
- **Onboarding docs**: `docs/concepts.md` glossary (#160) and a persona-oriented README with a
  demo-first quick start (#156; product screenshots still pending).
- **Testing**: Playwright browser E2E with an E9 "operator usability" axis and axe a11y checks
  (#159), plus a full-stack demo E2E.
- CI: CodeQL, Dependabot, a lightweight external-PR gate (`pr-check.yml`), coverage reporting,
  weekly scheduled integration/golden test runs, and a Swagger/Aspida drift check.
- `docs/cost-quality-backlog.md`: cost-optimization and quality-improvement backlog (A-1..A-9,
  B-1..B-10), largely implemented incrementally after the initial readiness review.
- `CODE_OF_CONDUCT.md`, `CODEOWNERS`, `.github/ISSUE_TEMPLATE/`, `.github/PULL_REQUEST_TEMPLATE.md`.

### Changed

- **Warm tier now defaults to the Parquet lake on MinIO; TimescaleDB is opt-in**
  (`WARM_STORE=timescale`), and the default DB image is `postgres:16` (#216 / #234). Breaking for
  deployments that relied on the TimescaleDB warm store — see `docs/oss-warm-parquet-lake.md`.
- **Gateway point-list sync** (#224): the digital twin is the source of truth and gateways follow
  `GET /gateways/{id}/pointlist` with a content-hash ETag (`If-None-Match` → 304, `?since=` diff, push).
- Gateway telemetry ingress (`GatewayIngress`) and control egress (`GatewayEgress`) split into
  distinct services/ports.
- Post-login landing unified to `/home` for every role; the app title is now "Building OS"
  (previously "…Demo App") (#191 / #193).

### Fixed

- Batch freshness fetch now distinguishes "unavailable" from "no data" and splits >500-id requests
  into server-cap chunks, instead of silently reporting every point as missing (#189).
- Removed the unauthenticated `grpc-test` dev page and its middleware bypass, and the dead
  `WorkspacePlaceholder` component (#193).
- Hardcoded Swagger Basic Auth password, CORS wide-open by default, broken `harbor-push` build
  context, and other pre-publication findings (see `docs/oss-readiness-review.md`).
- Swagger/Aspida type generation was silently broken by a schemaId collision between two
  same-named nested DTOs; fixed, and a CI drift check now guards against regressions.
- Device control modal (`point-control-modal.tsx`) was sending a request body shape the API no
  longer accepts (`{ controlType, body }` instead of `{ value }`) for BACnet points.

## [0.0.0] - Initial public release

Initial OSS release of Building OS: NATS-based ingest → validate → Parquet lake pipeline, OxiGraph
digital twin, Keycloak auth, REST + gRPC API, Next.js dashboard.
