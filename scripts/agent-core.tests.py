import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('agent_core', Path(__file__).with_name('agent-core.py'))
core = importlib.util.module_from_spec(spec)
spec.loader.exec_module(core)


class CoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='fufucode-core-')
        self.addCleanup(self.temp.cleanup)

    def test_local_has_no_model_and_replay_has_one_record(self):
        event = {'eventId': 'run/delivery', 'type': 'delivery.received'}
        self.assertIsNone(core.route(self.temp.name, event)['model'])
        core.route(self.temp.name, event)
        self.assertEqual(len(list((Path(self.temp.name) / 'tmp/agent-core/events').glob('*.json'))), 1)
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            core.route(self.temp.name, dict(event, head='changed'))

    def test_semantic_and_review_are_distinct(self):
        self.assertEqual(core.route(self.temp.name, {'eventId': 'v', 'type': 'verification.local_passed'})['role'], 'validator')
        self.assertEqual(core.route(self.temp.name, {'eventId': 'r', 'type': 'verification.semantic_completed'})['role'], 'reviewer')

    def test_experts_need_evidence_and_only_propose(self):
        with self.assertRaises(ValueError):
            core.route(self.temp.name, {'eventId': 'x', 'type': 'architecture.conflict_confirmed'})
        result = core.route(self.temp.name, {'eventId': 'a', 'type': 'architecture.change_requested', 'evidence': ['user request']}, True)
        self.assertEqual(result['model'], ['devin/swe-2'] * 3)
        self.assertEqual(result['effects'], 'proposal_only')

    def test_failure_replays_do_not_escalate(self):
        with self.assertRaises(ValueError):
            core.route(self.temp.name, {'eventId': 'f', 'type': 'task.repeated_failure', 'failureIds': ['same'] * 3})
        self.assertEqual(core.route(self.temp.name, {'eventId': 'f', 'type': 'task.repeated_failure', 'failureCategory': 'verification', 'failureIds': ['one', 'two', 'three']})['role'], 'architect')

    def test_exact_json_wrapper_and_mixed_output_rejected(self):
        text = '{"role":"architect","evidence":[],"findings":[]}'
        self.assertEqual(core.decode_advisory('```json\n' + text + '\n```', 'architect')['role'], 'architect')
        with self.assertRaises(ValueError):
            core.decode_advisory('Here is the answer: ' + text, 'architect')
        with self.assertRaises(ValueError):
            core.decode_advisory(text, 'validator')


if __name__ == '__main__':
    unittest.main()
