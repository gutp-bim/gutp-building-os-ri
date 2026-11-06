# v1 native bounds: reject responses that cannot be represented as int32

- Status: Accepted for RI #610 (user decision, 2026-10-27).
- Related: #298 (metadata projection), #610, ADR-0008 (API versioning).

## Context

`Point.minPresValue` / `maxPresValue` expose BACnet native bounds as nullable
int32 in v1. Builder RDF can legitimately contain fractional bounds such as
278.15 and 308.15. Previously the mapper silently converted a failed int parse
to null, indistinguishable from an unspecified bound.

The agreed v1 scope is explicit refusal, not rounding or widening the response
field types. Full fractional metadata support is deferred to a future API update.
This supersedes #610's original precision-preserving response acceptance goal
for the immediate v1 fix.

## Decision

- Keep the existing nullable int32 fields and their generated TypeScript types.
- Accept absent bounds as null. Accept numeric lexical forms only when the exact
  mathematical value fits int32 (including 1.0 or 2e1). Do not round, truncate,
  convert through floating point, or silently underflow a tiny fraction to zero.
- Record an internal, JSON-ignored representation flag when either specified
  bound is fractional, malformed, non-finite or outside int32. The mapper remains
  usable and RDF import is unchanged.
- After authentication/authorization, the four v1 reads (`GET /api/v1/points`,
  `/points/{pointId}`, `/point-details`, `/point-details/{pointId}`) refuse an
  otherwise successful response containing that flag with HTTP 409:

```json
{
  "error": "native_bounds_not_representable",
  "message": "The v1 API requires minPresValue and maxPresValue to be int32 values or absent. The response cannot be represented without data loss."
}
```

- A list containing an unrepresentable point fails as a whole; do not return a
  partial list or hide invalid points. The error contains no raw RDF, point IDs,
  configuration values or counts. Existing 401/403/404 outcomes are preserved.
- Do not apply this response check to control POSTs, control audit, health,
  provisioning or import. Native bounds remain descriptive metadata, not
  control-write limits. `ControlSchema`, its safety policy and authorization
  remain authoritative and unchanged.

## Compatibility and validation

This is an explicitly agreed v1 error-behavior correction to ADR-0008's general
status/behavior stability policy: a request that previously succeeded with
incorrect null metadata can now fail with 409. It is not a claim that all v1
consumers remain behaviorally unaffected. Clients must treat this as an error,
not as an empty result or missing bounds. No new API version or widened field is
introduced; the 409 response is documented in OpenAPI.

Contract tests exercise real OxiGraph, JWT validation, controllers and MVC result
filters for integer, absent, fractional, invalid, range and precision cases.
They cover all four reads, list refusal and authorization preceding the error.
The existing Builder CSV/RDF fixture verifies three fractional points are refused,
while BPT-0035's 0/1 bounds remain readable and all 35 points remain imported.
A control test uses the real RDF ControlSchema resolver and a test-only command
publisher to verify schema-based acceptance/rejection remains unchanged.
Production Keycloak, external BDE/UI and real device writes are not exercised.

Future fractional metadata support must define API/client compatibility and
precision together; this decision does not preselect its wire format or version.
