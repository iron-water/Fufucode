"""Materialize the authorized project roadmap into explicit executable contracts.

Prepare only; native Approve and Resume remain the activation boundary.
"""
import argparse
import json
from pathlib import Path
import re


def prepare(repository):
    repo = Path(repository).resolve()
    box = repo / 'tmp/agent-flow'
    plan = json.loads((box / 'plan.json').read_text(encoding='utf-8-sig'))
    prompts = (repo / 'docs/tasks/launch-prompts.md').read_text(encoding='utf-8-sig')
    sections = dict(re.findall(r'^## ([A-Z][A-Z0-9]*)[^\n]*\n(.*?)(?=^## |\Z)', prompts, re.M | re.S))
    contracts = repo / 'docs/automation/contracts'
    contracts.mkdir(parents=True, exist_ok=True)
    # Scope is intentionally explicit. Legacy owners are never launched again.
    specs = {
      'U1': ('codex', ['src/Fufucode.Core/Automation/*'], [], ['G0'], [], 'Automation'),
      'O6-RULES': ('omp', ['src/Fufucode.Core/Scheduling/*'], [], ['G0'], [], 'Scheduling'),
      'U3': ('codex', ['src/Fufucode.App/Features/Automation/*'], ['U1'], ['G0'], ['U2'], 'App.Automation'),
      'M0': ('omp', ['docs/frontend-prototype/mobile/*'], [], ['G0'], [], None),
      'D0': ('codex', ['scripts/packaging/*'], [], ['G0'], [], None),
      'D2': ('codex', ['src/Fufucode.App/Infrastructure/ConPtyTerminalSession.cs','src/Fufucode.App/Infrastructure/TerminalBuffer.cs','src/Fufucode.App/Views/Terminal*','src/Fufucode.App/ViewModels/TerminalDockViewModel.cs'], [], ['G1'], [], None),
      'D3': ('codex', ['src/Fufucode.App/Views/Browser*','src/Fufucode.App/Infrastructure/Browser/*'], [], ['G1'], [], None),
      'D4': ('codex', ['src/Fufucode.Core/GitWorktreeService.cs','src/Fufucode.App/Integration/Git*'], ['D4-SNAPSHOT'], ['G1'], [], None),
      'D5': ('codex', ['src/Fufucode.Core/Host/*','src/Fufucode.App/Integration/Host*'], [], ['G1'], [], 'Host'),
      'U2': ('codex', ['src/Fufucode.Core/AutomationHost/*'], ['U1','D5'], ['G1'], [], 'AutomationHost'),
      'O4': ('omp', ['src/Fufucode.App/Features/Review/*'], ['D4'], ['G1'], [], 'App.Review'),
      'O5': ('omp', ['src/Fufucode.App/Views/ExtensionsPage*','src/Fufucode.App/ViewModels/ExtensionsPageViewModel.cs'], [], ['G1'], [], None),
      'O6': ('omp', ['src/Fufucode.App/Features/Schedules/*'], ['O6-RULES','D5'], ['G2'], [], 'App.Schedules'),
      'O7': ('omp', ['src/Fufucode.App/Features/Images/*'], [], ['G0','O3-native'], [], 'App.Images'),
      'D6': ('codex', ['src/Fufucode.Core/Remote/*','src/Fufucode.App/Features/Remote/*'], ['D5','M0'], ['G2'], [], 'Remote'),
      'S2': ('codex', ['src/Fufucode.App/Styles/*','src/Fufucode.App/Infrastructure/WorkbenchAppearance.cs'], [], ['S2'], [], None),
      'V': ('codex', [], [], ['native-tools-available'], [], None),
      'R': ('codex', ['packaging/*','scripts/packaging/*'], ['D0','D2','D3','D4','D5','D6','O4','O5','O6','O7','S2','U2','U3','V'], ['G1','G2','O1-native','O3-native','O2-integrated'], [], None),
      'D8': ('codex', ['src/Fufucode.App/Infrastructure/Browser/Agent/*'], ['D3'], ['G3'], [], None),
      'D9': ('codex', ['src/Fufucode.Core/Remote/External/*'], ['D6'], ['G3'], [], None),
      'O8': ('omp', ['src/Fufucode.App/Features/PrReview/*'], ['O4'], ['G3','external-comments-authorized'], [], None),
      'S3': ('codex', ['src/Fufucode.App/Styles/Advanced/*'], ['S2'], ['G3'], [], None),
    }
    descriptions = {
      'U1': '依据automation-workbench实现独立持久规则：Thread/Task/DeliveryPart/Revision、局部accept/rework/blocked/defer、事件/命令去重、投递不明冻结、版本化DAG、撤销围栏、重建身份、崩溃恢复、单写入租约。多个任务属于同线程，部分打回不改变其他已接受部分。提供真实临时存储行为测试。不得修改现有会话宿主或UI。',
      'O6-RULES': '只实现独立本地调度规则与持久状态：一次性/周期、时区/DST、漏跑策略、幂等、取消、恢复、可注入时钟。规则计算不能启动模型或访问真实用户数据。不接后台宿主/App共享入口；O6的后端接线仍依赖D5/G2。行为测试覆盖春季跳时、秋季重复、重启、取消和重复触发。',
      'U2': '实现U1规则与已验D5宿主间的受管执行适配，版本化项目任务图自动编排派工、事件审核、原会话部分修复、撤销围栏、重建、任务重排、单主线写入者。沿用官方harness，不将prompt冒充状态机。测试模型显式devin/swe-2。',
      'U3': '实现独立Features/Automation图模块：线程节点含多任务/部分进度、大层级、事件动画、显示未运行过滤、自动/手动、修改拓扑模型、撤销/重建、版本记录。绑定U1真实契约，可注入执行控制适配；未集成U2时只交module_ready和待接线，不假接线。共享侧栏由M负责，执行者不得改共享入口。',
    }
    existing = {t['id']: t for t in plan['tasks']}
    additions = []
    for task_id, (harness, scopes, deps, gates, integration_deps, test_name) in specs.items():
        if task_id in existing:
            continue
        record = 'docs/records/' + task_id + '-delivery.md'
        test_dir = 'tests/Fufucode.' + (test_name or task_id) + '.Tests'
        scopes = scopes + [test_dir + '/*', record]
        test_project = test_dir + '/Fufucode.' + (test_name or task_id) + '.Tests.csproj'
        description = descriptions.get(task_id, sections.get(task_id, 'Read the authoritative task specification.'))
        prefix = (f'任务：{task_id}｜难度：L4（跨状态项）/原任务等级见session-difficulty｜推荐主模型：group/auto-hy4-preview-f｜实际执行：devin/swe-2（免费，明确成本选择）\n'
                  '先读AGENTS、AI_DEVELOPMENT_PLAN、execution-contract、session-handoff-protocol、agent-event-protocol和本任务书。真实主区在C:/Projects/Fucode/Fufucode，但只在已分配独立worktree实施。\n')
        schema = {'schemaVersion':1,'type':'delivery.card','taskId':task_id,'status':'module_ready','summary':'事实',
                  'commits':[],'changedFiles':[],'verification':{'passed':[],'failed':[],'notRun':[]},
                  'evidence':[],'integrationNeeds':[],'blockers':[],'writerStopped':True}
        prompt = contracts / (task_id + '.md')
        prompt.write_text(prefix + description + '\n本轮允许路径：' + ', '.join(scopes) + '\n'
             '范围不足先交协调卡，不扩写共享入口。新增独立可执行测试工程；模型试跑及子代理必须devin/swe-2，不启动其他任务，不push/登录/删除工作树/改权威记录。所有验证用临时数据。每个必要增量明确git add并commit，保护继承内容；交付记录完整列实现、实际测试、未验门禁。最终回复必须是单份纯JSON：' + json.dumps(schema,ensure_ascii=False) + '\n所有数组保持数组，commits列真实SHA。模块待共享接线是有效交付，不假称原生/整项验收完成。\n', encoding='utf-8')
        integration = contracts / (task_id + '-integration.md')
        integration.write_text(f'只整合{task_id}批准范围，以真实accept/HEAD和原生验证脚本复核。先merge真实来源，再接必要共享入口；未过G1/G2不越门接依赖后端。'+
             ('U3共享tab接线只有U2真实集成后才允许。' if task_id=='U3' else '')+
             '保护全部继承内容，不提交其他任务修改，不更改门禁。记录本项真实测试与未完成范围；禁止用项目构建代替行为测试。\n', encoding='utf-8')
        verify = contracts / (task_id + '-verify.ps1')
        verify.write_text('param([string]$Repository)\n$ErrorActionPreference="Stop"\n'
             f'$project=Join-Path $Repository \'{test_project}\'\nif(-not (Test-Path $project)){{throw "Missing independent behavior tests"}}\n'
             '& dotnet.exe run --project $project -c Release\nif($LASTEXITCODE){throw "Behavior tests failed"}\n'
             '& dotnet.exe build (Join-Path $Repository "src/Fufucode.App/Fufucode.App.csproj") -c Release --nologo\nif($LASTEXITCODE){throw "Product build failed"}\n'
             'Write-Output "PASS module only; native gates remain unchanged"\n', encoding='utf-8')
        additions.append({'id':task_id,'model':'devin/swe-2','harness':harness,'worktree':str(repo.parent / 'worktrees' / ('auto-project-'+task_id)),
            'allowedPaths':scopes,'integrationPaths':[],'dependsOn':deps,'integrationDependsOn':integration_deps,
            'gates':gates,'promptFile':str(prompt),'integrationPromptFile':str(integration),'verificationFile':str(verify),
            'timeoutSeconds':3600,'enabled':True})
    plan['tasks'].extend(additions)
    plan['revision'] = int(plan.get('revision',1))+1
    for gate in ['G3','O1-native','O3-native','O2-integrated','native-tools-available','external-comments-authorized']:
        plan['gates'].setdefault(gate,'open')
    legacy = {
      'D1': ('blocked', '原执行会话须核实原模型/身份和G1原生补验；不新建重复执行者'),
      'O2': ('blocked', '原OMP工作树2cd551f待复核R-030/R-031，原会话未纳入受管协议；禁止接管未知会话'),
      'O1': ('module_integrated', '9ff7b1d模块已整合；原生键盘/IME/DPI门禁未验，不重复开发'),
      'O3': ('module_integrated', '10a971e模块已整合；聊天真实接线/原生门禁未验，不重复开发'),
      'S1': ('completed', 'baca4c3样稿已接受且集成，不重复审核'),
      'U0': ('completed', '研究与交互原型已交付；真实控制台12da07f/01f26e6'),
    }
    plan['projectBacklog'] = [{'id':task_id,'status':legacy[task_id][0] if task_id in legacy else 'scheduled',
       'reason':legacy[task_id][1] if task_id in legacy else '批准契约已物化，按真实门禁/依赖自动执行'}
       for task_id in list(sections)+['U0','U1','U2','U3']]
    path = box / 'project-plan.candidate.json'
    path.write_text(json.dumps(plan,ensure_ascii=False,indent=2),encoding='utf-8')
    return {'candidate':str(path),'tasks':len(plan['tasks']),'projectTasks':len(plan['projectBacklog']),
            'added':[t['id'] for t in additions]}


if __name__ == '__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--repository',required=True)
    print(json.dumps(prepare(parser.parse_args().repository),ensure_ascii=False))
