"""Loopback UI bridge to the existing managed CLI workflow; no model polling."""
import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import threading
import time
import re
import importlib.util
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

_parallel_spec = importlib.util.spec_from_file_location('parallel_control', Path(__file__).with_name('parallel_control.py'))
_parallel = importlib.util.module_from_spec(_parallel_spec)
_parallel_spec.loader.exec_module(_parallel)


def read(path, fallback=None):
    try:
        return json.loads(Path(path).read_text(encoding='utf-8-sig'))
    except FileNotFoundError:
        return fallback


def write(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.new')
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
    os.replace(temporary, path)


def activity(path):
    """Bounded public execution events. Never expose reasoning or tool arguments."""
    events, session = [], None
    try:
        with Path(path).open('rb') as stream:
            size = stream.seek(0, 2)
            stream.seek(max(0, size - 2097152))
            lines = stream.read().decode('utf-8', errors='replace').splitlines()
        updated = dt.datetime.fromtimestamp(Path(path).stat().st_mtime, dt.timezone.utc).isoformat()
    except FileNotFoundError:
        return {'events': [], 'updated': None, 'session': None}
    for line in lines:
        try:
            event = json.loads(line)
        except ValueError:
            continue  # An in-flight partial line is not a corrupt session.
        kind = event.get('type')
        if kind == 'thread.started':
            session = event.get('thread_id')
        item = event.get('item') or {}
        if kind in ('item.started', 'item.completed'):
            labels = {'command_execution': '执行命令', 'file_change': '修改文件',
                      'mcp_tool_call': '调用工具', 'agent_message': '输出结果',
                      'todo_list': '更新任务步骤'}
            label = labels.get(item.get('type'))
            if label:
                record = {'label': label, 'status': '完成' if kind == 'item.completed' else '进行中'}
                if item.get('type') == 'command_execution' and item.get('exit_code') not in (None, 0):
                    record['status'] = '失败'
                if item.get('type') == 'todo_list':
                    record['steps'] = [{'text': str(i.get('text', ''))[:160],
                                        'completed': bool(i.get('completed'))} for i in item.get('items', [])]
                events.append(record)
        elif kind in ('tool_execution_start', 'tool_execution_end'):
            events.append({'label': '调用工具', 'status': '进行中' if kind.endswith('start') else
                           ('失败' if event.get('isError') else '完成')})
        elif kind in ('turn.completed', 'agent_end', 'turn.failed', 'error'):
            events.append({'label': '会话执行', 'status': '失败' if kind in ('turn.failed', 'error') else '结束'})
    return {'events': events[-30:], 'updated': updated, 'session': session}


def process_alive(pid, started=None, tolerance=0.01):
    if os.name != 'nt' or not isinstance(pid, int) or pid <= 0:
        return False
    import ctypes
    from ctypes import wintypes
    api = ctypes.WinDLL('kernel32', use_last_error=True)
    api.OpenProcess.restype = wintypes.HANDLE
    api.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    api.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    api.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    api.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = api.OpenProcess(0x1000, False, pid)
    if not handle:
        return False
    try:
        code = wintypes.DWORD()
        if not api.GetExitCodeProcess(handle, ctypes.byref(code)) or code.value != 259:
            return False
        if not started:
            return False  # A PID without a birth identity cannot establish ownership.
        times = [wintypes.FILETIME() for _ in range(4)]
        if not api.GetProcessTimes(handle, *[ctypes.byref(t) for t in times]):
            return False
        birth = ((times[0].dwHighDateTime << 32) | times[0].dwLowDateTime) / 10000000 - 11644473600
        try:
            expected = dt.datetime.fromisoformat(started.replace('Z', '+00:00')).timestamp()
        except (ValueError, AttributeError):
            return False
        return abs(expected - birth) <= tolerance
    finally:
        api.CloseHandle(handle)


class Bridge:
    def __init__(self, repo):
        self.repo = Path(repo).resolve()
        self.flow = self.repo / 'tmp/agent-flow'
        self.gate = self.repo / 'tmp/agent-gate'
        self.box = self.repo / 'tmp/automation-console'
        self.lock = threading.Lock()
        self.token = secrets.token_urlsafe(32)
        self.development = _parallel.Store(self.repo / 'tmp/development-control/state.json')

    def command(self, script, mode):
        result = subprocess.run(['pwsh', '-NoProfile', '-File', str(self.repo / 'scripts' / script),
                                 '-Mode', mode, '-Repository', str(self.repo)], cwd=self.repo,
                                capture_output=True, text=True, encoding='utf-8', timeout=45)
        if result.returncode:
            raise ValueError(result.stderr.strip() or result.stdout.strip() or 'Control command failed')
        return result.stdout

    def snapshot(self):
        plan = read(self.flow / 'plan.json', {})
        manager = read(self.gate / 'manager.json', {})
        manager['processAlive'] = process_alive(manager.get('processId'), manager.get('processStarted'))
        runs = []
        for directory in (self.repo / 'tmp/agent-queue').glob('*'):
            manifest = read(directory / 'manifest.json', {})
            if manifest.get('workflowId') != plan.get('id'):
                continue  # Do not adopt unrelated/user-owned sessions or mechanism fixtures.
            state = read(directory / 'state.json', {})
            review = read(directory / 'review.json', {})
            delivery = read(directory / 'delivery.json', {})
            progress = activity(directory / 'stdout.log')
            integrations = [read(p) for p in self.flow.glob('integration-*.json')]
            integration = next((i for i in integrations if i and i.get('runId') == manifest.get('runId')), None)
            runs.append({'runId': manifest.get('runId'), 'taskId': manifest.get('task'),
                         'attempt': manifest.get('attempt', 1), 'model': manifest.get('model'),
                         'harness': manifest.get('harness'), 'session': manifest.get('resumeSession') or delivery.get('sessionId'),
                         'worktree': manifest.get('worktree'), 'state': state.get('status'),
                         'head': state.get('head'), 'started': state.get('started'),
                         'finished': state.get('finished'), 'workerPid': state.get('workerPid'),
                         'agentPid': state.get('agentPid'), 'decision': review.get('decision'),
                         'reviewedHead': review.get('reviewedHead'), 'findings': review.get('findings', []),
                         'summary': (delivery.get('card') or {}).get('summary'), 'progress': progress,
                         'agentAlive': process_alive(state.get('agentPid'), state.get('agentStarted')),
                         'integration': integration, 'directory': str(directory)})
        runs.sort(key=lambda r: (r.get('started') or '', r.get('attempt', 0)))
        policy = read(self.box / 'policy.json', {'mode': 'auto', 'maxWorkers': 2,
                      'strategy': 'dependency-cost-conflict', 'testModel': 'devin/swe-2'})
        runtimes = {'workflow': read(self.flow / 'runtime.json', {}), 'gate': read(self.gate / 'runtime.json', {})}
        runtimes['core'] = read(self.repo / 'tmp/agent-core/runtime.json', {})
        runtimes['controls'] = read(self.flow / 'controls/runtime.json', {})
        for runtime in runtimes.values():
            runtime['processAlive'] = process_alive(runtime.get('pid'), runtime.get('started') or runtime.get('at'), 5)
        human_requirements = []
        for path in (self.flow / 'human').glob('task-*.json'):
            item = read(path)
            replaced = {i for r in item.get('requests', []) for i in (r['record']['request'].get('supersedes') or [])}
            effective = [r for r in item.get('requests', []) if r['commandId'] not in replaced]
            item['acknowledgments'] = [read(self.flow / 'human' / ('ack-' + r['commandId'] + '.json')) for r in effective]
            item['ready'] = all(a and a.get('decision') == 'applied' for a in item['acknowledgments'])
            human_requirements.append(item)
        return {'repository': str(self.repo), 'at': dt.datetime.now(dt.timezone.utc).isoformat(),
                'team': read(self.repo / 'docs/automation/coordinator-team.json', {'members': []}),
                'plan': plan, 'manager': manager, 'runs': runs, 'runtime': runtimes,
                'blockers': [item for p in self.flow.glob('blocked-*.json')
                             if (item := read(p, {})) and
                             not (self.flow / ('done-' + str(item.get('task')) + '.json')).exists()],
                'paused': {'workflow': (self.flow / 'paused').exists(), 'gate': (self.gate / 'paused').exists()},
                'policy': policy, 'draft': read(self.box / 'draft.json'),
                'taskControls': [read(p) for p in (self.flow / 'controls').glob('command-*.json')],
                'humanRequirements': human_requirements,
                'coreEvents': [read(p) for p in (self.repo / 'tmp/agent-core/events').glob('*.json')],
                'coreResults': [read(p) for p in (self.repo / 'tmp/agent-core/results').glob('*.json')],
                'coreClaims': [read(p) for p in (self.repo / 'tmp/agent-core/claims').glob('*.json')],
                'coreDecisions': [read(p) for p in (self.repo / 'tmp/agent-core').glob('manager-*.json') if not p.name.startswith('manager-dispatch-')],
                'planRevision': hashlib.sha256(json.dumps(plan, sort_keys=True).encode()).hexdigest(),
                'capabilities': {'realControls': True, 'partialReview': False,
                                 'multipleTasksPerNativeThread': False, 'planApply': True,
                                 'taskRevisions': True, 'revocation': True, 'rebuild': True,
                                 'humanRequirements': True, 'originalSessionPromptInjection': False}}

    def validate_draft(self, data):
        if not isinstance(data, dict) or not isinstance(data.get('tasks'), list):
            raise ValueError('Plan object with tasks required')
        original = read(self.flow / 'plan.json', {})
        if data.get('repository') != original.get('repository') or data.get('id') != original.get('id'):
            raise ValueError('Cannot change workflow or repository identity')
        if data.get('gates') != original.get('gates'):
            raise ValueError('Manual edits cannot pass or remove gates')
        for key in ('schemaVersion', 'codexExecutable', 'ompExecutable', 'managerModel', 'mechanismTest'):
            if data.get(key) != original.get(key):
                raise ValueError('Runtime identity is immutable: ' + key)
        tasks = data['tasks']
        ids = [t.get('id') for t in tasks]
        if not all(isinstance(i, str) and i for i in ids) or len(set(ids)) != len(ids):
            raise ValueError('Unique task identities required')
        for t in tasks:
            if not t.get('model') or t.get('harness') not in ('codex', 'omp'):
                raise ValueError('Explicit model and supported harness required')
            dependencies = (t.get('dependsOn') or []) + (t.get('integrationDependsOn') or [])
            if any(d not in ids or d == t['id'] for d in dependencies):
                raise ValueError('Unknown or self dependency')
            if t['id'] not in {old['id'] for old in original.get('tasks', [])}:
                if not t.get('allowedPaths') or not t.get('worktree'):
                    raise ValueError('New task requires explicit worktree and allowedPaths')
                for name in ('promptFile', 'integrationPromptFile', 'verificationFile'):
                    if not t.get(name) or not Path(t[name]).is_file():
                        raise ValueError('New task requires real contract file: ' + name)
        resolved = set()
        while True:
            before = len(resolved)
            for t in tasks:
                deps = (t.get('dependsOn') or []) + (t.get('integrationDependsOn') or [])
                if set(deps) <= resolved:
                    resolved.add(t['id'])
            if len(resolved) == before:
                break
        if len(resolved) != len(tasks):
            raise ValueError('Dependency cycle')
        # Started runs, including accepted/completed tasks, retain original model/worktree/contracts.
        started = {r['taskId'] for r in self.snapshot()['runs']}
        for old in original.get('tasks', []):
            if old['id'] in started and old != next((t for t in tasks if t['id'] == old['id']), None):
                raise ValueError('Started or completed task is immutable: ' + old['id'])
        return data

    def mutate(self, action, data):
        with self.lock:
            if action == 'pause':
                # Stop future launches first; in-flight model work is not interrupted.
                self.command('agent-workflow.ps1', 'Pause')
                self.command('agent-gate-control.ps1', 'Pause')
            elif action == 'resume':
                if self.snapshot()['policy']['mode'] != 'auto':
                    raise ValueError('Choose auto mode before resuming scheduling')
                if read(self.gate / 'manager.json', {}).get('status') not in ('ready',):
                    raise ValueError('Manager not ready; reconcile unknown delivery first')
                self.command('agent-gate-control.ps1', 'Resume')
                self.command('agent-workflow.ps1', 'Resume')
            elif action == 'mode':
                mode = data.get('mode')
                if mode not in ('auto', 'manual'):
                    raise ValueError('auto/manual required')
                # Editing mode and scheduling pause are independent user controls.
                write(self.box / 'policy.json', {'mode': mode, 'maxWorkers': 2,
                      'strategy': 'dependency-cost-conflict', 'testModel': 'devin/swe-2',
                      'updated': dt.datetime.now(dt.timezone.utc).isoformat()})
            elif action == 'draft':
                snapshot = self.snapshot()
                if data.get('baseRevision') != snapshot['planRevision']:
                    raise ValueError('Plan changed; refresh before saving')
                validated = self.validate_draft(data.get('plan'))
                write(self.box / 'draft.json', {'plan': validated, 'baseRevision': snapshot['planRevision'],
                      'status': 'pending_manager_review', 'created': dt.datetime.now(dt.timezone.utc).isoformat()})
            elif action == 'architecture_request':
                command_id = data.get('commandId', '')
                if not re.fullmatch(r'[A-Za-z0-9-]{8,80}', command_id) or not data.get('requirement'):
                    raise ValueError('Architecture command identity and requirement required')
                event = {'eventId': 'human-architecture/' + command_id, 'type': 'architecture.change_requested',
                         'evidence': [data['requirement']], 'baseRevision': data.get('baseRevision'),
                         'taskId': data.get('taskId'), 'allowedEffects': 'proposal_only',
                         'basePlanHash': hashlib.sha256((self.flow / 'plan.json').read_bytes()).hexdigest().upper()}
                event_file = self.box / ('architecture-' + command_id + '.json')
                prior = read(event_file)
                if not prior and event['baseRevision'] != self.snapshot()['planRevision']:
                    raise ValueError('Architecture plan revision changed; refresh')
                if prior and prior != event:
                    raise ValueError('Conflicting architecture request')
                write(event_file, event)
                arguments = ['python', '-B', str(self.repo / 'scripts/agent-core.py'), '--repository', str(self.repo), '--event-file', str(event_file)]
                if self.snapshot()['plan'].get('mechanismTest'):
                    arguments.append('--mechanism-test')
                result = subprocess.run(arguments, cwd=self.repo, capture_output=True, text=True, encoding='utf-8', timeout=15)
                if result.returncode:
                    raise ValueError(result.stderr.strip() or result.stdout.strip())
            elif action == 'human_requirement':
                command_id = data.get('commandId', '')
                if not re.fullmatch(r'[A-Za-z0-9-]{8,80}', command_id):
                    raise ValueError('Persistent commandId required')
                request_file = self.box / ('human-input-' + command_id + '.json')
                previous = read(request_file)
                if previous is not None and previous != data:
                    raise ValueError('Conflicting human command replay')
                write(request_file, data)
                result = subprocess.run(['pwsh', '-NoProfile', '-File', str(self.repo / 'scripts/agent-human-control.ps1'),
                    '-Mode', 'Submit', '-Repository', str(self.repo), '-RequestFile', str(request_file)],
                    cwd=self.repo, capture_output=True, text=True, encoding='utf-8', timeout=15)
                if result.returncode:
                    raise ValueError(result.stderr.strip() or result.stdout.strip())
            elif action in ('revoke', 'rebuild', 'update_task'):
                task_id, command_id = data.get('taskId', ''), data.get('commandId', '')
                if not re.fullmatch(r'[A-Za-z0-9-]+', task_id) or not re.fullmatch(r'[A-Za-z0-9-]{8,80}', command_id):
                    raise ValueError('Task and persistent commandId required')
                changes = data.get('changes', {})
                if not isinstance(changes, dict):
                    raise ValueError('Task changes object required')
                request_file = self.box / ('request-' + command_id + '.json')
                prior_request = read(request_file)
                if prior_request is not None and prior_request != changes:
                    raise ValueError('CommandId already binds different changes')
                write(request_file, changes)
                modes = {'revoke':'Revoke', 'rebuild':'Rebuild', 'update_task':'Update'}
                result = subprocess.run(['pwsh', '-NoProfile', '-File', str(self.repo / 'scripts/agent-task-control.ps1'),
                    '-Mode', modes[action], '-Repository', str(self.repo), '-TaskId', task_id,
                    '-CommandId', command_id, '-RequestFile', str(request_file)], cwd=self.repo,
                    capture_output=True, text=True, encoding='utf-8', timeout=45)
                if result.returncode:
                    raise ValueError(result.stderr.strip() or result.stdout.strip())
            elif action == 'apply':
                snapshot = self.snapshot()
                draft = snapshot['draft']
                if not draft or draft['baseRevision'] != snapshot['planRevision']:
                    raise ValueError('No draft matching current plan')
                if (snapshot['manager'].get('status') != 'ready' or
                    any(r['state'] in ('running', 'starting') for r in snapshot['runs'])):
                    draft['status'] = 'queued_apply'
                    write(self.box / 'draft.json', draft)
                    return self.snapshot()  # Durable intent; local host applies when writers settle.
                validated = self.validate_draft(draft['plan'])
                claim = self.box / 'apply-claim.json'
                previous = read(claim, {})
                if previous.get('status') == 'inflight':
                    raise ValueError('Previous apply outcome unknown; inspect local claim, no resend')
                backup = {'plan': read(self.flow / 'plan.json'), 'approval': read(self.flow / 'approval.json')}
                plan_bytes = (self.flow / 'plan.json').read_bytes()
                approval_path = self.flow / 'approval.json'
                approval_bytes = approval_path.read_bytes() if approval_path.exists() else None
                self.box.mkdir(parents=True, exist_ok=True)
                (self.box / 'plan-backup.raw').write_bytes(plan_bytes)
                if approval_bytes is not None:
                    (self.box / 'approval-backup.raw').write_bytes(approval_bytes)
                write(self.box / 'plan-backup.json', backup)
                write(claim, {'status': 'inflight', 'baseRevision': snapshot['planRevision'],
                              'at': dt.datetime.now(dt.timezone.utc).isoformat()})
                try:
                    self.command('agent-workflow.ps1', 'Pause')
                    self.command('agent-gate-control.ps1', 'Pause')
                    self.command('agent-workflow.ps1', 'Stop')
                    deadline = time.monotonic() + 20
                    while read(self.flow / 'runtime.json', {}).get('status') != 'stopped':
                        if time.monotonic() >= deadline:
                            raise ValueError('Workflow did not settle; draft not applied')
                        time.sleep(.2)
                    if read(self.gate / 'manager.json', {}).get('status') != 'ready':
                        raise ValueError('M changed while stopping; draft not applied')
                    write(self.flow / 'plan.json', validated)
                    self.command('agent-workflow.ps1', 'Approve')
                    draft['status'] = 'applied_paused'
                    write(self.box / 'draft.json', draft)
                    write(claim, {'status': 'done', 'at': dt.datetime.now(dt.timezone.utc).isoformat()})
                    # Deliberate separate resume: apply does not launch a model or run task.
                except Exception:
                    # Approval binds the exact file hash, including BOM/whitespace.
                    (self.flow / 'plan.json').write_bytes(plan_bytes)
                    if approval_bytes is not None:
                        approval_path.write_bytes(approval_bytes)
                    write(claim, {'status': 'failed_restored', 'at': dt.datetime.now(dt.timezone.utc).isoformat()})
                    raise
            else:
                raise ValueError('Unsupported command; no direct review writes or arbitrary shell')
            with (self.box / 'audit.jsonl').open('a', encoding='utf-8') as audit:
                audit.write(json.dumps({'at': dt.datetime.now(dt.timezone.utc).isoformat(),
                                       'action': action, 'status': 'done'}, ensure_ascii=False) + '\n')
            return self.snapshot()


def serve(repo, port):
    bridge = Bridge(repo)
    bridge.box.mkdir(parents=True, exist_ok=True)
    static = bridge.repo / 'docs/frontend-prototype/automation'

    class Handler(SimpleHTTPRequestHandler):
        def log_message(self, format, *args):
            with (bridge.box / 'server.stderr.log').open('a', encoding='utf-8') as log:
                log.write(self.log_date_time_string() + ' ' + (format % args) + '\n')

        def __init__(self, *args, **kwargs):
            super().__init__(*args, directory=str(static), **kwargs)

        def json_response(self, status, data):
            body = json.dumps(data, ensure_ascii=False).encode()
            self.send_response(status)
            self.send_header('Content-Type', 'application/json; charset=utf-8')
            self.send_header('Cache-Control', 'no-store')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def permitted(self):
            return self.headers.get('Host') == f'127.0.0.1:{port}'

        def do_GET(self):
            if not self.permitted():
                return self.json_response(403, {'error': 'Invalid local host'})
            path = urlsplit(self.path).path
            try:
                if path == '/api/state':
                    return self.json_response(200, bridge.snapshot())
                if path == '/api/development':
                    return self.json_response(200, bridge.development.snapshot())
                if path == '/api/session':
                    return self.json_response(200, {'token': bridge.token})
                if path.startswith('/api/'):
                    return self.json_response(404, {'error': 'Not found'})
                if path == '/':
                    self.path = '/parallel.html'
                super().do_GET()
            except (ValueError, OSError) as error:
                self.json_response(409, {'error': str(error)})

        def do_POST(self):
            if (not self.permitted() or self.headers.get('X-Console-Token') != bridge.token or
                    self.headers.get('Origin') != f'http://127.0.0.1:{port}'):
                return self.json_response(403, {'error': 'Local same-origin command token required'})
            try:
                length = int(self.headers.get('Content-Length', '0'))
                if not 0 < length <= 262144:
                    raise ValueError('Invalid request size')
                request = json.loads(self.rfile.read(length))
                if urlsplit(self.path).path != '/api/command':
                    raise ValueError('Unknown command path')
                if request.get('action') == 'shutdown':
                    self.json_response(200, {'stopped': True})
                    threading.Thread(target=server.shutdown, daemon=True).start()
                    return
                if request.get('action') == 'development':
                    with bridge.lock:
                        bridge.development.apply(request.get('data', {}), actor='human')
                    return self.json_response(200, bridge.development.snapshot())
                self.json_response(200, bridge.mutate(request['action'], request.get('data', {})))
            except (ValueError, OSError, KeyError, subprocess.TimeoutExpired) as error:
                bridge.box.mkdir(parents=True, exist_ok=True)
                with (bridge.box / 'audit.jsonl').open('a', encoding='utf-8') as audit:
                    audit.write(json.dumps({'at': dt.datetime.now(dt.timezone.utc).isoformat(),
                                           'status': 'failed', 'error': str(error)}, ensure_ascii=False) + '\n')
                self.json_response(409, {'error': str(error)})

    server = ThreadingHTTPServer(('127.0.0.1', port), Handler)
    write(bridge.box / 'server.json', {'pid': os.getpid(), 'port': port, 'repository': str(bridge.repo),
          'started': dt.datetime.now(dt.timezone.utc).isoformat(), 'url': f'http://127.0.0.1:{port}'})
    server.serve_forever()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--repository', required=True)
    parser.add_argument('--port', type=int, default=18765)
    parser.add_argument('--apply-draft', action='store_true')
    parser.add_argument('--control-database')
    parser.add_argument('--cli')
    args = parser.parse_args()
    if args.control_database:
        if args.apply_draft or not args.cli:
            parser.error('Core mode requires --cli and cannot apply a legacy JSON draft')
        from control_console import serve as serve_control
        serve_control(args.repository, args.control_database, args.cli, args.port)
    elif args.cli:
        parser.error('--cli requires an explicitly selected --control-database')
    elif args.apply_draft:
        print(json.dumps(Bridge(args.repository).mutate('apply', {}), ensure_ascii=False))
    else:
        serve(args.repository, args.port)
