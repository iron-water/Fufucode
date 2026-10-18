"""Explicit opt-in, isolated real harness probes. Every request uses devin/swe-2.

Usage: python scripts/harness-smoke.py <runtime-profiles.json> [harness ...]
Profiles map harness IDs to HarnessRuntime objects; paths and credentials stay local.
No discovery, installation, global configuration changes, or production adoption.
"""
import json
import pathlib
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
HOST = ROOT / 'tools/Fufucode.HarnessHost/bin/Debug/net10.0/Fufucode.HarnessHost.exe'
profiles = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
evidence = pathlib.Path(tempfile.mkdtemp(prefix='fufucode-harness-smoke-'))
print(f'Evidence: {evidence}', flush=True)
results = []

def invoke(action, manifest):
    process = subprocess.run([str(HOST), action, str(manifest)], cwd=str(manifest.parent),
                             capture_output=True, text=True, encoding='utf-8', timeout=150)
    (manifest.parent / f'{manifest.stem}-{action}.log').write_text(process.stdout + process.stderr, encoding='utf-8')
    if action == 'register':
        assert process.returncode == 0, 'registration failed'
        return None
    return json.loads(process.stdout)

for harness in sys.argv[2:] or profiles:
    directory = evidence / harness
    directory.mkdir()
    marker = 'MEMORY_' + directory.parent.name.rsplit('-', 1)[-1]
    config = dict(Database=str(directory / 'automation.db'), EvidenceDirectory=str(directory / 'runs'),
                  Worktree=str(directory), ProjectId='smoke', BaseSha='fixture', TimeoutSeconds=120,
                  Runtime=profiles[harness],
                  Request=dict(RunId='first', ThreadId='smoke-thread', TaskId='smoke-task',
                               Model='devin/swe-2', Provider='magpie' if harness in ('pi','omp','dsh') else None,
                               Prompt=f'Remember this marker for the next message: {marker}. Reply exactly FIRST_OK. Do not use tools or delegate.'))
    path = directory / 'first.json'
    path.write_text(json.dumps(config, indent=2), encoding='utf-8')
    result = dict(harness=harness)
    try:
        invoke('register', path)
        first = invoke('run', path)
        result['first'] = first
        if first['Status'] == 2 and first['Output'].strip() == 'FIRST_OK':
            config['Request'].update(RunId='second', Prompt='Reply only with the marker I asked you to remember in the previous message. Do not use tools or delegate.')
            second_path = directory / 'second.json'
            second_path.write_text(json.dumps(config, indent=2), encoding='utf-8')
            second = invoke('run', second_path)
            result['second'] = second
            result['passed'] = second['Status'] == 2 and second['Output'].strip() == marker and first['SessionId'] == second['SessionId']
            result['replay'] = invoke('run', path) == first
        else:
            result['passed'] = False
    except Exception as error:
        result.update(passed=False, error=str(error))
    results.append(result)
    (evidence / 'results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
    print(harness, 'PASS' if result['passed'] else 'FAIL', flush=True)
print(f'Results: {evidence / "results.json"}')
sys.exit(0 if all(r['passed'] for r in results) else 1)
