"""HTTP review against a backup of a real, stopped Control/Host fixture. No model calls."""
import argparse
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import threading
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from control_console import Client, make_server


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--database', type=Path, required=True)
    parser.add_argument('--cli', type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    if not args.database.is_absolute() or not args.database.is_file():
        raise ValueError('Explicit completed fixture database required')
    with tempfile.TemporaryDirectory(prefix='fufucode-review-http-') as temp:
        db = Path(temp) / '审核 临时.db'
        with closing(sqlite3.connect(args.database)) as source, closing(sqlite3.connect(db)) as target:
            source.backup(target)
        client = Client(repo, db, args.cli)
        with make_server(client) as server:
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            base = f'http://127.0.0.1:{server.server_port}'
            try:
                with urlopen(base + '/api/session', timeout=10) as r:
                    session = json.load(r)
                with urlopen(base + '/api/control', timeout=10) as r:
                    before = json.load(r)
                item = next(i for i in before['inbox']['items'] if i['objectId'] == 'review-untouched')
                context = json.loads(item['contextJson'])
                command = dict(commandId='http-partial-review', expectedRevision=before['inbox']['revision'],
                               decidedBy='human-http', taskId=context['TaskId'], partId=item['objectId'],
                               expectedPartRevision=item['version'], expectedRunId=context['part']['RunId'],
                               expectedHeadSha=context['part']['HeadSha'], expectedEvidenceToken=item['evidenceToken'],
                               decision='Accept', findings=None, evidenceJson='{"checks":["HTTP fixture"]}')
                def post(cmd, token=session['token']):
                    data = json.dumps(dict(operation='review', command=cmd)).encode()
                    headers = {'Origin': base, 'X-Console-Token': token, 'Content-Type': 'application/json'}
                    with urlopen(Request(base + '/api/control-command', data=data, headers=headers), timeout=30) as r:
                        return json.load(r)
                for cmd, token, status in ((command, 'incorrect', 403),
                                           (dict(command, expectedHeadSha='wrong'), session['token'], 409)):
                    try:
                        post(cmd, token)
                        raise AssertionError('Invalid review was accepted')
                    except HTTPError as error:
                        assert error.code == status, error.read()
                assert client.snapshot() == before, 'Rejected requests must not mutate authoritative state'
                missing_decision = {key: value for key, value in command.items() if key != 'decision'}
                try:
                    post(missing_decision)
                    raise AssertionError('Missing explicit decision must be rejected, never default to Accept')
                except HTTPError as error:
                    assert error.code == 409, error.read()
                assert client.snapshot() == before, 'Missing decision must not mutate state'
                accepted = post(command)
                assert accepted == post(command), 'Lost response retry must return the original receipt'
                after = client.snapshot()
                assert after['inbox']['revision'] == before['inbox']['revision'] + 1
                assert not any(i['objectId'] == 'review-untouched' for i in after['inbox']['items'])
                with closing(sqlite3.connect(db)) as c:
                    decisions = dict(c.execute("SELECT p.part_id,d.decision FROM delivery_parts p JOIN part_decisions d ON p.part_id=d.part_id WHERE p.part_id LIKE 'review-%'"))
                    assert decisions['review-untouched'] == decisions['review-accept']
                    assert len({decisions['review-accept'], decisions['review-rework'], decisions['review-blocked'], decisions['review-defer']}) == 4
                    assert c.execute("SELECT count(*) FROM integration_marks WHERE part_id LIKE 'review-%'").fetchone()[0] == 0
                print('PASS 9 real HTTP partial-review checks (isolated database backup)')
            finally:
                server.shutdown()
                worker.join(timeout=10)
                assert not worker.is_alive()


if __name__ == '__main__':
    main()
