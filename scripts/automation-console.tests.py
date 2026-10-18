import importlib.util
import json
import hashlib
from pathlib import Path
import tempfile
import unittest
import shutil
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('console_bridge', Path(__file__).with_name('automation-console.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class BridgeTests(unittest.TestCase):
    def test_inflight_is_not_proof_of_live_manager(self):
        module.write(self.bridge.gate / 'manager.json', {'status': 'inflight', 'processId': 123,
                                                        'processStarted': '2026-10-11T00:00:00Z'})
        with patch.object(module, 'process_alive', return_value=False):
            self.assertFalse(self.bridge.snapshot()['manager']['processAlive'])
        with patch.object(module, 'process_alive', return_value=True):
            self.assertTrue(self.bridge.snapshot()['manager']['processAlive'])

    def test_resolved_blocker_is_not_current_blocker(self):
        module.write(self.bridge.flow / 'blocked-A.json', {'task': 'A', 'error': 'old failure'})
        module.write(self.bridge.flow / 'blocked-B.json', {'task': 'B', 'error': 'needs repair'})
        module.write(self.bridge.flow / 'done-A.json', {'task': 'A', 'mainSha': 'abc'})
        self.assertEqual([b['task'] for b in self.bridge.snapshot()['blockers']], ['B'])

    def test_public_activity_ignores_reasoning_and_partial_lines(self):
        path = self.repo / 'stdout.log'
        path.write_text('\n'.join(json.dumps(e) for e in [
            {'type': 'item.completed', 'item': {'type': 'reasoning', 'text': 'private'}},
            {'type': 'item.started', 'item': {'type': 'command_execution', 'command': 'secret'}},
            {'type': 'item.completed', 'item': {'type': 'command_execution', 'exit_code': 1}},
            {'type': 'tool_execution_end', 'isError': False},
        ]) + '\n{"type":', encoding='utf-8')
        result = module.activity(path)
        self.assertEqual([e['status'] for e in result['events']], ['进行中', '失败', '完成'])
        self.assertNotIn('private', json.dumps(result))
        self.assertNotIn('secret', json.dumps(result))

    def test_real_human_submit_persists_without_model(self):
        scripts = self.repo / 'scripts'
        scripts.mkdir()
        for name in ('agent-events.ps1', 'agent-human.ps1', 'agent-human-control.ps1'):
            shutil.copy(Path(__file__).with_name(name), scripts / name)
        data = {'schemaVersion': 1, 'type': 'human.change.request', 'commandId': 'human-api-000001',
                'projectId': 'fixture', 'taskId': 'A', 'action': 'append_acceptance',
                'baseRevision': 0, 'requirement': 'New acceptance', 'acceptance': ['must be 43']}
        first = self.bridge.mutate('human_requirement', data)
        self.assertEqual(first['humanRequirements'][0]['revision'], 1)
        self.assertFalse(first['humanRequirements'][0]['ready'])
        again = self.bridge.mutate('human_requirement', data)
        self.assertEqual(again['humanRequirements'][0]['revision'], 1)
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            self.bridge.mutate('human_requirement', dict(data, requirement='different'))

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='fufucode-console-')
        self.repo = Path(self.temp.name)
        self.bridge = module.Bridge(self.repo)
        self.plan = {'id': 'fixture', 'repository': str(self.repo), 'gates': {'G1': 'open'},
                     'tasks': [{'id': 'A', 'model': 'devin/swe-2', 'harness': 'codex', 'dependsOn': []}]}
        module.write(self.bridge.flow / 'plan.json', self.plan)

    def tearDown(self):
        self.temp.cleanup()

    def test_only_managed_runs_and_integration_is_separate(self):
        own = self.repo / 'tmp/agent-queue/own'
        module.write(own / 'manifest.json', {'workflowId': 'fixture', 'task': 'A', 'runId': 'r1'})
        module.write(own / 'state.json', {'status': 'completed_pending_review', 'head': 'abc'})
        module.write(own / 'review.json', {'decision': 'accept'})
        module.write(self.repo / 'tmp/agent-queue/unknown/manifest.json', {'workflowId': 'other'})
        snapshot = self.bridge.snapshot()
        self.assertEqual(len(snapshot['runs']), 1)
        self.assertIsNone(snapshot['runs'][0]['integration'])
        module.write(self.bridge.flow / 'integration-a.json', {'runId': 'r1', 'status': 'done', 'mainSha': 'def'})
        self.assertEqual(self.bridge.snapshot()['runs'][0]['integration']['mainSha'], 'def')

    def test_draft_never_changes_approved_plan(self):
        snapshot = self.bridge.snapshot()
        self.bridge.mutate('draft', {'baseRevision': snapshot['planRevision'], 'plan': self.plan})
        self.assertEqual(module.read(self.bridge.flow / 'plan.json'), self.plan)
        self.assertEqual(module.read(self.bridge.box / 'draft.json')['status'], 'pending_manager_review')

    def test_reject_cycles_and_gate_changes(self):
        cycle = json.loads(json.dumps(self.plan))
        cycle['tasks'][0]['dependsOn'] = ['B']
        contract = self.repo / 'contract.md'
        contract.write_text('fixture', encoding='utf-8')
        cycle['tasks'].append({'id': 'B', 'model': 'devin/swe-2', 'harness': 'omp', 'dependsOn': ['A'],
                              'worktree': str(self.repo / 'worktree'), 'allowedPaths': ['fixture.txt'],
                              'promptFile': str(contract), 'integrationPromptFile': str(contract),
                              'verificationFile': str(contract)})
        with self.assertRaisesRegex(ValueError, 'cycle'):
            self.bridge.validate_draft(cycle)
        invalid = json.loads(json.dumps(self.plan))
        invalid['gates']['G1'] = 'passed'
        with self.assertRaisesRegex(ValueError, 'gates'):
            self.bridge.validate_draft(invalid)

    def test_started_task_model_is_immutable(self):
        module.write(self.repo / 'tmp/agent-queue/own/manifest.json', {'workflowId': 'fixture', 'task': 'A'})
        changed = json.loads(json.dumps(self.plan))
        changed['tasks'][0]['model'] = 'another-model'
        with self.assertRaisesRegex(ValueError, 'immutable'):
            self.bridge.validate_draft(changed)

    def test_stale_draft_refused(self):
        with self.assertRaisesRegex(ValueError, 'changed'):
            self.bridge.mutate('draft', {'baseRevision': 'stale', 'plan': self.plan})

    def test_control_modes_use_existing_entrypoints(self):
        calls = []
        self.bridge.command = lambda script, mode: calls.append((script, mode))
        self.bridge.mutate('mode', {'mode': 'manual'})
        self.assertEqual(calls, [])  # Manual editing is not an instruction to stop scheduling.
        self.bridge.mutate('pause', {})
        self.assertEqual(calls, [('agent-workflow.ps1', 'Pause'), ('agent-gate-control.ps1', 'Pause')])
        calls.clear()
        self.bridge.mutate('mode', {'mode': 'auto'})
        self.assertEqual(calls, [])  # Mode choice does not silently resume paused work.
        module.write(self.bridge.gate / 'manager.json', {'status': 'needs_attention'})
        with self.assertRaisesRegex(ValueError, 'not ready'):
            self.bridge.mutate('resume', {})

    def test_apply_uses_approve_without_launch(self):
        module.write(self.bridge.gate / 'manager.json', {'status': 'ready'})
        module.write(self.bridge.flow / 'runtime.json', {'status': 'stopped'})
        module.write(self.bridge.flow / 'approval.json', {'sha256': 'fixture'})
        self.bridge.mutate('draft', {'baseRevision': self.bridge.snapshot()['planRevision'], 'plan': self.plan})
        calls = []
        self.bridge.command = lambda script, mode: calls.append((script, mode))
        self.bridge.mutate('apply', {})
        self.assertIn(('agent-workflow.ps1', 'Approve'), calls)
        self.assertFalse(any(mode in ('Resume', 'Run', 'Register') for _, mode in calls))
        self.assertEqual(module.read(self.bridge.box / 'draft.json')['status'], 'applied_paused')

    def test_apply_failure_restores_plan_and_approval(self):
        module.write(self.bridge.gate / 'manager.json', {'status': 'ready'})
        module.write(self.bridge.flow / 'runtime.json', {'status': 'stopped'})
        module.write(self.bridge.flow / 'approval.json', {'sha256': 'original'})
        plan_path = self.bridge.flow / 'plan.json'
        plan_path.write_bytes(b'\xef\xbb\xbf' + plan_path.read_bytes() + b'\r\n')
        original_plan_bytes = plan_path.read_bytes()
        original_approval_bytes = (self.bridge.flow / 'approval.json').read_bytes()
        self.bridge.mutate('draft', {'baseRevision': self.bridge.snapshot()['planRevision'], 'plan': self.plan})
        def command(script, mode):
            if mode == 'Approve':
                raise ValueError('contract failed')
        self.bridge.command = command
        with self.assertRaisesRegex(ValueError, 'contract failed'):
            self.bridge.mutate('apply', {})
        self.assertEqual(module.read(self.bridge.flow / 'plan.json'), self.plan)
        self.assertEqual(module.read(self.bridge.flow / 'approval.json'), {'sha256': 'original'})
        self.assertEqual(plan_path.read_bytes(), original_plan_bytes)
        self.assertEqual((self.bridge.flow / 'approval.json').read_bytes(), original_approval_bytes)

    def test_unknown_apply_is_not_resent(self):
        module.write(self.bridge.gate / 'manager.json', {'status': 'ready'})
        module.write(self.bridge.flow / 'runtime.json', {'status': 'stopped'})
        self.bridge.mutate('draft', {'baseRevision': self.bridge.snapshot()['planRevision'], 'plan': self.plan})
        module.write(self.bridge.box / 'apply-claim.json', {'status': 'inflight'})
        calls = []
        self.bridge.command = lambda script, mode: calls.append(mode)
        with self.assertRaisesRegex(ValueError, 'unknown'):
            self.bridge.mutate('apply', {})
        self.assertEqual(calls, [])

    def test_runtime_identity_and_bad_ids_rejected(self):
        changed = dict(self.plan, codexExecutable='untrusted.exe')
        with self.assertRaisesRegex(ValueError, 'immutable'):
            self.bridge.validate_draft(changed)
        changed = dict(self.plan, tasks=[{'id': [], 'model': 'devin/swe-2', 'harness': 'codex'}])
        with self.assertRaisesRegex(ValueError, 'identities'):
            self.bridge.validate_draft(changed)

    def test_apply_intent_is_persisted_while_manager_writes(self):
        module.write(self.bridge.gate / 'manager.json', {'status':'inflight'})
        self.bridge.mutate('draft', {'baseRevision':self.bridge.snapshot()['planRevision'],'plan':self.plan})
        calls=[]
        self.bridge.command=lambda script, mode:calls.append(mode)
        result=self.bridge.mutate('apply', {})
        self.assertEqual(result['draft']['status'],'queued_apply')
        self.assertEqual(calls,[])
        self.assertEqual(module.read(self.bridge.flow / 'plan.json'),self.plan)

    def test_apply_runs_real_native_approval_in_isolated_fixture(self):
        contract = self.repo / 'fixture.md'
        contract.write_text('No model call; native plan validation only.', encoding='utf-8')
        plan = dict(self.plan, schemaVersion=1, managerModel='devin/swe-2',
                    mechanismTest=True, codexExecutable='unused.exe')
        plan['tasks'] = [dict(self.plan['tasks'][0], allowedPaths=['fixture.txt'],
                            worktree=str(self.repo / 'worktree'), promptFile=str(contract),
                            integrationPromptFile=str(contract), verificationFile=str(contract))]
        module.write(self.bridge.flow / 'plan.json', plan)
        module.write(self.bridge.gate / 'manager.json', {'status': 'ready'})
        module.write(self.bridge.flow / 'runtime.json', {'status': 'stopped'})
        self.bridge.mutate('draft', {'baseRevision': self.bridge.snapshot()['planRevision'], 'plan': plan})
        calls = []
        def native_command(script, mode):
            calls.append(mode)
            result = module.subprocess.run(['pwsh', '-NoProfile', '-File',
                str(Path(__file__).with_name(script).resolve()), '-Mode', mode,
                '-Repository', str(self.repo)], cwd=self.repo, capture_output=True, timeout=30)
            if result.returncode:
                raise ValueError(result.stderr.decode('utf-8', errors='replace'))
        self.bridge.command = native_command
        self.bridge.mutate('apply', {})
        approval = module.read(self.bridge.flow / 'approval.json')
        self.assertEqual(approval['sha256'].lower(), hashlib.sha256((self.bridge.flow / 'plan.json').read_bytes()).hexdigest())
        self.assertEqual(approval['contracts'][str(contract)].lower(), hashlib.sha256(contract.read_bytes()).hexdigest())
        self.assertEqual(calls, ['Pause', 'Pause', 'Stop', 'Approve'])
        self.assertTrue((self.bridge.flow / 'paused').exists())
        self.assertTrue((self.bridge.gate / 'paused').exists())


if __name__ == '__main__':
    unittest.main()
