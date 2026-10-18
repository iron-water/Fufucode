"""Durable event routing for the coordinator. Routing never calls a model."""
import argparse
import hashlib
import json
from pathlib import Path
import os
import tempfile
import subprocess
import datetime as dt

ROLES = {
    'local': None,
    'validator': 'devin/swe-2',
    'reviewer': 'group/auto-gpt-6-1-sol',
    'architect': ['group/auto-gpt-6-astra', 'group/auto-claude-sonnet-5-5', 'group/auto-gpt-6-1-sol'],
}
ROUTES = {
    'delivery.received': 'local',
    'verification.local_passed': 'validator',
    'verification.semantic_completed': 'reviewer',
    'human.requirement.received': 'reviewer',
    'architecture.change_requested': 'architect',
    'architecture.conflict_confirmed': 'architect',
    'task.repeated_failure': 'architect',
    'review.decided': 'local',
    'integration.completed': 'local',
    'dependency.unblocked': 'local',
}


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(dir=path.parent, suffix='.tmp')
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as output:
            json.dump(value, output, ensure_ascii=False, indent=2)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def route(repo, event, mechanism_test=False):
    """Persist one authoritative envelope. Claims/results are separate, never overwritten."""
    if not isinstance(event, dict) or not event.get('eventId') or event.get('type') not in ROUTES:
        raise ValueError('Known structured event with eventId required')
    if event['type'] == 'task.repeated_failure':
        if event.get('failureCategory') not in ('verification', 'requirement', 'architecture'):
            raise ValueError('Transport/provider failures do not justify architecture escalation')
        failures = event.get('failureIds', [])
        if not isinstance(failures, list) or len(set(failures)) < 3:
            raise ValueError('Architecture escalation requires three distinct recorded failures')
    if event['type'].startswith('architecture.') and not event.get('evidence'):
        raise ValueError('Architecture event requires explicit evidence; no speculative polling')
    role = ROUTES[event['type']]
    model = ROLES[role]
    if mechanism_test and model:
        model = ['devin/swe-2'] * len(model) if isinstance(model, list) else 'devin/swe-2'
    key = hashlib.sha256(event['eventId'].encode()).hexdigest()
    box = Path(repo) / 'tmp/agent-core'
    path = box / 'events' / (key + '.json')
    payload = {'event': event, 'role': role, 'model': model,
               'mechanismTest': mechanism_test, 'effects': 'proposal_only' if role == 'architect' else 'evidence_only' if role == 'validator' else 'controlled_entrypoint_required',
               'status': 'queued', 'key': key}
    lock = box / 'router.lock'
    lock.parent.mkdir(parents=True, exist_ok=True)
    with lock.open('a+b') as handle:
        if os.name == 'nt':
            import msvcrt
            handle.seek(0)
            handle.write(b'0')
            handle.flush()
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_LOCK, 1)
        else:
            import fcntl
            fcntl.flock(handle, fcntl.LOCK_EX)
        try:
            if path.exists():
                prior = json.loads(path.read_text(encoding='utf-8-sig'))
                if prior != payload:
                    raise ValueError('Conflicting event replay or model policy change')
                return prior
            save(path, payload)
            return payload
        finally:
            if os.name == 'nt':
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)


def decode_advisory(text, role):
    # Only an exact JSON Markdown wrapper is normalized; prose/mixed output fails.
    text = text.strip()
    if text.startswith('```json\n') and text.endswith('\n```'):
        text = text[8:-4]
    result = json.loads(text)
    if result.get('role') != role or not isinstance(result.get('evidence'), list) or not isinstance(result.get('findings'), list):
        raise ValueError('Invalid structured advisory result')
    return result


def dispatch(repo, executable, roles=None):
    """Read-only independent role sessions. Output is advisory, never acceptance."""
    box = Path(repo) / 'tmp/agent-core'
    for path in sorted((box / 'events').glob('*.json')):
        job = json.loads(path.read_text(encoding='utf-8-sig'))
        if not job['model'] or (roles and job['role'] not in roles):
            continue
        claim = box / 'claims' / path.name
        claim.parent.mkdir(parents=True, exist_ok=True)
        try:
            with claim.open('x', encoding='utf-8') as output:
                json.dump({'status': 'dispatching', 'key': job['key'], 'role': job['role'],
                           'started': dt.datetime.now(dt.timezone.utc).isoformat()}, output)
        except FileExistsError:
            continue  # Crash/timeout ambiguity is durable and never resent.
        models = job['model'] if isinstance(job['model'], list) else [job['model']]
        results = []
        try:
            for index, model in enumerate(models):
                if job['mechanismTest'] and model != 'devin/swe-2':
                    raise ValueError('Every mechanism-test role must explicitly use devin/swe-2')
                prefix = box / 'logs' / (job['key'] + '-' + str(index))
                prefix.parent.mkdir(parents=True, exist_ok=True)
                prompt = ('你是独立' + job['role'] + '。只读工作，不改文件，不派任务、不集成、不替代最终Review。'
                          '分析下列事件及其中明确提供的证据；证据不足如实说。专家提出路线建议并保留异议，'
                          '验证者只给核验报告；不能虚构通过。所有工具命令显式workdir，模型试跑显式devin/swe-2。'
                          '最终只输出JSON对象，字段role,summary,evidence数组,findings数组,proposal对象或null。事件：'
                          + json.dumps(job['event'], ensure_ascii=False))
                command = [str(executable), '--no-daemon', 'exec', '-m', model, '-C', str(repo),
                           '-s', 'read-only', '-c', 'approval_policy="never"', '--json', '-']
                with prefix.with_suffix('.stdout.jsonl').open('w', encoding='utf-8') as out, prefix.with_suffix('.stderr.log').open('w', encoding='utf-8') as err:
                    process = subprocess.run(command, input=prompt, cwd=repo, text=True, encoding='utf-8',
                                             stdout=out, stderr=err, timeout=600)
                records = [json.loads(line) for line in prefix.with_suffix('.stdout.jsonl').read_text(encoding='utf-8').splitlines() if line.strip()]
                if process.returncode or not any(e.get('type') == 'turn.completed' for e in records):
                    raise ValueError('Role model failed; no automatic retry')
                messages = [e['item']['text'] for e in records if e.get('type') == 'item.completed' and e.get('item', {}).get('type') == 'agent_message']
                result = decode_advisory(messages[-1], job['role'])
                results.append({'model': model, 'result': result, 'logPrefix': str(prefix),
                                'threadId': next((e.get('thread_id') for e in records if e.get('type') == 'thread.started'), None)})
            save(box / 'results' / path.name, {'key': job['key'], 'role': job['role'], 'eventId': job['event']['eventId'],
                 'advisoryOnly': True, 'members': results, 'requiresControlledApplication': True})
            save(claim, {'status': 'completed', 'key': job['key'], 'modelCalls': len(results)})
        except Exception as error:
            save(claim, {'status': 'needs_attention', 'key': job['key'], 'error': str(error), 'automaticRetry': False})


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--repository', required=True)
    parser.add_argument('--event-file')
    parser.add_argument('--mechanism-test', action='store_true')
    parser.add_argument('--dispatch', action='store_true')
    parser.add_argument('--executable')
    parser.add_argument('--roles', nargs='+')
    args = parser.parse_args()
    if args.dispatch:
        if not args.executable:
            parser.error('Explicit native executable required')
        dispatch(args.repository, args.executable, args.roles)
    elif args.event_file:
        print(json.dumps(route(args.repository, json.loads(Path(args.event_file).read_text(encoding='utf-8-sig')), args.mechanism_test), ensure_ascii=False))
    else:
        parser.error('Event file or dispatch required')
