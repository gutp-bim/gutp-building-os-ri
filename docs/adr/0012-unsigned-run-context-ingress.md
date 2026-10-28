# 0012 Unsigned, fail-closed run context ingress (simulation scope)

Status: Proposed. Supersedes the Ed25519 signature requirement of the signed run
context ingress introduced in 936a787. Mirrors Nexus ADR-0010 (unsigned run context).

## Decision

The testbed is a simulation. The threat model is accidental run mixing (stale run,
generation rollback, expired context, wrong gateway/clock), not a forged `run_id`.
Forgery resistance is out of scope and is not claimed. The envelope therefore loses
its `signature`; `RunContextVerifier` no longer verifies signatures and the
`BouncyCastle.Cryptography` dependency is removed.

## Contract

- Opt-in via `GRPC_INGRESS_RUN_CONTEXT_TRUST_FILE`. Trust entries: `gatewayId`,
  `clockId`, `envelopeFile` (no public key). Envelope: `{ "payload": <base64> }`;
  unknown fields (including a leftover `signature`) are rejected at load.
- Payload schema `clock-run-context/2-draft` is unchanged and still validated:
  exact field set, scope match, generation 1..2^53-1, status, issued/validFrom/expiresAt.
- Per frame: mTLS-bound gateway identity, scope, provisioning revision, run id,
  generation, commit <= apply <= poll start, whole poll interval inside the lifetime
  (nanosecond exact). Frames with no run claim (absent) are unchanged.
- Fail closed: if a frame carries a run claim (`sim_run_status` other than `absent`,
  or `sim_run_id` / `sim_context_generation`) and no verifier is configured, the frame
  is rejected (`run_context_unconfigured`), never passed through as valid.
- `run_id` remains an annotation, never an authentication or authorization input.
  mTLS, identity binding and Safety/BOS-write boundaries are unchanged.

## Compatibility and rollback

Deployments that do not set the trust file and whose gateways emit `sim_run_*`
claims will now have those frames rejected; unset the claim on the gateway side or
configure the trust file. Rollback: revert this change (signed variant in 936a787);
do not share a ledger across signed and unsigned formats.

## Verification scope

Unit tests only (verifier, ingress configured-match / mismatch / unconfigured, DI
resolution). No live Nexus -> RI -> BDE run.
