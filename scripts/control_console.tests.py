"""Real CLI/SQLite and loopback HTTP tests. Uses isolated data, never an agent model."""
import json
from contextlib import closing
from pathlib import Path
import subprocess
import socket
import struct
import os
import tempfile
import threading
import unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from unittest.mock import patch
from control_console import Client, ControlError, make_server

REPO = Path(__file__).resolve().parents[1]
CLI = REPO / 'src/Fufucode.Cli/bin/Debug/net10.0/Fufucode.Cli.dll'


class Checks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='fufucode-client-check-')
        self.root = Path(self.temp.name)
        self.database = self.root / '中文 控制.db'
        subprocess.run(['dotnet', str(CLI), 'control', 'init', str(self.database)],
                       cwd=REPO, check=True, capture_output=True)
        self.client = Client(REPO, self.database, CLI)

    def tearDown(self):
        self.temp.cleanup()

    def define(self, command='create-a', revision=0):
        return dict(commandId=command, expectedRevision=revision, decidedBy='human-fixture',
                    action='DefinePackage', expectedPackageVersion=0,
                    package=dict(id='A', title='资料检查', goal='验证真实读取', scope=['src/owned/**'],
                                 acceptance=['实际行为通过'], harness='codex', model='devin/swe-2',
                                 artifact='code', dependsOn=[]))

    def test_real_state_plan_inbox_and_replay_share_database(self):
        before = self.client.snapshot()
        self.assertEqual(before['state']['control']['mode'], 'Manual')
        command = self.define()
        receipt = self.client.mutate('apply', command)
        second = Client(REPO, self.database, CLI)
        state = second.snapshot()
        self.assertEqual(state['state']['control']['revision'], state['inbox']['revision'])
        self.assertEqual(state['state']['eventCursor'], state['inbox']['eventCursor'])
        self.assertEqual(state['state']['control']['packages'][0]['spec']['title'], '资料检查')
        self.assertEqual(state['inbox']['items'][0]['kind'], 'plan')
        self.assertEqual(second.mutate('apply', command), receipt)
        self.assertEqual(second.snapshot(), state)
        with self.assertRaises(ControlError):
            second.mutate('apply', dict(command, package=dict(command['package'], title='变更')))
        self.assertEqual(second.snapshot(), state)

    def test_stale_and_auto_rejected_without_execution(self):
        self.client.mutate('apply', self.define())
        stale = dict(commandId='stale', expectedRevision=0, decidedBy='human-fixture',
                     action='ApprovePackage', packageId='A', expectedPackageVersion=1)
        with self.assertRaises(ControlError):
            self.client.mutate('apply', stale)
        with self.assertRaises(ControlError):
            self.client.mutate('apply', dict(commandId='auto', expectedRevision=1,
                                            decidedBy='human-fixture', action='SetMode', mode='Automatic'))
        current = self.client.snapshot()
        self.assertEqual(current['inbox']['revision'], 1)
        self.assertEqual(current['state']['control']['intents'], [])

    def test_approval_is_not_execution_and_semi_creates_only_intent(self):
        self.client.mutate('apply', self.define())
        self.client.mutate('apply', dict(commandId='approve', expectedRevision=1, decidedBy='human-fixture',
                                        action='ApprovePackage', packageId='A', expectedPackageVersion=1))
        self.assertEqual(self.client.snapshot()['state']['control']['intents'], [])
        self.client.mutate('apply', dict(commandId='semi', expectedRevision=2, decidedBy='human-fixture',
                                        action='SetMode', mode='SemiAutomatic'))
        current = self.client.snapshot()
        self.assertEqual(current['state']['control']['intents'][0]['status'], 'awaiting_binding')
        import sqlite3
        with closing(sqlite3.connect(self.database)) as c:
            self.assertEqual(c.execute('select count(*) from tasks').fetchone()[0], 0)

    def test_atomic_client_snapshot_during_real_concurrent_changes(self):
        errors = []
        def change():
            try:
                for n in range(12):
                    command = self.define(f'concurrent-{n}', n)
                    command['package']['id'] = f'p{n}'
                    self.client.mutate('apply', command)
            except Exception as error:
                errors.append(error)
        writer = threading.Thread(target=change)
        writer.start()
        second = Client(REPO, self.database, CLI)
        for _ in range(12):
            state = second.snapshot()
            self.assertEqual(state['state']['control']['revision'], state['inbox']['revision'])
            self.assertEqual(state['state']['eventCursor'], state['inbox']['eventCursor'])
            self.assertEqual(len(state['state']['control']['packages']), len(state['inbox']['items']))
        writer.join(timeout=45)
        self.assertFalse(writer.is_alive())
        self.assertEqual(errors, [])
        self.assertEqual(second.snapshot()['inbox']['revision'], 12)

    def test_transport_never_creates_database_or_accepts_runtime_reports(self):
        missing = self.root / 'not-created.db'
        with self.assertRaises(FileNotFoundError):
            Client(REPO, missing, CLI)
        self.assertFalse(missing.exists())
        for op in ('init', 'archive', 'bind', 'consume-repair', 'mark-integrated', 'chat'):
            with self.assertRaises(ControlError):
                self.client.mutate(op, self.define())
        with self.assertRaises(ValueError):
            Client(REPO, Path('relative.db'), CLI)

    def test_failed_transport_preserves_unknown_outcome(self):
        with patch('control_console.subprocess.run', side_effect=subprocess.TimeoutExpired('fixture', 45)):
            with self.assertRaises(ControlError) as caught:
                self.client.mutate('apply', self.define())
        self.assertTrue(caught.exception.uncertain)
        self.assertEqual(self.client.snapshot()['inbox']['revision'], 0)

    def test_closed_response_socket_does_not_trigger_a_second_error_response(self):
        class ClosedSocket:
            def __init__(self, error):
                self.error = error
            def write(self, raw):
                raise self.error('peer closed')
        class Response:
            def __init__(self, error, when):
                self.wfile = ClosedSocket(error)
                self.statuses = []
                self.error, self.when = error, when
            def send_response(self, status):
                self.statuses.append(status)
            def send_header(self, *_):
                pass
            def end_headers(self):
                if self.when == 'headers':
                    raise self.error('headers peer closed')
        with make_server(self.client) as server:
            for error in (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
                for when in ('headers', 'body'):
                    response = Response(error, when)
                    server.RequestHandlerClass.respond(response, 200, {'receipt': 'persisted-command'})
                    self.assertEqual(response.statuses, [200])

    def test_lost_http_reply_preserves_real_core_receipt(self):
        committed, release, responded = threading.Event(), threading.Event(), threading.Event()
        original_mutate = self.client.mutate
        def commit_then_wait(operation, command):
            receipt = original_mutate(operation, command)
            committed.set()
            if not release.wait(timeout=20):
                raise TimeoutError('Fixture response release timed out')
            return receipt
        with make_server(self.client) as server:
            original_respond = server.RequestHandlerClass.respond
            def track_response(handler, code, body):
                try:
                    return original_respond(handler, code, body)
                finally:
                    if 'receipt' in body:
                        responded.set()
            errors = []
            server.handle_error = lambda *_: errors.append('unhandled request error')
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            command = self.define('lost-real-http-reply')
            base = f'http://127.0.0.1:{server.server_port}'
            raw = json.dumps({'operation': 'apply', 'command': command}).encode()
            request = (f'POST /api/control-command HTTP/1.1\r\nHost: 127.0.0.1:{server.server_port}\r\n'
                       f'Origin: {base}\r\nX-Console-Token: {self.client.token}\r\nContent-Type: application/json\r\n'
                       f'Content-Length: {len(raw)}\r\n\r\n').encode() + raw
            try:
                with patch.object(self.client, 'mutate', side_effect=commit_then_wait), \
                     patch.object(server.RequestHandlerClass, 'respond', track_response):
                    connection = socket.create_connection(('127.0.0.1', server.server_port), timeout=10)
                    try:
                        connection.sendall(request)
                        self.assertTrue(committed.wait(timeout=45), 'Real Core command must commit before disconnect')
                        connection.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack('hh' if os.name == 'nt' else 'ii', 1, 0))
                    finally:
                        connection.close()
                        release.set()
                    self.assertTrue(responded.wait(timeout=10))
                with urlopen(base + '/api/control', timeout=15) as response:
                    state = json.load(response)
                self.assertEqual(state['inbox']['revision'], 1)
                self.assertEqual(len(state['state']['control']['packages']), 1)
                receipt = original_mutate('apply', command)
                self.assertEqual(receipt['revision'], 1)
                self.assertEqual(self.client.snapshot(), state)
                self.assertEqual(errors, [])
            finally:
                release.set()
                server.shutdown()
                thread.join(timeout=10)
                self.assertFalse(thread.is_alive())

    def test_http_core_mode_excludes_legacy_and_checks_origin_token_host(self):
        with make_server(self.client) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            base = f'http://127.0.0.1:{server.server_port}'
            def get(path):
                with urlopen(base + path, timeout=10) as response:
                    return json.load(response)
            def post(headers):
                raw = json.dumps({'operation': 'apply', 'command': self.define()}).encode()
                return urlopen(Request(base + '/api/control-command', data=raw, headers=headers), timeout=15)
            try:
                session = get('/api/session')
                self.assertEqual(session['database'], str(self.database))
                good = {'Origin': base, 'X-Console-Token': session['token'], 'Content-Type': 'application/json'}
                for headers in ({}, dict(good, Origin='http://other.invalid'), dict(good, Host='localhost:80')) * 3:
                    with self.assertRaises(HTTPError) as caught:
                        post(headers)
                    self.assertEqual(caught.exception.code, 403)
                    caught.exception.close()
                for path in ('/api/development', '/parallel.html', '/../AGENTS.md'):
                    with self.assertRaises(HTTPError) as caught:
                        get(path)
                    self.assertEqual(caught.exception.code, 404)
                    caught.exception.close()
                with post(good) as response:
                    self.assertEqual(json.load(response)['receipt']['revision'], 1)
                self.assertEqual(get('/api/control')['inbox']['revision'], 1)
            finally:
                server.shutdown()
                thread.join(timeout=5)

    def test_snapshot_ignores_legacy_json_and_never_exposes_it(self):
        legacy = self.root / 'state.json'
        raw = b'{"mode":"automatic","packages":{"fake":{"integrated":true}}}'
        legacy.write_bytes(raw)
        self.assertEqual(self.client.snapshot()['state']['control']['packages'], [])
        self.assertEqual(legacy.read_bytes(), raw)


if __name__ == '__main__':
    unittest.main()
