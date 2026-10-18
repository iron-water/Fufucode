"""Explicit free-model role transport in its own disposable repository."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile

spec = importlib.util.spec_from_file_location('agent_core', Path(__file__).with_name('agent-core.py'))
core = importlib.util.module_from_spec(spec)
spec.loader.exec_module(core)
root = Path(tempfile.mkdtemp(prefix='fufucode-core-model-'))
executable = sys.argv[1]  # Native codex.exe, supplied explicitly; no defaults.
subprocess.run(['git', 'init', '-q', str(root)], check=True, cwd=root)
(root / 'AGENTS.md').write_text('Mechanism fixture only. Every model explicitly devin/swe-2. No tools needed. Return requested JSON.', encoding='utf-8')
evidence = ['Isolated mechanism probe. No product work. Only verify valid role JSON and persistent records; do not use tools.']
core.route(root, {'eventId': 'validator-probe', 'type': 'verification.local_passed', 'evidence': evidence}, True)
core.route(root, {'eventId': 'expert-probe', 'type': 'architecture.change_requested', 'evidence': evidence}, True)
source = Path(__file__).resolve().parent.parent
core.save(source / 'tmp/automation-preview/core-model-root.json', {'root': str(root)})
core.dispatch(root, executable)
results = list((root / 'tmp/agent-core/results').glob('*.json'))
if len(results) != 2:
    raise RuntimeError('Some model role failed; inspect claims/logs: ' + str(root))
records = [json.loads(p.read_text(encoding='utf-8')) for p in results]
members = [m for r in records for m in r['members']]
assert len(members) == 4 and all(m['model'] == 'devin/swe-2' and m['threadId'] for m in members)
assert len({m['threadId'] for m in members}) == 4
before = [p.read_bytes() for p in sorted((root / 'tmp/agent-core/claims').glob('*.json'))]
core.dispatch(root, executable)
after = [p.read_bytes() for p in sorted((root / 'tmp/agent-core/claims').glob('*.json'))]
assert before == after
core.save(root / 'validation.json', {'status': 'passed', 'model': 'devin/swe-2', 'modelCalls': 4,
          'roles': ['validator', 'three-independent-experts'], 'replayModelCalls': 0, 'advisoryOnly': True})
print('PASS four actual free role sessions and replay: ' + str(root))
