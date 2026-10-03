# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and this project follows [Semantic Versioning](https://semver.org/). The canonical
release version is the top-level [`VERSION`](./VERSION) file; a release is cut by
tagging `v<VERSION>` (e.g. `v1.0.0-rc.1`), which `harbor-push.yml` builds and
publishes images for (`v*.*.*`).

## [Unreleased]

## [1.0.0-rc.3] - 2026-10-03

Third release candidate. Adds persisted health events (#455) and the `/api/v1` REST versioning
(ADR-0008), switches the local object store to RustFS, and gathers the breaking changes made since rc.2
so they can settle before 1.0.0. Per SemVer these are acceptable only because 1.0.0 is not yet released;
after 1.0.0 they would need a new major API version.

### Upgrading from rc.2

Three changes since rc.2 are breaking (details under "Changed" below):

- **Twin traversal is topology only.** Equipment placed only by the `sbco:floor` literal is no longer in
  any Level/Building. Re-import so it carries `sbco:locatedIn`; the import reports it as `floor_literal_only`.
- **Telemetry read responses carry one union-typed `value`** (`number | string | boolean | null`).
- **`valueText` / `valueBool` are removed** from the telemetry read responses (#359). Read the numeric or
  raw reading from `value`, and the non-numeric reading from the new `state` field — in a mixed aggregate
  bucket `value` is the numeric average and only `state` carries the last string/boolean reading.

Also worth knowing when coming from rc.2:

- **REST paths moved to `/api/v1/…`** (ADR-0008). The old paths keep working — the API rewrites them and
  answers with `Deprecation` / `Link: rel="successor-version"` — so existing clients are not broken, but
  `make openapi-breaking` against the rc.2 spec reports the 68 relocated operations as removed paths.
  Move clients to `/api/v1` within the 6-month window.
- **The local object store is RustFS, not MinIO.** rc.2's `minio/minio` image is no longer pullable and the
  on-disk formats differ: migrate existing data with `scripts/migrate-minio-to-rustfs.sh` before bringing the
  new stack up, and take a backup first. A rollback restores the application images only, not MinIO.

Upgrade order and rollback: [`oss-upgrade-runbook.md`](./docs/operations/oss-upgrade-runbook.md) (API
server before the web client — the web client calls only `/api/v1`). The rc.2 ↔ rc.3 upgrade and rollback
of the application layer was exercised end to end on the Compose stack (§3.5 there).

### Known limitations

- **Parquet lake retention is off by default (#492).** `building-os.minio` is RustFS 1.0.0, which breaks
  `PutObject` / `GetObject` / `HeadObject` once a bucket has any lifecycle (ILM) rule, so
  `LAKE_RETENTION_DAYS` now defaults to `0` (unlimited) in the compose files. Ingestion, compaction and
  queries are unaffected, but **lake objects never expire and disk use grows with ingest** — monitor
  capacity and delete old data by hand: [`oss-lake-retention-runbook.md`](./docs/operations/oss-lake-retention-runbook.md).
  Do not set `LAKE_RETENTION_DAYS` above 0 on RustFS until a stable release containing the upstream fix
  (rustfs/rustfs#8057) is available. Production IaC (`opentofu/modules/minio`) is not affected.
- No `buf breaking` gate for `proto/` yet (REST is covered by `make openapi-breaking`).
- Health-event load at the 12k-point scale has not been measured.

### Added

- Persisted health events (#455): `HealthEvaluatorHostedService` scans the same `PointHealthLedger` as
  `GET /api/v1/telemetry/health` and raises / clears stale / missing / alarm / gateway_offline events in
  the `health_event` table (deadband via `HEALTH_EVALUATOR_RAISE_AFTER_SCANS` / `…_CLEAR_AFTER_SCANS`,
  switch with `HEALTH_EVALUATOR_ENABLED`). `GET /api/v1/health/events` lists them (lifecycle
  `open|cleared` and ack `acked|unacked` are separate filters) and `POST …/{id}/ack` acknowledges one
  (admin / operator, idempotent, audited). UI: the `/health` events tab and "この Point の直近イベント".

- The twin import preview reports `sbco:id` values shared by two or more nodes of one resource type
  (`idCollisionCount` / `idCollisions`), and apply refuses such an import even with `allowOrphans` (#517):
  authorization identifies a node by its business id, so a room `501` in each of two buildings would share
  every `space:501` grant. An append is checked against the existing twin (re-importing the same node is
  not a duplicate; duplicates already in the twin are not blamed on it). The startup seed logs duplicates
  as an ERROR without stopping startup.

- `GET /api/v1/my-resources?idFormat=original&expand=descendants[&targetType=point]` adds the twin
  descendants of what a user can read, down to `targetType`, in one call (#509) — e.g. a Group grant on a
  room yields its equipment and points. Descendants follow exactly the paths the authorization ancestor
  chain follows (verified against `CanAccessAsync` on a real twin), so every returned id is readable.
- `GET /api/v1/my-resources?idFormat=original` (and `…/my-resources/accessible?…&idFormat=original`) returns
  business ids only — a Group member's id, or one the admin UI recorded in the id-mapping table — and lists
  grants whose original id cannot be recovered under `unresolved` / `unresolvedResourceIds` as hashes,
  instead of mixing hashes in (#504). Without `idFormat` (or with `idFormat=hash`) the response is unchanged;
  the new fields are `null` there.
- `/api/v1` paths are lower-kebab throughout (#507): `/api/v1/groups`, `/api/v1/users`,
  `/api/v1/permissions`, `/api/v1/auth` and **`/api/v1/my-resources`** (was `MyResources`). Routing is
  case-insensitive, so the PascalCase spellings keep working; `/api/MyResources` and the briefly published
  `/api/v1/MyResources` are rewritten to `/api/v1/my-resources` with a `Deprecation` header.
- `make openapi-breaking` (`Tools/check-openapi-breaking.bash`) fails on a breaking change to the REST
  API's OpenAPI document versus `origin/main` (oasdiff, pinned image), enforcing ADR-0008's additive-only
  v1 (#507).
- **The REST API is versioned under `/api/v1/…`** (#507, ADR-0008). Every endpoint moved there
  (`/buildings` → `/api/v1/buildings`, `/api/Groups` → `/api/v1/Groups`, `/api/admin/twin` →
  `/api/v1/admin/twin`, …) and OpenAPI lists only the `/api/v1` paths. **The old paths keep working
  unchanged** — they are rewritten to `/api/v1` in-process and the response carries `Deprecation` and a
  `Link: rel="successor-version"` header — so existing clients (web client, gateways polling the point
  list, external applications) need no change yet. Move to `/api/v1`; a removal date for the old paths
  will be announced with a `Sunset` header at least 6 months ahead. The web client already calls `/api/v1` (regenerated
  aspida client and the hand-written fetches), so **upgrade the API server before the web client** and roll
  back in the reverse order — the new web client gets 404s from an API server without `/api/v1`. Not
  versioned: the gateway point list stays at `GET /gateways/{gatewayId}/pointlist`, since it is authenticated
  by a header only the mTLS ingress may set and must not be reachable through the `/api` ingress route.
  Legacy-path traffic is counted in `building_os.api.legacy_requests{root}`.
- `GET /points/{pointId}/control-audit` takes a time range and pages with a cursor (#478): `start`
  (inclusive) / `end` (exclusive) filter `createdAt`, and when more rows exist the response carries
  `X-Next-Cursor`, passed back as `cursor` for the next (older) page. The cursor is a keyset position
  (`createdAt`, `controlId`), so writes recorded while a client pages backwards neither shift nor
  duplicate rows. The body is still the same array, so existing clients are unaffected; the header is
  exposed to cross-origin browser clients.
- `CONTROL_SCHEMA_FAILURE_POLICY=deny` makes control writes fail closed (#481): a write to a point
  whose ControlSchema cannot constrain the value (no schema, no or an unknown `dataType`, unusable
  `enumLabels`) is refused with 400 and a `reason` instead of being sent unvalidated. The default stays
  `allow`. Both modes count such writes in `building_os.control.schema_unresolved{reason,policy}`, and
  the effective policy is logged at startup. Under `deny` an OxiGraph outage also refuses control.

### Changed

- `building-os.minio` in `docker-compose.oss.yaml` is now RustFS instead of MinIO (#489 / #490), since
  `minio/minio` is no longer published. Service name, ports and `MINIO_*` variables are unchanged; the
  on-disk formats are not interchangeable — see `scripts/migrate-minio-to-rustfs.sh` to keep existing data.
  This is what makes the retention limitation below (#492) apply.

- **BREAKING: the twin is traversed by topology only.** Every read path — authorization ancestors,
  building/floor-scoped reads (`/point-details`, `/device-details`, health), search scope, the #291
  import orphan check and the #292 ingress reachability — follows `sbco:hasPart` / `sbco:locatedIn` /
  `sbco:hasPoint` only. The `sbco:floor` literal on equipment is metadata and is **no longer joined to a
  Level's name**: equipment placed only by it (no `sbco:locatedIn`) is in no Level or Building, so
  building/floor/room grants no longer reach it. The admin import reports such equipment with the new
  orphan reason `floor_literal_only`, and the seed logs a warning with the count and examples. Fix the
  twin at its builder by emitting `sbco:locatedIn` to the Room or Level. The name join was ambiguous —
  same-named floors in two buildings matched each other's equipment.
- Authorization ancestors are the **union of every placement** instead of whichever SPARQL row came
  first, so a device located both in a Room and directly on a Level is reachable from either grant
  every time (previously it depended on row order).
- **BREAKING: telemetry read responses now carry one union-typed `value`**
  (`number | string | boolean | null`) instead of requiring clients to reassemble the storage layer's
  discriminated split (#344). Affects `GET /telemetries/query`, the per-tier reads, and
  `POST /telemetries/query/batch-latest`. Previously `value` was always a number or null and a
  non-numeric reading arrived in `valueText`/`valueBool`, so a statically-typed consumer that
  deserializes `value` as a number now fails on the first string or boolean point. `valueType` is
  retained and describes the `value` actually shipped, so it can no longer contradict its runtime
  type. The Parquet lake's column model is unchanged — this is an API-boundary change only.

### Removed

- **Permission wildcards are gone from the dev realm and the docs (#505).** The API never interpreted
  `*` (type and id always matched exactly), so `building:*:read`, `*:*:*` and friends granted nothing:
  the seeded `testoperator` user and the `building-os-operators` / `building-os-viewers` groups could
  read nothing despite being documented as "read + control". They are removed, and the realm ships only
  `admin` / `building-os-admins` (admin is decided by `role=admin`). The API now ignores a wildcard
  entry with a one-time warning instead of returning `"*"` as an id from `GET /api/MyResources`, and the
  web client's `hasPermission` no longer treats `*` as a wildcard. An already imported realm keeps the
  old user and groups; delete them in the Keycloak console if present.

- **BREAKING:** `valueText` and `valueBool` are gone from the telemetry read responses (#359).
  A reading's non-numeric half now travels in the new **`state`** field (`string | boolean | null`).
  It exists for the aggregate bucket, the only row shape carrying two readings at once: `value` is
  the bucket average, so a mixed hour's last-in-bucket state needs its own carrier. Raw non-numeric
  rows repeat their reading in `state` as well, so clients read it with a single lookup.

  **Upgrade the API server before the web client.** The skew is asymmetric. A client newer than its
  server sees no `state` at all and loses the *entire* state timeline, at every granularity — a
  string-only point then renders neither chart nor timeline, with no error. A server newer than its
  client loses only the mixed aggregate bucket, because the old client's fallback ended at `value`,
  which still carries the reading for raw rows and for purely non-numeric buckets.

### Fixed

- Picking the building that is already selected on the operator home no longer empties the floor list
  (it left "フロアなし" until the building was switched and back).
- User management no longer reports a write Keycloak already accepted as failed, nor fails on one user's
  unreadable groups (#532). When the PUT succeeds but the verifying re-read fails (5xx, timeout,
  cancellation), `PATCH /api/v1/users/{id}/attributes` and `POST` / `DELETE …/permissions` answer 200 with
  the user as written, save the resource-id reverse lookup, and audit a success with `verified: false` and a
  warning (was 400 + a failure audit and no mapping). A 403 / 5xx on `users/{id}/groups` or a parent group
  now leaves only that user's group role unknown: the list and detail still return (blank role, warning
  log, `building_os.user_management.group_lookup_failures{reason}`), the lockout guard treats the role as
  possibly admin and never as a remaining admin, and the Admin-API authorization fallback authorizes from
  the user's own attributes (not cached) instead of dropping them to `role=user` with no permissions. The
  admin service account's required `realm-management` roles (`view-users`, `query-groups`,
  `manage-users`) are now documented in `docs/operations/keycloak-admin-provisioning.md`.
- The hierarchy reads and search now authorize nodes by their **business id** (`sbco:id`), the id space
  Group items, telemetry and the ancestor chain use (#504). Previously they matched the dtId (IRI), so a
  user granted e.g. `building:B1` got an empty `/buildings`, and one granted `space:R501` (directly or
  through a Group) could read R501's telemetry yet got 403 on `GET /spaces/{dtId}`, empty
  `/devices` / `/points` below it and an empty search. The same applies to `/spaces/{dtId}/adjacent`,
  `/point-details`, `/device-details` and the metadata write check. Access widens only to what the
  existing grants already name. A grant recorded against a dtId keeps matching during migration (direct
  only; points were always matched by business id). Not changed: a grant on a descendant does not list
  its ancestors, so `space:R501` alone still sees no building or floor in `/buildings` / `/floors`
  (descendant expansion is #509).
- `GET /telemetries/query` now says when it returns only part of the requested range (#499). When a
  parquet read exceeds `PARQUET_QUERY_MAX_FILES` (5000 in the OSS compose stack) the store keeps the
  newest partitions, as before, but the response now carries `X-Partial-Result: true` and
  `X-Covered-From` (ISO-8601 UTC instant from which the data is complete) instead of a silent 200.
  Both headers are exposed to cross-origin browser clients via CORS.
- `/admin` user-attribute follow-ups to #519: an attribute update `PUT`s only `attributes` plus
  `username` / `email` / `firstName` / `lastName`, so it no longer reverts a concurrent disable (or
  `requiredActions` / `emailVerified` change) by another admin; the role is trimmed, a blank role clears
  it, and a role outside `admin` / `operator` / `viewer` is rejected with `400`; the self-lockout /
  last-admin guard counts an admin inherited from a Keycloak group (e.g. `building-os-admins`).
  The role `/admin` shows, the lockout guard counts and the Admin-API fallback authorizes with now mirrors
  the token: the user's own `role` (first value, untrimmed — a whitespace value still shadows the group),
  else the group-derived role, else the legacy `buildingos_role` (the earlier "legacy `buildingos_role`
  wins" rule is withdrawn). A permission-only write leaves `role` and `buildingos_role` exactly as stored;
  only an explicit role write sets `role` and removes `buildingos_role`. A user whose groups carry
  different roles counts as an admin when they are the target (no self-lockout) but not as a remaining
  admin — unless they are the acting admin, whose token settles it — and `/admin` shows the non-admin
  role. Clearing an own `admin` role now resolves the user's group role, so a user who keeps admin through a
  group is no longer falsely refused. Group ancestors are walked by `parentId`, so a group name containing
  `/` (escaped `~/`) is no longer mistaken for a subgroup. An update is re-read after the `PUT`: a write
  none of whose role / permission attributes came back (no `unmanagedAttributePolicy`) now fails with `502`
  (body `{ error, stored }`) and a failure audit instead of `200`; a concurrent change by another admin is
  not reported as a failure, and the response shows what Keycloak holds. Add/remove permission no longer
  reads the user first (one token, read, `PUT`, verify read) and now audits its failures like the other
  writes (`404` / `502` / `400`) instead of an unaudited `500`; the guard is skipped when it cannot
  trigger (enabling, promoting to `admin`, permission-only), resolves only the target unless an admin is
  being disabled/demoted, shares one admin token and group cache across that, and its Keycloak lookups
  are audited on failure. Listing users resolves their groups concurrently (at most 8 at a time). The
  migration `jq` copies a legacy role only verbatim — its first value, when not blank or padded — and
  never over a group role; anything else is left for manual review, because trimming or skipping blanks
  could grant admin to a user who is not one today.
- Roles and permissions granted in `/admin` now reach the access token (#519). The admin UI wrote the
  Keycloak attributes `buildingos_role` / `buildingos_permissions`, but the realm's mappers put `role` /
  `permissions` into the token, so a user who also got `building_os_role` from a group silently lost
  every grant made in `/admin`. The admin UI and the Admin-API fallback now use `role` / `permissions`
  (one definition, `KeycloakUserAttributes`, pinned to `realm.json` by a test), so admin grants are also
  unioned with group `permissions` (#508). The legacy attributes are still read during migration (role:
  own `role`, then the group role, then `buildingos_role`; permissions: the union); `buildingos_permissions`
  is removed on a user's next `/admin` update and `buildingos_role` on their next explicit role change. The realm now sets `unmanagedAttributePolicy: ADMIN_EDIT`: Keycloak 24+ otherwise
  drops undeclared user attributes, so `/admin` writes returned 200 and stored nothing. An `/admin`
  update also no longer clears the user's email / first / last name (Keycloak 24+ treats an
  attribute-bearing `PUT` as a full profile update), which had left the user unable to log in. **An
  already imported realm needs both the policy and the attribute migration applied with `kcadm.sh`** —
  see "Admin UI writes the same attributes" in `docs/operations/keycloak-permission-mapping.md`.
- The `permissions` access-token claim is now the union of the user's attribute and **every** group's
  attribute (`aggregate.attrs=true` on the `building-os-permissions` mapper, #508). Previously a user
  in two or more groups carrying `permissions` received only one group's permissions, and a user with
  their own `permissions` attribute received none of their groups'. Access widens for both kinds of
  user, so review multi-group memberships and per-user `permissions` attributes before upgrading. An already
  imported realm keeps the old mapper config — apply the change with the `kcadm.sh` command in
  `docs/operations/keycloak-permission-mapping.md`.
- Updated the demo and performance gRPC telemetry feeders for the discriminated
  `TelemetryFrame.value_num` contract, restoring live demo data and full-stack UI E2E coverage.
- Made demo E2E authentication handle demo auto-login reliably and isolated route-mocked UI tests
  from developer-local API settings.
- Excluded frontend build artifacts from the Web Docker context.

### Security

- Updated OpenTelemetry packages to 1.17.0 and pinned AWS SDK packages to published versions,
  eliminating vulnerable and non-reproducible NuGet dependency resolution.
- Overrode the vulnerable transitive Snappier version and updated the remaining xUnit 2 test
  projects so the full solution dependency audit reports no known vulnerabilities.

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
