"""Upper-level development policy. No harness, subprocess, Git or model calls.

The reducer produces intents, never pretends they were executed. The small JSON
store supports the discussion workbench; U1/runtime integration is a separate port.
"""
import copy
import hashlib
import json
import os
import re
from contextlib import contextmanager
from pathlib import Path


def initial():
    return dict(schemaVersion=1, revision=0, mode='manual', packages={}, groups={},
                decisions={}, knowledge={}, questions={}, intents={}, events=[], receipts={})


def required(value, label):
    if not isinstance(value, str) or not value.strip():
        raise ValueError(label + '不能为空')
    return value.strip()


def identifier(value):
    value = required(value, 'ID')
    if not re.fullmatch(r'[A-Za-z0-9_-]{1,80}', value):
        raise ValueError('ID只允许字母、数字、下划线和短横线')
    return value


def lines(value, label):
    if not isinstance(value, list) or not value:
        raise ValueError(label + '至少一项')
    return [required(v, label) for v in value]


def package(spec, parents=None):
    return dict(id=identifier(spec.get('id')), title=required(spec.get('title'), '标题'),
                goal=required(spec.get('goal'), '目标'), scope=lines(spec.get('scope'), '范围'),
                acceptance=lines(spec.get('acceptance'), '验收要求'),
                harness=required(spec.get('harness'), 'Harness'), model=required(spec.get('model'), '模型'),
                artifact=spec.get('artifact', 'code'), dependsOn=spec.get('dependsOn', []),
                knowledgeRefs=spec.get('knowledgeRefs', []), adoptedKnowledge={},
                version=1, status='draft', threadId=None, parts={}, history=[],
                parents=parents or [], approved=False)


def validate_graph(s):
    active = {k: p for k, p in s['packages'].items() if p['status'] != 'superseded'}
    for p in active.values():
        if p['artifact'] not in ('code', 'evidence'):
            raise ValueError('成果类型必须为code或evidence')
        if not isinstance(p['dependsOn'], list) or any(d not in active for d in p['dependsOn']):
            raise ValueError('依赖不存在或已被替代')
        if not isinstance(p['knowledgeRefs'], list) or any(not isinstance(k, str) for k in p['knowledgeRefs']):
            raise ValueError('契约引用必须为ID数组')
    done = set()
    while True:
        ready = {k for k, p in active.items() if set(p['dependsOn']) <= done}
        if ready <= done:
            break
        done |= ready
    if len(done) != len(active):
        raise ValueError('依赖形成循环')


def decision(s, decision_id, kind, targets, **extra):
    s['decisions'].setdefault(decision_id, dict(id=decision_id, kind=kind, targets=targets, status='pending',
        versions={i: s['packages'][i]['version'] for i in targets}, **extra))


def intent(s, key, kind, p, **extra):
    s['intents'].setdefault(key, dict(id=key, kind=kind, packageId=p['id'], version=p['version'],
        status='awaiting_adapter', **extra))


def accepted(p):
    return bool(p['parts']) and all(x['status'] == 'accepted' for x in p['parts'].values())


def can_integrate(s, p):
    if not accepted(p) or p['artifact'] != 'code' or p['status'] == 'superseded':
        return False
    if any(d['kind'] == 'adoption' and p['id'] in d['targets'] and
           d['status'] in ('pending', 'waiting_stop') for d in s['decisions'].values()):
        return False
    for g in s['groups'].values():
        if p['id'] not in g['members']:
            continue
        if g['relation'] == 'competitive' and g.get('winner') != p['id']:
            return False
        if g['relation'] == 'complementary' and not all(accepted(s['packages'][m]) for m in g['members']):
            return False
    return True


def refresh(s):
    for d in s['decisions'].values():
        if d['status'] not in ('pending', 'waiting_stop'):
            continue
        if any(s['packages'][i]['version'] != v or s['packages'][i]['status'] == 'superseded'
               for i, v in d['versions'].items()):
            d['status'] = 'superseded'
        elif d['kind'] == 'integration' and s['packages'][d['targets'][0]]['parts'] != d['parts']:
            d['status'] = 'superseded'
    for op in s['intents'].values():
        p = s['packages'][op['packageId']]
        if op['status'] == 'awaiting_adapter' and (op['version'] != p['version'] or
                p['status'] == 'superseded' or
                (op['kind'] == 'integrate' and not can_integrate(s, p)) or
                (op['kind'] == 'repair' and p['parts'].get(op['partId'], {}).get('status') != 'rework')):
            op['status'] = 'superseded'
    for p in s['packages'].values():
        if p['status'] == 'superseded':
            continue
        adopting = any(d['kind'] == 'adoption' and p['id'] in d['targets'] and
                       d['status'] in ('pending', 'waiting_stop') for d in s['decisions'].values())
        if p['approved'] and p['status'] in ('approved', 'waiting') and not adopting:
            ready = all(s['packages'][d]['status'] == 'integrated' or
                        (s['packages'][d]['artifact'] == 'evidence' and accepted(s['packages'][d]))
                        for d in p['dependsOn'])
            p['status'] = 'approved' if ready else 'waiting'
            if ready and s['mode'] == 'parallel':
                intent(s, f"execute-{p['id']}-{p['version']}", 'execute', p)
        if can_integrate(s, p) and p['status'] != 'integrated':
            fingerprint = hashlib.sha256(json.dumps(p['parts'], sort_keys=True).encode()).hexdigest()[:16]
            decision(s, f"integrate-{p['id']}-{p['version']}-{fingerprint}", 'integration', [p['id']],
                     parts=copy.deepcopy(p['parts']))


def reduce(state, command, actor='human'):
    """One command -> one new revision, including decisions, receipts and intents."""
    cid = identifier(command.get('commandId'))
    digest = hashlib.sha256(json.dumps([actor, command], sort_keys=True, ensure_ascii=False).encode()).hexdigest()
    previous = state['receipts'].get(cid)
    if previous:
        if previous['digest'] != digest:
            raise ValueError('同一命令ID不能改变内容')
        return state
    if command.get('baseRevision') != state['revision']:
        raise ValueError('状态已更新，请刷新后重试')
    s = copy.deepcopy(state)
    action, data = required(command.get('action'), '操作'), command.get('data', {})
    if action.endswith('.report'):
        if actor != 'adapter':
            raise ValueError('执行证据只能由已接入的执行适配器报告')
    elif actor != 'human':
        raise ValueError('此决定需要人类授权')

    if action == 'mode':
        if data.get('mode') not in ('manual', 'parallel', 'automatic'):
            raise ValueError('未知模式')
        s['mode'] = data['mode']  # Never pauses or interrupts existing execution.
    elif action == 'package.create':
        p = package(data)
        if p['id'] in s['packages']:
            raise ValueError('工作包ID已存在')
        s['packages'][p['id']] = p
        decision(s, 'plan-' + p['id'], 'plan', [p['id']])
    elif action == 'group.create':
        gid, members, relation = identifier(data.get('id')), lines(data.get('members'), '成员'), data.get('relation')
        if gid in s['groups'] or len(set(members)) != len(members) or len(members) < 2:
            raise ValueError('组ID须唯一，至少两个不同工作包')
        if relation not in ('independent', 'competitive', 'complementary', 'pipeline'):
            raise ValueError('未知并行关系')
        packages = [s['packages'][i] for i in members]
        if any(p['status'] != 'draft' for p in packages):
            raise ValueError('先确定协作关系，再批准工作包；已启动工作请用重组')
        group = dict(id=gid, members=members, relation=relation, winner=None)
        if relation == 'competitive':
            group['criteria'] = lines(data.get('criteria'), '共同评价标准')
            group['baseline'] = required(data.get('baseline'), '共同基线')
            if any(g['relation'] == 'competitive' and set(members) & set(g['members']) for g in s['groups'].values()):
                raise ValueError('一个工作包不能参加多个竞争组')
            decision(s, 'choice-' + gid, 'choice', members, groupId=gid)
        if relation == 'pipeline':
            for before, after in zip(members, members[1:]):
                s['packages'][after]['dependsOn'] = sorted(set(s['packages'][after]['dependsOn'] + [before]))
        s['groups'][gid] = group
    elif action == 'delivery.record' or action == 'delivery.report':
        p = s['packages'][data['packageId']]
        if not p['approved'] or p['status'] in ('running', 'superseded', 'integrated'):
            raise ValueError('须为已批准、已停止且未集成的工作包')
        if data.get('version') != p['version']:
            raise ValueError('交付工作包版本过期')
        part_id, head = identifier(data.get('partId')), required(data.get('head'), '成果版本/HEAD')
        evidence = lines(data.get('evidence'), '证据引用')
        old = p['parts'].get(part_id)
        if old:
            if old['status'] != 'rework':
                raise ValueError('只有被打回的部分可以重新交付')
            p['history'].append(copy.deepcopy(old))
        p['parts'][part_id] = dict(id=part_id, head=head, evidence=evidence, status='delivered',
            revision=(old['revision'] + 1 if old else 1), source=actor)
        p['status'] = 'delivered'
        decision(s, 'review-' + cid, 'review', [p['id']], partId=part_id,
                 partRevision=p['parts'][part_id]['revision'], head=head)
    elif action == 'restructure':
        sources = lines(data.get('sources'), '原工作包')
        if len(sources) != len(set(sources)):
            raise ValueError('原工作包重复')
        if any(s['packages'][i]['status'] in ('integrated', 'superseded') for i in sources):
            raise ValueError('已集成或替代的工作包不能重组')
        affected = [g for g in s['groups'].values() if set(sources) & set(g['members'])]
        if len(affected) > 1:
            raise ValueError('跨多个协作组重组须先明确新关系；不隐式混合不同协作语义')
        replacements = [package(p, sources) for p in data.get('replacements', [])]
        ids = [p['id'] for p in replacements]
        if not ids or len(ids) != len(set(ids)) or any(i in s['packages'] for i in ids):
            raise ValueError('后继工作包必须使用新的唯一ID')
        trial = copy.deepcopy(s)
        apply_restructure(trial, sources, replacements)
        validate_graph(trial)
        decision(s, 'restructure-' + cid, 'restructure', sources, replacements=replacements)
    elif action == 'knowledge.propose':
        key = identifier(data.get('key'))
        # A proposal remains distinct from a published fact.
        decision(s, 'knowledge-' + cid, 'knowledge', [], key=key,
                 text=required(data.get('text'), '公共事实'), source=required(data.get('source'), '来源'),
                 previous=len(s['knowledge'].get(key, [])))
    elif action == 'question.ask':
        source, target = data['from'], data['to']
        if source not in s['packages'] or target not in s['packages'] or source == target:
            raise ValueError('问题必须指向另一工作包')
        s['questions'][cid] = dict(id=cid, sender=source, target=target,
                                 text=required(data.get('text'), '问题'), answer=None, source='human')
    elif action == 'question.answer':
        q = s['questions'][data['id']]
        if q['answer'] is not None:
            raise ValueError('已有答复不可覆盖，请创建后续问题')
        q['answer'] = required(data.get('text'), '答复')
        q['answerSource'] = 'human_recorded'  # Not automatically public knowledge.
    elif action == 'decide':
        d = s['decisions'][data['id']]
        if d['status'] not in ('pending', 'waiting_stop'):
            raise ValueError('该决定已处理')
        if any(s['packages'][k]['version'] != v or s['packages'][k]['status'] == 'superseded'
               for k, v in d['versions'].items()):
            raise ValueError('决定对应的工作包已变化')
        choice = data.get('choice')
        if choice not in ('approve', 'accept', 'rework', 'defer', 'reject', 'select'):
            raise ValueError('未知决定')
        if choice in ('defer', 'reject'):
            d.update(status=choice, reason=required(data.get('reason'), '理由'))
        else:
            decide(s, d, data)
    elif action in ('execution.report', 'integration.report'):
        operation = s['intents'][data['intentId']]
        p = s['packages'][operation['packageId']]
        if operation['version'] != p['version'] or p['status'] == 'superseded':
            raise ValueError('执行结果版本过期')
        stopping = (action == 'execution.report' and data.get('status') == 'stopped' and
                    operation.get('result', {}).get('status') == 'running')
        if operation['status'] != 'awaiting_adapter' and not stopping:
            raise ValueError('执行意图已有结果')
        if action == 'execution.report':
            if operation['kind'] not in ('execute', 'stop', 'repair'):
                raise ValueError('执行意图类型不匹配')
            if data.get('status') not in ('running', 'stopped', 'unknown'):
                raise ValueError('未知执行结果')
            thread = required(data.get('threadId'), '执行线程身份')
            if p['threadId'] and p['threadId'] != thread:
                raise ValueError('原线程身份不能静默变更')
            p['threadId'] = thread
            p['status'] = data['status']
        else:
            if operation['kind'] != 'integrate' or not can_integrate(s, p):
                raise ValueError('该成果不允许集成')
            required(data.get('head'), '集成HEAD')
            lines(data.get('evidence'), '集成验证证据')
            p['status'] = 'integrated' if data.get('passed') is True else 'integration_failed'
        operation.update(status='reported', result=copy.deepcopy(data))
    else:
        raise ValueError('未知上层控制命令')
    validate_graph(s)
    refresh(s)
    s['revision'] += 1
    s['events'].append(dict(sequence=s['revision'], commandId=cid, action=action, actor=actor,
                            data=copy.deepcopy(data)))
    s['receipts'][cid] = dict(digest=digest, revision=s['revision'])
    return s


def apply_restructure(s, sources, replacements):
    new_ids = [p['id'] for p in replacements]
    for i in sources:
        s['packages'][i]['status'] = 'superseded'
    for p in replacements:
        s['packages'][p['id']] = copy.deepcopy(p)
        decision(s, 'plan-' + p['id'], 'plan', [p['id']])
    for g in s['groups'].values():
        if set(sources) & set(g['members']):
            g['members'] = list(dict.fromkeys([i for i in g['members'] if i not in sources] + new_ids))
            g['version'] = g.get('version', 1) + 1
            g['winner'] = None
            for d in s['decisions'].values():
                if d.get('groupId') == g['id'] and d['status'] in ('pending', 'waiting_stop'):
                    d['status'] = 'superseded'
            if g['relation'] == 'competitive':
                decision(s, f"choice-{g['id']}-{g['version']}", 'choice', g['members'], groupId=g['id'])
    for p in s['packages'].values():
        if p['status'] == 'superseded' or p['id'] in new_ids:
            continue
        if set(p['dependsOn']) & set(sources):
            if p['status'] not in ('draft', 'approved', 'waiting'):
                raise ValueError('已有成果或在途依赖方须一并协调，不自动改线')
            p['dependsOn'] = sorted(set(p['dependsOn']) - set(sources) | set(new_ids))
            p['version'] += 1
            p['approved'] = False
            p['status'] = 'draft'
            decision(s, f"plan-{p['id']}-{p['version']}", 'plan', [p['id']])
    for d in s['decisions'].values():
        if d['status'] == 'pending' and set(d['targets']) & set(sources):
            d['status'] = 'superseded'
    for op in s['intents'].values():
        if op['packageId'] in sources and op['status'] == 'awaiting_adapter':
            op['status'] = 'superseded'


def decide(s, d, data):
    kind, choice = d['kind'], data['choice']
    allowed = dict(plan={'approve'}, review={'accept', 'rework'}, integration={'approve'},
                   choice={'select'}, restructure={'approve'}, knowledge={'approve'}, adoption={'approve'})
    if choice not in allowed[kind]:
        raise ValueError('决定不适用于此事项')
    if kind in ('restructure', 'adoption'):
        uncertain = [s['packages'][i] for i in d['targets'] if s['packages'][i]['status'] == 'unknown']
        if uncertain:
            raise ValueError('执行状态不明，必须先对账')
        running = [s['packages'][i] for i in d['targets'] if s['packages'][i]['status'] == 'running']
        if running:
            for p in running:
                intent(s, 'stop-' + d['id'] + '-' + p['id'], 'stop', p)
            d['status'] = 'waiting_stop'
            return
    if kind == 'plan':
        p = s['packages'][d['targets'][0]]
        if p['status'] != 'draft':
            raise ValueError('工作包已不处于待批准状态')
        p.update(approved=True, status='approved')
        p['adoptedKnowledge'] = {k: len(s['knowledge'][k]) for k in p['knowledgeRefs'] if k in s['knowledge']}
    elif kind == 'review':
        p = s['packages'][d['targets'][0]]
        part = p['parts'][d['partId']]
        if part['revision'] != d['partRevision'] or part['head'] != d['head']:
            raise ValueError('交付版本已经变化')
        part['status'] = 'accepted' if choice == 'accept' else 'rework'
        if choice == 'rework':
            part['reason'] = required(data.get('reason'), '修复要求')
            intent(s, 'repair-' + d['id'], 'repair', p, partId=part['id'], reason=part['reason'],
                   threadId=p['threadId'], model=p['model'], harness=p['harness'])
        p['status'] = 'accepted' if accepted(p) else 'rework' if choice == 'rework' else 'delivered'
    elif kind == 'choice':
        winner = data.get('winner')
        if winner not in d['targets'] or not accepted(s['packages'][winner]):
            raise ValueError('只能选择本组已通过审核的方案')
        s['groups'][d['groupId']]['winner'] = winner
        d['winner'] = winner  # Other candidates remain inspectable, not deleted.
    elif kind == 'integration':
        p = s['packages'][d['targets'][0]]
        if p['parts'] != d['parts'] or not can_integrate(s, p):
            raise ValueError('成果或协作条件已变化')
        intent(s, d['id'], 'integrate', p, parts=copy.deepcopy(p['parts']))
    elif kind == 'restructure':
        apply_restructure(s, d['targets'], d['replacements'])
    elif kind == 'knowledge':
        history = s['knowledge'].setdefault(d['key'], [])
        if len(history) != d['previous']:
            raise ValueError('公共事实已经更新，需要重新提出')
        history.append(dict(version=len(history) + 1, text=d['text'], source=d['source'], decisionId=d['id']))
        for p in s['packages'].values():
            if d['key'] in p['knowledgeRefs'] and p['status'] not in ('draft', 'superseded', 'integrated'):
                decision(s, 'adopt-' + d['id'] + '-' + p['id'], 'adoption', [p['id']],
                         key=d['key'], knowledgeVersion=len(history))
    elif kind == 'adoption':
        p = s['packages'][d['targets'][0]]
        if len(s['knowledge'].get(d['key'], [])) != d['knowledgeVersion']:
            raise ValueError('公共事实已有更新版本')
        p['history'].append(dict(version=p['version'], parts=copy.deepcopy(p['parts'])))
        p['version'] += 1
        p['parts'] = {}
        p['adoptedKnowledge'][d['key']] = d['knowledgeVersion']
        p['status'] = 'approved' if p['approved'] else 'draft'
        if not p['approved']:
            decision(s, f"plan-{p['id']}-{p['version']}", 'plan', [p['id']])
    d.update(status='resolved', choice=choice, decidedBy='human')


def prompt_for(state, pid):
    p = state['packages'][pid]
    return '\n'.join([f"模型：{p['model']}；Harness：{p['harness']}；工作包：{pid} v{p['version']}",
        '目标：' + p['goal'], '允许范围：' + '；'.join(p['scope']), '验收：' + '；'.join(p['acceptance']),
        '公共知识：' + json.dumps({k: state['knowledge'][k][v-1] for k, v in p['adoptedKnowledge'].items()}, ensure_ascii=False),
        '协作关系：' + json.dumps([g for g in state['groups'].values() if pid in g['members']], ensure_ascii=False),
        '交付须包含成果版本、各部分及验证证据。范围或公共契约变化先提交协调请求。',
        '其他候选实现不得自行合并；不直接修改主线；不自动变更模型或接管未知会话。'])


class Store:
    def __init__(self, path):
        self.path = Path(path)

    def read(self):
        return json.loads(self.path.read_text(encoding='utf-8')) if self.path.exists() else initial()

    @contextmanager
    def locked(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.path.with_suffix('.lock').open('a+b') as lock:
            lock.seek(0)
            if os.name == 'nt':
                import msvcrt
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            try:
                yield
            finally:
                lock.seek(0)
                if os.name == 'nt':
                    msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    fcntl.flock(lock, fcntl.LOCK_UN)

    def apply(self, command, actor='human'):
        with self.locked():
            old = self.read()
            new = reduce(old, command, actor)
            if new is not old:
                temp = self.path.with_suffix('.new')
                with temp.open('w', encoding='utf-8') as f:
                    json.dump(new, f, ensure_ascii=False, indent=2)
                    f.flush()
                    os.fsync(f.fileno())
                os.replace(temp, self.path)
            return new

    def snapshot(self):
        s = self.read()
        return dict(state=s, prompts={i: prompt_for(s, i) for i in s['packages']},
                    capabilities=dict(upperControl=True, runtimeDispatch=False, nativeIntegration=False,
                                      automaticPolicy=False))
