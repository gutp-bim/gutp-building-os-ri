"""Review fixture checks only; does not import, provision, or contact any service."""
import copy
import hashlib
import json
from pathlib import Path

from pyshacl import validate
from rdflib import Graph, Literal, Namespace, RDF, URIRef

from time_contract import ContractError, next_context_state, validate_observation

ROOT = Path(__file__).parent / 'fixtures'
BOS = Namespace('http://buildingos.gutp.jp/ontology#')
SBCO = Namespace('https://www.sbco.or.jp/ont/')
EX = Namespace('https://example.com/ri-606/')
SHAPES = Graph().parse(ROOT / 'clock-shapes.ttl')


def check(condition, message):
    if not condition:
        raise ValueError(message)


def conforms(graph):
    return validate(graph, shacl_graph=SHAPES, inference='none')[0]


def one(graph, subject, predicate):
    values = list(graph.objects(subject, predicate))
    check(len(values) == 1, f'Expected one {predicate}')
    return values[0]


def digest(snapshot):
    content = {k: v for k, v in snapshot.items() if k not in ('revision', 'generatedAt')}
    data = json.dumps(content, sort_keys=True, separators=(',', ':'), ensure_ascii=False)
    return '"sha256:' + hashlib.sha256(data.encode()).hexdigest() + '"'


def check_snapshot(s):
    check(s['schemaVersion'] == 'clock-provisioning/1-draft', 'Unknown schema')
    check(s['revision'] == digest(s), 'Content hash mismatch')
    points = s['points']
    check([p['pointId'] for p in points] == [f'BPT-{n:04d}' for n in range(2, 37)], '35 stable IDs')
    check(sum(p['writable'] is True for p in points) == 4, '4 writable')
    clocks = {c['clockId']: c for c in s['clockSources']}
    check(len(clocks) == len(s['clockSources']) == 1, 'Duplicate/missing clock')
    bindings = {b['equipmentId']: b['clockId'] for b in s['clockBindings']}
    check(len(bindings) == len(s['clockBindings']) == 2, 'Duplicate/missing equipment binding')
    for c in clocks.values():
        check(set(c) == {'clockId', 'clockDomain', 'unit', 'readOnly', 'acquisitionMode', 'native'}, 'Static fields')
        check((c['clockDomain'], c['unit'], c['readOnly'], c['acquisitionMode']) == ('simulation', 's', True, 'poll'), 'Clock semantics')
        native = c['native']
        check(native['gatewayId'] == s['gatewayId'], 'Gateway ownership')
        check(native['protocol'] == 'bacnet' and native['property'] == 'presentValue' and native['unit'] == 'seconds', 'Native semantics')
    for p in points:
        eq = p['device']['dtId']
        check(eq in bindings and bindings[eq] in clocks, 'Unresolved binding')
        check(p['pointId'] not in clocks, 'Clock mixed into points')


def check_time_cases(snapshot):
    """Run explicit fixture scenarios with trusted evidence separate from the telemetry payload."""
    def read(name):
        return json.loads((ROOT / name).read_text())

    suite = read('time-contract-cases.json')
    accepted = rejected = 0
    names = set()
    for case in suite['cases']:
        name = case['name']
        check(name not in names, f'Duplicate time case: {name}')
        names.add(name)
        frame = read(case.get('frame', 'telemetry.json'))
        frame.update(case.get('framePatch', {}))
        frame['attributes'].update(case.get('attributes', {}))
        for key in case.get('omitAttributes', []):
            frame['attributes'].pop(key, None)
        acquisition = read(suite['acquisitionEvidence'])
        acquisition.update(case.get('acquisitionPatch', {}))
        context = read(case['context']) if 'context' in case else None
        receiver = read(suite['receiverEvidence']) if context is not None else None
        if context is not None:
            context.update(case.get('contextPatch', {}))
            receiver.update(case.get('receiverPatch', {}))
            if 'committedContextPatch' in case:
                receiver['committedContext'].update(case['committedContextPatch'])
        original = copy.deepcopy((snapshot, frame, acquisition, context, receiver))
        try:
            result = validate_observation(snapshot, frame, acquisition, context, receiver,
                                          case.get('profile', suite['profile']))
        except ContractError as error:
            check(str(error) == case.get('expectedError'), f'{name}: unexpected rejection {error}')
            rejected += 1
        else:
            check('expectedError' not in case, f'{name}: expected rejection')
            for key, value in case['expected'].items():
                check(result[key] == value, f'{name}: {key} = {result[key]!r}, expected {value!r}')
            # Replay is assessed using the same recorded acquisition-time snapshot/state.
            check(result == validate_observation(snapshot, frame, acquisition, context, receiver,
                                                 case.get('profile', suite['profile'])), f'{name}: replay changed')
            accepted += 1
        check(original == (snapshot, frame, acquisition, context, receiver), f'{name}: mutated original event/state')

    run = read('run-context.json')
    original = copy.deepcopy(run)
    check(next_context_state(run, run) == run, 'Same generation/content must be idempotent')
    reordered = dict(reversed(list(run.items())))
    check(next_context_state(run, reordered) == run, 'JSON key order must not create a generation conflict')
    renewed = dict(run, generation=run['generation'] + 1)
    check(next_context_state(run, renewed) == renewed, 'Higher generation can be staged')
    new_run = dict(renewed, runId='example-run-002')
    check(next_context_state(run, new_run, [run['runId']]) == new_run, 'Reset recovery needs new run ID')
    revoked = dict(renewed, status='revoked')
    check(next_context_state(run, revoked, [run['runId']]) == revoked, 'Revocation can advance generation')
    transitions = [
        ('no durable history', None, run, [], 'generation_history_unavailable'),
        ('stale generation', renewed, run, [], 'stale_generation'),
        ('same generation/content conflict', run, dict(run, runId='different'), [], 'generation_conflict'),
        ('reset cannot be cleared by renewal', run, renewed, [run['runId']], 'reset_run_reuse'),
        ('scope transfer', run, dict(renewed, gatewayId='GW-OTHER'), [], 'scope_mismatch'),
    ]
    for name, previous, candidate, invalidated, expected in transitions:
        try:
            next_context_state(previous, candidate, invalidated)
        except ContractError as error:
            check(str(error) == expected, f'{name}: unexpected transition rejection {error}')
        else:
            raise ValueError(f'{name}: transition should be rejected')
    check(original == run, 'Context transition mutated committed state')
    return accepted, rejected, 5 + len(transitions)


def main():
    graph = Graph().parse(ROOT / 'model.ttl')
    check(conforms(graph), 'Positive SHACL fixture must conform')
    snapshot = json.loads((ROOT / 'provisioning.json').read_text())
    check_snapshot(snapshot)
    accepted, rejected, transitions = check_time_cases(snapshot)
    check(len(set(graph.subjects(RDF.type, SBCO.PointExt))) == 35, 'RDF point count')
    check(len(set(graph.subjects(RDF.type, BOS.ClockSource))) == 1, 'RDF clock count')
    check(len(list(graph.triples((None, BOS.usesClockSource, None)))) == 2, 'RDF binding count')
    for p in snapshot['points']:
        subject = EX[p['pointId']]
        check(str(one(graph, subject, SBCO.id)) == p['pointId'], 'Canonical ID mapping')
        for rdf_key, json_value in [(SBCO.gatewayId, snapshot['gatewayId']), (BOS.protocol, p['protocol']), (SBCO.unit, p['unit']), (SBCO.deviceIdBacnet, p['native']['deviceId']), (SBCO.objectTypeBacnet, p['native']['objectType']), (SBCO.instanceNoBacnet, p['native']['instanceNo'])]:
            check(str(one(graph, subject, rdf_key)) == json_value, 'Point field mapping')
        check(one(graph, subject, SBCO.writable).toPython() == p['writable'], 'Writable mapping')
        check((URIRef(p['device']['dtId']), SBCO.hasPoint, subject) in graph, 'Equipment mapping')
        if p['writable']:
            for key in ('dataType', 'minValue', 'maxValue'):
                check(str(one(graph, subject, BOS[key])) == p['controlSchema'][key], 'Control mapping')
    c = snapshot['clockSources'][0]
    clock = URIRef(c['clockId'])
    for prop, key in [('clockDomain', 'clockDomain'), ('clockUnit', 'unit'), ('readOnly', 'readOnly'), ('acquisitionMode', 'acquisitionMode')]:
        check(one(graph, clock, BOS[prop]).toPython() == c[key], 'Clock mapping')
    native = one(graph, clock, BOS.nativeBinding)
    check((native, RDF.type, BOS.BacnetClockBinding) in graph, 'Native binding class')
    for prop, key in [('gatewayId','gatewayId'), ('connectorId','connectorId'), ('deviceInstance','deviceInstance'), ('objectType','objectType'), ('objectInstance','objectInstance'), ('property','property'), ('nativeUnit','unit')]:
        check(one(graph, native, BOS[prop]).toPython() == c['native'][key], 'Native mapping')
    for binding in snapshot['clockBindings']:
        check((URIRef(binding['equipmentId']), BOS.usesClockSource, URIRef(binding['clockId'])) in graph, 'Binding mapping')

    def reject_rdf(name, mutate):
        candidate = copy.deepcopy(graph)
        mutate(candidate)
        check(not conforms(candidate), f'SHACL must reject: {name}')

    cases = [
        ('dangling clock', lambda g: g.set((EX['equipment-a'], BOS.usesClockSource, EX.missing))),
        ('multiple clocks', lambda g: g.add((EX['equipment-a'], BOS.usesClockSource, EX.other))),
        ('missing equipment ID', lambda g: g.remove((EX['equipment-a'], SBCO.id, None))),
        ('unknown equipment', lambda g: g.add((EX.unknown, BOS.usesClockSource, clock))),
        ('cross gateway', lambda g: g.set((EX['BPT-0002'], SBCO.gatewayId, Literal('GW-OTHER')))),
        ('missing unit', lambda g: g.remove((clock, BOS.clockUnit, None))),
        ('conflicting clock definition', lambda g: g.add((clock, BOS.clockUnit, Literal('ms')))),
        ('missing native', lambda g: g.remove((native, BOS.objectInstance, None))),
        ('invalid native', lambda g: g.set((native, BOS.deviceInstance, Literal(-1)))),
        ('writable clock', lambda g: g.set((clock, BOS.readOnly, Literal(False)))),
        ('dual type', lambda g: g.add((clock, RDF.type, SBCO.PointExt))),
        ('point override', lambda g: g.add((EX['BPT-0002'], BOS.usesClockSource, clock))),
        ('ambiguous equipment', lambda g: g.add((EX['equipment-b'], SBCO.hasPoint, EX['BPT-0002']))),
        ('retired ID', lambda g: g.set((EX['BPT-0002'], SBCO.id, Literal('BPT-0001')))),
        ('duplicate ID', lambda g: g.set((EX['BPT-0002'], SBCO.id, Literal('BPT-0003')))),
        ('runtime in static RDF', lambda g: g.add((clock, BOS.runId, Literal('example-run')))),
    ]
    for name, mutate in cases:
        reject_rdf(name, mutate)

    def reject_json(name, s, mutate):
        mutate(s)
        # Rehash intentional content mutations so semantic checks, not just hash, reject them.
        s['revision'] = digest(s)
        try:
            check_snapshot(s)
        except ValueError:
            return
        raise ValueError(f'JSON must reject: {name}')

    json_cases = [
        ('duplicate clock', lambda s: s['clockSources'].append(copy.deepcopy(s['clockSources'][0]))),
        ('duplicate equipment', lambda s: s['clockBindings'].append(copy.deepcopy(s['clockBindings'][0]))),
        ('missing point', lambda s: s['points'].pop()),
    ]
    for name, mutate in json_cases:
        reject_json(name, copy.deepcopy(snapshot), mutate)
    changed = copy.deepcopy(snapshot)
    changed['clockSources'][0]['native']['objectInstance'] += 1
    check(digest(changed) != snapshot['revision'], 'Clock-only change invalidates revision')
    changed = copy.deepcopy(snapshot)
    changed['generatedAt'] = '2026-02-01T00:00:00Z'
    check(digest(changed) == snapshot['revision'], 'generatedAt excluded from hash')
    print(f'PASS: RDF/JSON 35 points, 1 clock, 2 bindings; {len(cases)} SHACL + {len(json_cases)} static JSON rejections; '
          f'time contract {accepted} accepted + {rejected} rejected; {transitions} generation transitions')


if __name__ == '__main__':
    main()
