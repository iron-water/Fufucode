import copy
import tempfile
import unittest
from pathlib import Path
from parallel_control import Store, initial, reduce, prompt_for


class PolicyTests(unittest.TestCase):
    def setUp(self):
        self.state = initial()
        self.sequence = 0

    def act(self, action, data, actor='human'):
        self.sequence += 1
        c = dict(commandId=f'c{self.sequence}', baseRevision=self.state['revision'], action=action, data=data)
        self.state = reduce(self.state, c, actor)
        return c

    def create(self, pid, **extra):
        spec = dict(id=pid, title=pid, goal='deliver '+pid, scope=[pid+'.cs'], acceptance=['behavior verified'],
                    harness='codex', model='devin/swe-2')
        spec.update(extra)
        self.act('package.create', spec)
        return spec

    def approve(self, pid):
        self.act('decide', dict(id='plan-'+pid, choice='approve'))

    def deliver(self, pid, part='whole', choice='accept'):
        p = self.state['packages'][pid]
        c = self.act('delivery.record', dict(packageId=pid, version=p['version'], partId=part,
                                           head='sha-'+str(self.sequence), evidence=['test.log']))
        self.act('decide', dict(id='review-'+c['commandId'], choice=choice, reason='fix this part'))

    def test_manual_default_and_mode_not_execution(self):
        self.create('A'); self.approve('A')
        self.assertEqual(self.state['mode'], 'manual')
        self.assertFalse(self.state['intents'])
        self.act('mode', {'mode':'parallel'})
        self.assertEqual(self.state['intents']['execute-A-1']['status'], 'awaiting_adapter')
        self.assertEqual(self.state['packages']['A']['status'], 'approved')
        self.act('mode', {'mode':'automatic'})
        self.create('B'); self.approve('B')
        self.assertNotIn('execute-B-1', self.state['intents'])

    def test_replay_and_cas(self):
        c = self.act('mode', {'mode':'parallel'})
        self.assertIs(reduce(self.state, c), self.state)
        with self.assertRaisesRegex(ValueError, '同一命令'):
            reduce(self.state, dict(c, data={'mode':'manual'}))
        with self.assertRaisesRegex(ValueError, '状态已更新'):
            reduce(self.state, dict(c, commandId='new', baseRevision=0))

    def test_competition_never_integrates_all_candidates(self):
        self.create('A'); self.create('B')
        self.act('group.create', dict(id='experiment', members=['A','B'], relation='competitive',
                                     baseline='base-sha', criteria=['correctness','cost']))
        self.approve('A'); self.approve('B'); self.deliver('A'); self.deliver('B')
        self.assertFalse(any(d['kind']=='integration' for d in self.state['decisions'].values()))
        self.act('decide', dict(id='choice-experiment', choice='select', winner='B'))
        integration = [d for d in self.state['decisions'].values() if d['kind']=='integration']
        self.assertEqual([d['targets'] for d in integration], [['B']])
        self.assertEqual(self.state['packages']['A']['status'], 'accepted')

    def test_unreviewed_candidate_cannot_win(self):
        self.create('A'); self.create('B')
        self.act('group.create', dict(id='g', members=['A','B'], relation='competitive', baseline='sha',criteria=['test']))
        with self.assertRaisesRegex(ValueError, '通过审核'):
            self.act('decide', dict(id='choice-g', choice='select', winner='A'))

    def test_complementary_waits_for_evidence(self):
        self.create('A'); self.create('B',artifact='evidence')
        self.act('group.create', dict(id='g', members=['A','B'], relation='complementary'))
        self.approve('A'); self.approve('B'); self.deliver('A')
        self.assertFalse(any(d['kind']=='integration' for d in self.state['decisions'].values()))
        self.deliver('B')
        self.assertEqual([d['targets'] for d in self.state['decisions'].values() if d['kind']=='integration'], [['A']])

    def test_pipeline_waits_for_integration_report(self):
        self.act('mode', {'mode':'parallel'}); self.create('A'); self.create('B')
        self.act('group.create', dict(id='g', members=['A','B'], relation='pipeline'))
        self.approve('A'); self.approve('B'); self.deliver('A')
        self.assertNotIn('execute-B-1', self.state['intents'])
        d = next(d for d in self.state['decisions'].values() if d['kind']=='integration')
        self.act('decide',dict(id=d['id'],choice='approve'))
        self.assertNotIn('execute-B-1', self.state['intents'])
        self.act('integration.report',dict(intentId=d['id'],head='main-sha',evidence=['verify.log'],passed=True),'adapter')
        self.assertIn('execute-B-1', self.state['intents'])

    def test_human_cannot_forge_runtime_or_integration(self):
        with self.assertRaisesRegex(ValueError, '适配器'):
            self.act('execution.report', {})
        with self.assertRaisesRegex(ValueError, '人类授权'):
            self.act('mode', {'mode':'parallel'}, 'adapter')

    def test_partial_rework_preserves_other_part(self):
        self.create('A'); self.approve('A'); self.deliver('A','one'); self.deliver('A','two','rework')
        one = copy.deepcopy(self.state['packages']['A']['parts']['one'])
        self.deliver('A','two')
        self.assertEqual(self.state['packages']['A']['parts']['one'], one)
        self.assertEqual(self.state['packages']['A']['parts']['two']['revision'],2)
        self.assertTrue(self.state['packages']['A']['history'])

    def test_cycles_rejected_without_mutation(self):
        self.create('A'); before=copy.deepcopy(self.state)
        with self.assertRaisesRegex(ValueError,'依赖'):
            self.create('B',dependsOn=['B'])
        self.assertEqual(self.state,before)

    def test_running_restructure_waits_for_stopped_writer(self):
        self.act('mode',{'mode':'parallel'}); spec=self.create('A'); self.approve('A')
        self.act('execution.report',dict(intentId='execute-A-1',status='running',threadId='original'),'adapter')
        new=dict(spec,id='B',model='other-explicit-model')
        c=self.act('restructure',dict(sources=['A'],replacements=[new]))
        did='restructure-'+c['commandId']
        self.act('decide',dict(id=did,choice='approve'))
        self.assertEqual(self.state['decisions'][did]['status'],'waiting_stop')
        self.assertNotIn('B',self.state['packages'])
        stop=next(i for i in self.state['intents'].values() if i['kind']=='stop')
        self.act('execution.report',dict(intentId=stop['id'],status='stopped',threadId='original'),'adapter')
        self.act('decide',dict(id=did,choice='approve'))
        self.assertEqual(self.state['packages']['A']['status'],'superseded')
        self.assertEqual(self.state['packages']['A']['model'],'devin/swe-2')
        self.assertIsNone(self.state['packages']['B']['threadId'])

    def test_merge_remaps_downstream_and_requires_new_approval(self):
        a=self.create('A'); self.create('B'); self.create('C',dependsOn=['A','B']); self.approve('C')
        c=self.act('restructure',dict(sources=['A','B'],replacements=[dict(a,id='AB')]))
        self.act('decide',dict(id='restructure-'+c['commandId'],choice='approve'))
        self.assertEqual(self.state['packages']['C']['dependsOn'],['AB'])
        self.assertFalse(self.state['packages']['C']['approved'])
        self.assertEqual(self.state['packages']['C']['version'],2)

    def test_group_restructure_requires_fresh_choice(self):
        a=self.create('A'); self.create('B')
        self.act('group.create',dict(id='g',members=['A','B'],relation='competitive',baseline='sha',criteria=['test']))
        c=self.act('restructure',dict(sources=['A'],replacements=[dict(a,id='A2')]))
        self.act('decide',dict(id='restructure-'+c['commandId'],choice='approve'))
        self.assertEqual(self.state['decisions']['choice-g']['status'],'superseded')
        self.assertEqual(self.state['decisions']['choice-g-2']['status'],'pending')

    def test_versioned_knowledge_targets_only_dependents(self):
        self.create('A',knowledgeRefs=['contract']); self.create('B'); self.approve('A'); self.approve('B'); self.deliver('A')
        c=self.act('knowledge.propose',dict(key='contract',text='v1 interface',source='design.md'))
        self.assertFalse(self.state['knowledge'])
        self.act('decide',dict(id='knowledge-'+c['commandId'],choice='approve'))
        d=next(d for d in self.state['decisions'].values() if d['kind']=='adoption')
        self.assertEqual(d['targets'],['A'])
        oldreview=next(d for d in self.state['decisions'].values() if d['kind']=='integration')
        with self.assertRaisesRegex(ValueError,'条件已变化'):
            self.act('decide',dict(id=oldreview['id'],choice='approve'))
        self.act('decide',dict(id=d['id'],choice='approve'))
        self.assertEqual(self.state['packages']['A']['version'],2)
        self.assertFalse(self.state['packages']['A']['parts'])
        self.assertEqual(self.state['packages']['B']['version'],1)
        self.assertIn('v1 interface',prompt_for(self.state,'A'))

    def test_answers_are_not_automatically_public_facts(self):
        self.create('A'); self.create('B')
        c=self.act('question.ask',dict(to='B',text='why?',**{'from':'A'}))
        self.act('question.answer',dict(id=c['commandId'],text='because'))
        self.assertFalse(self.state['knowledge'])

    def test_unknown_writer_blocks_restructure(self):
        self.act('mode',{'mode':'parallel'}); a=self.create('A'); self.approve('A')
        self.act('execution.report',dict(intentId='execute-A-1',status='unknown',threadId='original'),'adapter')
        c=self.act('restructure',dict(sources=['A'],replacements=[dict(a,id='A2')]))
        with self.assertRaisesRegex(ValueError,'状态不明'):
            self.act('decide',dict(id='restructure-'+c['commandId'],choice='approve'))
        self.assertNotIn('A2',self.state['packages'])

    def test_store_reopen_and_command_receipt_are_atomic(self):
        with tempfile.TemporaryDirectory(prefix='parallel-policy-') as directory:
            path=Path(directory)/'state.json'; store=Store(path)
            c=dict(commandId='saved',baseRevision=0,action='mode',data={'mode':'parallel'})
            store.apply(c); reopened=Store(path); reopened.apply(c)
            self.assertEqual(reopened.read()['revision'],1)
            self.assertFalse(reopened.snapshot()['capabilities']['runtimeDispatch'])


if __name__=='__main__':
    unittest.main()
