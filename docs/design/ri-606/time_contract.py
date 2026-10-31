"""Offline contract oracle, not an ingress handler, authenticator, or persistent run registry.

Receiver evidence is injected by tests. Flags in telemetry/context payloads cannot establish trust.
Replay must use the snapshot and receiver state recorded at acquisition, not today's active run.
"""
import copy
import re
from datetime import datetime, timedelta, timezone
from decimal import Decimal, InvalidOperation, localcontext

PROFILE = 'ri-clock-observation/2-draft'
NEXUS_PROFILE = 'nexus-bc1a9e5137fec041df250e4f927299eaeb5d9a0f'
LEGACY_RI_PROFILE = 'ri-6e0fefee8be2caaa33608d76845cbab2841cf9dd'
RUN_SCHEMA = 'clock-run-context/2-draft'
QUALITIES = {'stable_unfenced', 'changed', 'missing', 'reset'}
RUN_FIELDS = {'schemaVersion', 'gatewayId', 'clockId', 'provisioningRevision',
              'runId', 'generation', 'status', 'issuedAt', 'validFrom', 'expiresAt'}
NUMBER = re.compile(r'-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\Z')
UTC = re.compile(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?P<fraction>\.\d+)?(?:Z|\+00:00)\Z')


class ContractError(ValueError):
    pass


def require(condition, reason):
    if not condition:
        raise ContractError(reason)


def utc(value):
    require(isinstance(value, str), 'invalid_utc')
    match = UTC.fullmatch(value)
    require(match is not None, 'invalid_utc')
    fraction = match.group('fraction') or ''
    whole = value.replace(fraction, '', 1) if fraction else value
    try:
        result = datetime.fromisoformat(whole.replace('Z', '+00:00'))
    except ValueError as error:
        raise ContractError('invalid_utc') from error
    require(result.utcoffset() == timedelta(0), 'invalid_utc')
    # Preserve RFC3339 fractional precision instead of truncating at datetime's microseconds.
    elapsed = result - datetime(1970, 1, 1, tzinfo=timezone.utc)
    with localcontext() as precision:
        precision.prec = len(value) + 20
        return Decimal(elapsed.days * 86400 + elapsed.seconds) + Decimal('0' + fraction)


def seconds(value):
    require(isinstance(value, str) and NUMBER.fullmatch(value), 'invalid_sim_time')
    try:
        number = Decimal(value)
    except InvalidOperation as error:
        raise ContractError('invalid_sim_time') from error
    require(number.is_finite() and number >= 0, 'invalid_sim_time')
    return number


def validate_context(context):
    require(isinstance(context, dict) and set(context) == RUN_FIELDS, 'invalid_context_fields')
    require(context['schemaVersion'] == RUN_SCHEMA, 'unknown_context_schema')
    for key in ('gatewayId', 'clockId', 'provisioningRevision', 'runId'):
        require(isinstance(context[key], str) and context[key].strip(), 'invalid_context_identity')
    # bool is an int subclass in Python; never accept true as generation 1.
    require(type(context['generation']) is int and 0 < context['generation'] <= 9007199254740991,
            'invalid_generation')
    require(context['status'] in ('active', 'revoked'), 'invalid_context_status')
    issued, start, end = (utc(context[k]) for k in ('issuedAt', 'validFrom', 'expiresAt'))
    require(issued <= start < end, 'invalid_context_interval')


def acquisition_quality(acquisition):
    start, end = utc(acquisition['startedAt']), utc(acquisition['endedAt'])
    require(start <= end, 'invalid_acquisition_interval')
    chunks = acquisition['chunkCount']
    require(type(chunks) is int and chunks > 0, 'invalid_chunk_count')
    samples = acquisition['clockSamples']
    require(isinstance(samples, list) and len(samples) == chunks + 1, 'incomplete_clock_boundaries')
    require(type(acquisition['resetLatched']) is bool and type(acquisition['contextChanged']) is bool,
            'invalid_acquisition_evidence')
    # None represents any failed/invalid read (including a unit probe failure).
    values = [None if v is None else seconds(v) for v in samples]
    previous = acquisition.get('previousClockValue')
    previous = None if previous is None else seconds(previous)
    reset = acquisition['resetLatched']
    for value in values:
        if value is not None:
            reset = reset or (previous is not None and value < previous)
            previous = value
    if reset:
        return 'reset', None
    if any(v is None for v in values):
        return 'missing', None
    if any(v != values[0] for v in values):
        return 'changed', None
    return 'stable_unfenced', values[0]


def context_state(snapshot, clock_id, context, acquisition, receiver):
    if context is None:
        return 'absent', None
    validate_context(context)
    receiver = receiver or {}
    if receiver.get('authenticated') is not True:
        return 'unverified', 'unauthenticated_context'
    if receiver.get('issuerAuthorized') is not True:
        return 'unverified', 'issuer_not_authorized'
    if context['gatewayId'] != snapshot['gatewayId'] or context['clockId'] != clock_id:
        return 'invalid', 'scope_mismatch'
    if context['provisioningRevision'] != snapshot['revision']:
        return 'invalid', 'revision_mismatch'
    if context['status'] == 'revoked':
        return 'invalid', 'revoked'
    if utc(acquisition['startedAt']) < utc(context['validFrom']):
        return 'invalid', 'not_yet_valid'
    if utc(acquisition['endedAt']) >= utc(context['expiresAt']):
        return 'invalid', 'expired'
    if acquisition['contextChanged']:
        return 'invalid', 'context_changed'
    if receiver.get('historyRecovered') is not True:
        return 'unverified', 'generation_history_unavailable'
    committed = receiver.get('committedContext')
    if committed is None:
        return 'unverified', 'context_not_committed'
    validate_context(committed)
    require((committed['gatewayId'], committed['clockId']) == (snapshot['gatewayId'], clock_id),
            'invalid_receiver_scope')
    if context['generation'] < committed['generation']:
        return 'invalid', 'stale_generation'
    if context['generation'] == committed['generation'] and context != committed:
        return 'invalid', 'generation_conflict'
    if context['generation'] > committed['generation']:
        return 'unverified', 'context_not_committed'
    if receiver.get('appliedAt') is None:
        return 'unverified', 'context_not_applied'
    if max(utc(receiver['committedAt']), utc(receiver['appliedAt'])) > utc(acquisition['startedAt']):
        return 'invalid', 'context_not_active_at_start'
    quality, _ = acquisition_quality(acquisition)
    if quality == 'reset' or context['runId'] in receiver.get('invalidatedRunIds', []):
        return 'invalid', 'reset_detected'
    return 'valid', None


def next_context_state(previous, context, invalidated_run_ids=()):
    """Pure monotonicity check for an already-authenticated candidate; does NOT persist/activate it.

    Recovery/authorization, initial watermark establishment and durable commit remain external.
    The caller cannot treat this return value as proof of a completed durable commit.
    """
    validate_context(context)
    require(previous is not None, 'generation_history_unavailable')
    validate_context(previous)
    require((context['gatewayId'], context['clockId']) == (previous['gatewayId'], previous['clockId']),
            'scope_mismatch')
    require(context['generation'] >= previous['generation'], 'stale_generation')
    if context['generation'] == previous['generation']:
        require(context == previous, 'generation_conflict')
        return copy.deepcopy(previous)
    require(context['status'] != 'active' or context['runId'] not in invalidated_run_ids, 'reset_run_reuse')
    return copy.deepcopy(context)


def validate_observation(snapshot, frame, acquisition, context=None, receiver=None, profile=PROFILE):
    require(profile in (PROFILE, NEXUS_PROFILE, LEGACY_RI_PROFILE), 'unknown_profile')
    attrs = frame['attributes']
    require(isinstance(attrs, dict) and all(isinstance(v, str) for v in attrs.values()),
            'attributes_must_be_strings')
    require(frame['gateway_id'] == snapshot['gatewayId'], 'frame_gateway_mismatch')
    point = next((p for p in snapshot['points'] if p['pointId'] == frame['point_id']), None)
    require(point is not None, 'point_missing')
    clock_id = attrs.get('sim_clock_id')
    clock = next((c for c in snapshot['clockSources'] if c['clockId'] == clock_id), None)
    require(clock is not None and clock['native']['gatewayId'] == frame['gateway_id'], 'clock_scope_mismatch')
    require(any(b['clockId'] == clock_id and b['equipmentId'] == point['device']['dtId']
                for b in snapshot['clockBindings']), 'point_clock_mismatch')
    canonical, alias = attrs.get('sim_config_revision'), attrs.get('sim_clock_revision')
    require(not (canonical is not None and alias is not None and canonical != alias), 'revision_alias_conflict')
    if profile in (PROFILE, LEGACY_RI_PROFILE):
        require(canonical is not None and alias is None, 'canonical_revision_required')
    revision = canonical if canonical is not None else alias
    require(revision == snapshot['revision'], 'frame_revision_mismatch')
    observed = utc(frame['timestamp'])
    require(utc(acquisition['startedAt']) <= observed <= utc(acquisition['endedAt']), 'timestamp_outside_acquisition')
    require('sim_step_id' not in attrs, 'step_fence_unproved')
    quality = attrs.get('sim_time_quality')
    if profile in (PROFILE, NEXUS_PROFILE):
        require(quality in QUALITIES, 'unknown_acquisition_quality')
        expected_quality, expected_time = acquisition_quality(acquisition)
        require(quality == expected_quality, 'acquisition_quality_mismatch')
        if quality == 'stable_unfenced':
            require('sim_time_s' in attrs and seconds(attrs['sim_time_s']) == expected_time, 'stable_time_mismatch')
        else:
            require('sim_time_s' not in attrs, 'unstable_time_present')
    else:
        # Endpoint bracketing alone does not prove Nexus's all-chunk boundary coverage.
        require(quality in ('bracketed', 'unavailable', 'verified'), 'unknown_legacy_quality')
        if quality == 'unavailable':
            require('sim_time_s' not in attrs, 'unstable_time_present')
        elif 'sim_time_s' in attrs:
            seconds(attrs['sim_time_s'])

    if profile == PROFILE:
        state, reason = context_state(snapshot, clock_id, context, acquisition, receiver)
        require(attrs.get('sim_run_status') == state, 'run_status_mismatch')
        require(attrs.get('sim_run_reason') == reason, 'run_reason_mismatch')
        if state == 'valid':
            require(attrs.get('sim_run_id') == context['runId'] and
                    attrs.get('sim_context_generation') == str(context['generation']), 'run_identity_mismatch')
        else:
            require('sim_run_id' not in attrs and 'sim_context_generation' not in attrs, 'untrusted_run_identity')
    elif profile == NEXUS_PROFILE:
        require(context is None and not any(k in attrs for k in
                ('sim_run_status', 'sim_run_reason', 'sim_run_id', 'sim_context_generation')), 'unexpected_legacy_run_claim')
        state, reason = 'absent', None
    else:
        require(context is None, 'legacy_context_upgrade_unproved')
        state, reason = ('unverified', 'legacy_run_claims') if any(k in attrs for k in
                        ('sim_run_id', 'sim_context_generation')) else ('absent', None)

    # Return an interpretation; never rewrite the durable event or its original attributes.
    return {'quality': quality, 'revision': revision, 'runStatus': state, 'runReason': reason,
            'runTimeEligible': profile == PROFILE and state == 'valid' and quality == 'stable_unfenced',
            'strictStepEligible': False}
