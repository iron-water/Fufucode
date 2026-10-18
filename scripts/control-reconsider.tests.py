"""Real HTTP reopen/review of an isolated backup of a completed Host fixture."""
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
    with tempfile.TemporaryDirectory(prefix='fufucode-reconsider-http-') as temp:
        db = Path(temp) / '再审 临时.db'
        with closing(sqlite3.connect(args.database)) as source, closing(sqlite3.connect(db)) as target:
            source.backup(target)
        client = Client(repo, db, args.cli)
        before = client.snapshot()
        item = next(i for i in before['inbox']['items'] if i['objectId'] == 'reconsider-blocked')
        assert item['kind'] == 'reconsider' and item['options'] == ['reopen']
        context = json.loads(item['contextJson'])
        part = context['part']
        reopen = dict(commandId='http-reconsider', expectedRevision=before['inbox']['revision'], decidedBy='human-http',
                      taskId=context['TaskId'], partId=item['objectId'], expectedPartRevision=item['version'],
                      expectedRunId=part['RunId'], expectedHeadSha=part['HeadSha'], expectedDecisionId=part['ReviewId'],
                      expectedEvidenceToken=item['evidenceToken'], reason='Temporary fixture blocker resolved')
        with make_server(client) as server:
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            base = f'http://127.0.0.1:{server.server_port}'
            try:
                with urlopen(base + '/api/session', timeout=10) as r:
                    token = json.load(r)['token']
                def post(operation, command):
                    data = json.dumps(dict(operation=operation, command=command)).encode()
                    headers = {'Origin': base, 'X-Console-Token': token, 'Content-Type': 'application/json'}
                    with urlopen(Request(base + '/api/control-command', data=data, headers=headers), timeout=30) as r:
                        return json.load(r)
                for bad in (dict(reopen, reason=''), dict(reopen, expectedDecisionId='wrong-review')):
                    try:
                        post('reconsider', bad)
                        raise AssertionError('Invalid reconsider accepted')
                    except HTTPError as error:
                        assert error.code == 409, error.read()
                assert client.snapshot() == before
                receipt = post('reconsider', reopen)
                assert receipt == post('reconsider', reopen)
                pending = client.snapshot()
                review_item = next(i for i in pending['inbox']['items'] if i['objectId'] == item['objectId'])
                assert review_item['kind'] == 'review' and pending['inbox']['revision'] == before['inbox']['revision'] + 1
                pending_part = json.loads(review_item['contextJson'])['part']
                for key in ('RunId', 'HeadSha', 'Revision', 'CardJson', 'ReviewId'):
                    assert pending_part[key] == part[key], f'Reopen changed immutable {key}'
                review = dict(commandId='http-after-reconsider', expectedRevision=pending['inbox']['revision'], decidedBy='human-new-review',
                              taskId=context['TaskId'], partId=item['objectId'], expectedPartRevision=item['version'],
                              expectedRunId=part['RunId'], expectedHeadSha=part['HeadSha'], expectedEvidenceToken=review_item['evidenceToken'],
                              decision='Accept', findings=None, evidenceJson='{"checks":["reconsider HTTP fixture"]}')
                accepted = post('review', review)
                assert accepted == post('review', review)
                assert receipt == post('reconsider', reopen), 'Original reopen receipt must survive later review'
                after = client.snapshot()
                assert not any(i['objectId'] == item['objectId'] for i in after['inbox']['items'])
                with closing(sqlite3.connect(db)) as c:
                    rows = c.execute("SELECT decision_id,decision,decided_by FROM part_decisions WHERE part_id='reconsider-blocked' ORDER BY rowid").fetchall()
                    assert len(rows) == 2 and rows[0][0] == part['ReviewId'] and rows[0][1] == 2 and rows[1][1] == 0
                    assert c.execute("SELECT count(*) FROM delivery_revisions WHERE part_id='reconsider-blocked'").fetchone()[0] == 1
                    assert c.execute("SELECT count(*) FROM integration_marks WHERE part_id='reconsider-blocked'").fetchone()[0] == 0
                print('PASS 9 HTTP reconsider/review scenarios (original history and delivery preserved)')
            finally:
                server.shutdown()
                worker.join(timeout=10)
                assert not worker.is_alive()


if __name__ == '__main__':
    main()
