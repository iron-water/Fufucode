"""HTTP approves a repair intent; only the separate trusted CLI consumer may queue it."""
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
    if not args.database.is_absolute() or not args.database.is_file():
        raise ValueError('Explicit completed fixture database required')
    repo = Path(__file__).resolve().parents[1]
    with tempfile.TemporaryDirectory(prefix='fufucode-repair-http-') as temp:
        db = Path(temp) / '返工 临时.db'
        with closing(sqlite3.connect(args.database)) as source, closing(sqlite3.connect(db)) as target:
            source.backup(target)
        client = Client(repo, db, args.cli)
        before = client.snapshot()
        item = next(i for i in before['inbox']['items'] if i['kind'] == 'repair' and i['objectId'] == 'review-rework')
        assert item['actionable'] and item['options'] == ['request_repair']
        context = json.loads(item['contextJson']); part = context['part']
        command = dict(commandId='http-authorize-repair', expectedRevision=before['inbox']['revision'], decidedBy='human-http',
                       taskId=context['TaskId'], partId=item['objectId'], expectedPartRevision=item['version'],
                       expectedRunId=part['RunId'], expectedHeadSha=part['HeadSha'], expectedDecisionId=part['ReviewId'],
                       expectedEvidenceToken=item['evidenceToken'], reason='Authorize correction only for this part')
        with closing(sqlite3.connect(db)) as c:
            queues = c.execute('SELECT count(*) FROM host_requests').fetchone()[0]
        with make_server(client) as server:
            worker = threading.Thread(target=server.serve_forever, daemon=True); worker.start()
            base = f'http://127.0.0.1:{server.server_port}'
            try:
                with urlopen(base + '/api/session', timeout=10) as r:
                    token = json.load(r)['token']
                def post(operation, data):
                    raw = json.dumps(dict(operation=operation, command=data)).encode()
                    headers = {'Origin': base, 'X-Console-Token': token, 'Content-Type': 'application/json'}
                    with urlopen(Request(base + '/api/control-command', data=raw, headers=headers), timeout=30) as r:
                        return json.load(r)
                for operation, bad in (('repair', dict(command, reason='')), ('repair', dict(command, expectedDecisionId='invented')),
                                       ('repair', dict(command, model='unapproved-model')), ('consume-repair', dict(commandId='web-cannot-consume',
                                        expectedRevision=before['inbox']['revision'], decidedBy='human-http', repairId='fake'))):
                    try:
                        post(operation, bad); raise AssertionError('Invalid or privileged browser command accepted')
                    except HTTPError as error:
                        assert error.code == 409, error.read()
                assert client.snapshot() == before
                receipt = post('repair', command)
                assert receipt == post('repair', command)
                after = client.snapshot()
                repair = after['state']['control']['repairs'][0]
                assert repair['id'] == command['commandId'] and repair['status'] == 'awaiting_consumer' and repair['runId'] is None
                assert repair['sourceRunId'] == part['RunId'] and repair['decisionId'] == part['ReviewId']
                assert after['inbox']['revision'] == before['inbox']['revision'] + 1
                assert not any(i['kind'] == 'repair' and i['objectId'] == item['objectId'] for i in after['inbox']['items'])
                with closing(sqlite3.connect(db)) as c:
                    assert c.execute('SELECT count(*) FROM host_requests').fetchone()[0] == queues
                    assert c.execute("SELECT status,current_revision FROM delivery_parts WHERE part_id='review-rework'").fetchone() == (2, 1)
                    assert c.execute("SELECT count(*) FROM integration_marks WHERE part_id='review-rework'").fetchone()[0] == 0
                print('PASS 10 HTTP repair authorization scenarios; no dispatch/integration or model override')
            finally:
                server.shutdown(); worker.join(timeout=10); assert not worker.is_alive()


if __name__ == '__main__':
    main()
