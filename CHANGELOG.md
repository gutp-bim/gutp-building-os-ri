# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and this project follows [Semantic Versioning](https://semver.org/). The canonical
release version is the top-level [`VERSION`](./VERSION) file; a release is cut by
tagging `v<VERSION>` (e.g. `v1.0.0-rc.1`), which `harbor-push.yml` builds and
publishes images for (`v*.*.*`).

## [Unreleased]

_No unreleased changes yet._

## [1.0.0-rc.2] - 2026-07-23

Second release candidate. Consolidates the work merged after the rc.1 preparation commit — a
first-class non-numeric telemetry contract, operator alarms, true gateway connection/pointlist-sync
state, large-scale performance evaluation (up to 50,000 points / 20 gateways and a 100-gateway
reconnect load run), and a documentation reorganization. rc.1 was never tagged; this supersedes it as
the release-candidate baseline.

### Added

- **Non-numeric telemetry values** (`number` / `string` / `boolean`) end-to-end (#152, ADR-0006):
  gRPC keeps field 3 as the numeric value and adds new field numbers for string/boolean (wire-compatible
  with existing numeric gateways); Parquet gains nullable `value_type` / `value_text` / `value_bool`
  columns (old files still readable); API, Hot KV and UI carry the discriminated value. Non-numeric
  history uses last-in-bucket aggregation plus a state timeline (Phase A/B/C: #254 / #255 / #256).
- **Operator value-threshold alarms** on the home + a building-wide alert view (#158 Phase 2 / 2a,
  ADR-0005: #240 / #233).
- **True gateway connected/disconnected state** and **pointlist-sync state** via a shared NATS KV
  heartbeat (#230 Phase 1 / 2b, ADR-0004: #236 / #237); derived last-seen on the gateway view
  (#181 Phase 2: #222).
- **Per-point expected-interval stale detection** with an all-role telemetry-threshold read surface
  (#183: #215 and follow-ups).
- **Demo auth**: demo-only auto-login with a visible skipped-auth banner (#161: #234); the default OSS
  stack unified on Keycloak (#161 案B: #226).
- **Unified notification policy** (transient toast + explanatory inline) and permission-denied /
  gateway-offline control-failure explanations (#162: #232 / #227).
- **Point history chart**: period + granularity selectors, custom date range with start<end / future
  guards (#197: #220).
- **Responsive shell**: off-canvas sidebar drawer on mobile and two-pane stacking on narrow viewports
  (#199: #218-area / #158603a); dialog focus trap + Esc + focus restoration (#198: #204).
- **Operations docs**: Demo / Developer / Production edition definitions (#231), a backup/restore
  runbook for the three stores (#228), and upgrade + incident runbooks (#229) — all #163 slices.
- **Performance evaluation**: automated multi-building sweep to 50,000 points / 20 gateways (#270), a
  100-gateway reconnect load run (#271), a 10k-point gateway point-list optimization (#268), and
  ETag/revision-based avoidance of Twin re-queries for unchanged point lists (#269).
- **Gateway-bridge multi-connection**: supersede a gateway's prior egress stream on reconnect (#211).

### Changed

- **Documentation reorganized by purpose** — `guides/` `architecture/` `operations/` `reference/`
  `project/` `adr/` (#272).
- **Gateway point-list query strategy** optimized for 10k-point twins, backed by shared NATS KV
  point-list revisions (ETag `If-None-Match` → `304` without re-querying OxiGraph) (#268 / #269).
- **Telemetry enum representation**: the numeric-code enum workaround is deprecated in favour of the
  first-class non-numeric value (#152 Phase C: #256).
- **UI foundation**: shared Button/Dialog primitives, tokenized color/state values, and 37 unused UI
  dependencies pruned (#194); legacy detail pages migrated to the new conventions and `/my-resources`
  consolidated into `/resources` (#195).
- **README** now shows real UI screenshots (operator home / resource explorer / point detail),
  regenerable via Playwright (#156 / #224) — supersedes rc.1's "screenshots still pending".
- **Toolchain**: vite 5→8 / vitest 2→4 (#154: #223); observability compose Prometheus scrape aligned
  to 30s (A-9: #274); generated Swagger + Aspida client resynced with the current controllers and the
  admin API moved onto the generated client (B-8: #275).

### Fixed

- batch-latest PostgreSQL authorization coverage, verified with Testcontainers (#265).
- integration test dependency + NATS KV isolation (#258).
- performance harness aligned with the current ingress path (#267).
- freshness: stop exposing the unwired stale multiplier as an editable setting (#210 review).

### Known limitations

- **Warm store**: the Parquet lake is the supported default. **TimescaleDB is opt-in and experimental
  — numeric telemetry only**; the non-numeric `value_type` / `value_text` / `value_bool` fields land in
  the Parquet path, not the TimescaleDB opt-in path.
- **Scale**: validated to 50,000 points / 20 gateways and a 100-gateway concentrated reconnect on a
  **single host**. This is not a Kubernetes / multi-API-replica performance guarantee.
- **Sustained load**: long-duration continuous ingest at 50k (hours of writes + compaction + retention)
  is not yet evaluated (tracked separately).
- **Contract compatibility**: proto/REST/NATS compatibility is enforced by review, not yet by an
  automated `buf breaking` gate.

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
- **Onboarding docs**: `docs/guides/concepts.md` glossary (#160) and a persona-oriented README with a
  demo-first quick start (#156; product screenshots still pending).
- **Testing**: Playwright browser E2E with an E9 "operator usability" axis and axe a11y checks
  (#159), plus a full-stack demo E2E.
- CI: CodeQL, Dependabot, a lightweight external-PR gate (`pr-check.yml`), coverage reporting,
  weekly scheduled integration/golden test runs, and a Swagger/Aspida drift check.
- `docs/project/cost-quality-backlog.md`: cost-optimization and quality-improvement backlog (A-1..A-9,
  B-1..B-10), largely implemented incrementally after the initial readiness review.
- `CODE_OF_CONDUCT.md`, `CODEOWNERS`, `.github/ISSUE_TEMPLATE/`, `.github/PULL_REQUEST_TEMPLATE.md`.

### Changed

- **Warm tier now defaults to the Parquet lake on MinIO; TimescaleDB is opt-in**
  (`WARM_STORE=timescale`), and the default DB image is `postgres:16` (#216 / #234). Breaking for
  deployments that relied on the TimescaleDB warm store — see `docs/architecture/oss-warm-parquet-lake.md`.
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
  context, and other pre-publication findings (see `docs/project/oss-readiness-review.md`).
- Swagger/Aspida type generation was silently broken by a schemaId collision between two
  same-named nested DTOs; fixed, and a CI drift check now guards against regressions.
- Device control modal (`point-control-modal.tsx`) was sending a request body shape the API no
  longer accepts (`{ controlType, body }` instead of `{ value }`) for BACnet points.

## [0.0.0] - Initial public release

Initial OSS release of Building OS: NATS-based ingest → validate → Parquet lake pipeline, OxiGraph
digital twin, Keycloak auth, REST + gRPC API, Next.js dashboard.
