"""Loopback client of the explicitly selected Core/U1 database. No JSON reducer or runtime launches."""
import argparse
import json
from pathlib import Path
import secrets
import subprocess
import tempfile
import threading
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit


class ControlError(ValueError):
    def __init__(self, message, uncertain=False):
        super().__init__(message)
        self.uncertain = uncertain


class Client:
    operations = frozenset(('apply', 'relation', 'knowledge', 'restructure', 'integration', 'review', 'reconsider', 'repair'))

    def __init__(self, repository, database, cli):
        self.repository = Path(repository).resolve(strict=True)
        raw_database = Path(database)
        raw_cli = Path(cli)
        if not raw_database.is_absolute() or not raw_cli.is_absolute():
            raise ValueError('Database and CLI paths must be absolute')
        self.database = raw_database.resolve(strict=True)
        self.cli = raw_cli.resolve(strict=True)
        if not self.database.is_file() or not self.cli.is_file():
            raise ValueError('Explicit existing database and CLI required; initialize using the CLI first')
        self.token = secrets.token_urlsafe(32)
        self.lock = threading.Lock()

    def call(self, operation, command_file=None):
        argv = ['dotnet', str(self.cli), 'control', operation, str(self.database)]
        if command_file is not None:
            argv.append(str(command_file))
        try:
            result = subprocess.run(argv, cwd=self.repository, capture_output=True,
                                    text=True, encoding='utf-8-sig', timeout=45)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise ControlError('CLI unavailable or timed out; reconcile the original command ID', True) from error
        if result.returncode:
            raise ControlError(result.stderr.strip() or 'Core rejected the command')
        try:
            return json.loads(result.stdout)
        except ValueError as error:
            raise ControlError('CLI response unreadable; reconcile the original command ID', True) from error

    def snapshot(self):
        result = self.call('state')
        if (result.get('format') != 'fufucode-control-client-v1' or
                result['state']['control']['revision'] != result['inbox']['revision'] or
                result['state']['eventCursor'] != result['inbox']['eventCursor']):
            raise ControlError('Unsupported or incoherent Core snapshot')
        return result

    def mutate(self, operation, command):
        if operation not in self.operations or not isinstance(command, dict):
            raise ControlError('Unsupported client command; runtime bindings/reports are not browser commands')
        if (not isinstance(command.get('commandId'), str) or not command['commandId'].strip() or
                type(command.get('expectedRevision')) is not int or command['expectedRevision'] < 0 or
                not isinstance(command.get('decidedBy'), str) or not command['decidedBy'].strip()):
            raise ControlError('Original command ID, revision and local decision actor required')
        # Commands are transient transport files; only Core owns state and receipts.
        with self.lock, tempfile.TemporaryDirectory(prefix='fufucode-control-command-') as temporary:
            path = Path(temporary) / 'command.json'
            path.write_text(json.dumps(command, ensure_ascii=False), encoding='utf-8')
            return self.call(operation, path)


def make_server(client, port=0):
    static = client.repository / 'docs/frontend-prototype/automation'

    class Handler(SimpleHTTPRequestHandler):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, directory=str(static), **kwargs)

        def log_message(self, *args):
            pass  # Request bodies and command context do not enter web logs.

        def respond(self, code, body):
            raw = json.dumps(body, ensure_ascii=False).encode('utf-8')
            try:
                self.send_response(code)
                self.send_header('Content-Type', 'application/json; charset=utf-8')
                self.send_header('Cache-Control', 'no-store')
                self.send_header('Content-Length', str(len(raw)))
                self.end_headers()
                self.wfile.write(raw)
            except ConnectionError:
                # A disconnected reader cannot receive another error response.
                # Core's receipt remains authoritative; never retry the mutation here.
                return

        def permitted(self):
            return self.headers.get('Host') == f'127.0.0.1:{self.server.server_port}'

        def do_GET(self):
            if not self.permitted():
                return self.respond(403, {'error': 'Invalid loopback host'})
            path = urlsplit(self.path).path
            try:
                if path == '/api/session':
                    return self.respond(200, {'token': client.token, 'database': str(client.database)})
                if path == '/api/control':
                    return self.respond(200, client.snapshot())
                if path.startswith('/api/'):
                    return self.respond(404, {'error': 'Legacy JSON API unavailable in Core client mode'})
                if path not in ('/', '/control.html', '/control.js', '/control-commands.js', '/parallel.css'):
                    return self.respond(404, {'error': 'Not found'})
                if path == '/':
                    self.path = '/control.html'
                super().do_GET()
            except (ValueError, OSError, KeyError, TypeError) as error:
                self.respond(503, {'error': str(error)})

        def do_POST(self):
            # Drain bounded rejected bodies before closing: unread POST bytes can
            # reset a Windows socket and hide a definite 403 from the client.
            def reject(code, message):
                try:
                    length = int(self.headers.get('Content-Length', '0'))
                    if 0 < length <= 262144:
                        self.connection.settimeout(2)
                        self.rfile.read(length)
                except (ValueError, OSError):
                    pass
                self.respond(code, {'error': message})
            origin = f'http://127.0.0.1:{self.server.server_port}'
            if (not self.permitted() or self.headers.get('Origin') != origin or
                    self.headers.get('X-Console-Token') != client.token):
                return reject(403, 'Local same-origin command token required')
            if urlsplit(self.path).path != '/api/control-command':
                return reject(404, 'Unknown command path')
            try:
                length = int(self.headers.get('Content-Length', '0'))
                if not 0 < length <= 262144:
                    raise ControlError('Invalid request size')
                request = json.loads(self.rfile.read(length))
                if not isinstance(request, dict):
                    raise ControlError('Command envelope must be an object')
                receipt = client.mutate(request.get('operation'), request.get('command'))
                self.respond(200, {'receipt': receipt})
            except (ValueError, OSError, TypeError) as error:
                uncertain = getattr(error, 'uncertain', False)
                self.respond(503 if uncertain else 409, {'error': str(error), 'outcomeUnknown': uncertain})

    return ThreadingHTTPServer(('127.0.0.1', port), Handler)


def serve(repository, database, cli, port):
    client = Client(repository, database, cli)
    with make_server(client, port) as server:
        print(f'http://127.0.0.1:{server.server_port} · Core database: {client.database}', flush=True)
        server.serve_forever()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--repository', required=True)
    parser.add_argument('--database', required=True)
    parser.add_argument('--cli', required=True)
    parser.add_argument('--port', type=int, default=18766)
    args = parser.parse_args()
    serve(args.repository, args.database, args.cli, args.port)
