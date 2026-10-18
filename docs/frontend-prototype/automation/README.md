# 自动开发工作台交互原型

2026-10-12 新增C3 **唯一库客户端模式**，与下文旧JSON门控入口分开。计划、关系、知识、意图及收件箱从同一Core/U1事务读取；旧parallel.html/parallel_control.py保留为历史原型，不作为此模式的权威。

```powershell
dotnet build src/Fufucode.Cli/Fufucode.Cli.csproj
# 替换数据库路径为你明确选择的、已初始化的项目库。
python -B scripts/control_console.py --repository C:/Projects/Fucode/Fufucode --database C:/path/to/project.db --cli C:/Projects/Fucode/Fufucode/src/Fufucode.Cli/bin/Debug/net10.0/Fufucode.Cli.dll --port 18766
```

打开终端打印的本机URL。缺库拒绝启动；新库须先明确运行 `dotnet <CLI.dll> control init <绝对数据库路径>`。也可在automation-console.py中显式传--control-database/--cli选择相同模式，不能结合--apply-draft。没有重启或替换用户当前服务/任务计划程序。

页面可创建/批准工作包、切换全人工/半自动，按既有Core类型记录关系/知识/重组/独立集成授权决定。JSON决定编辑器是维护者过渡入口，不是最终C5原生界面；选择竞争赢家需其实际成果token，Core仍复核。运行绑定、执行报告、实际Git集成及部分打回续接不从浏览器提交；批准不是执行或接受。

结果不明时浏览器保留原operation/command，刷新后重试同ID同输入以对账，不新建另一命令冒充重试。旧JSON只能经既有CLI archive显式记录为history_only。关闭本页/测试服务不停止后台宿主，5秒轮询只调用本地代码，无模型请求。详见[C3客户端记录](../../records/M-C3-client-2026-10-12.md)。

当前真实入口：**http://127.0.0.1:18765**。此入口显示真实固定 M、D4-SNAPSHOT 原会话两轮运行、审核决定、HEAD和集成SHA，禁用示例打回/重启操作。直接打开HTML仍是下述示例。

默认开启“显示未运行流程”，显示完整阶段/Harness规划层级和真实workflow，区分2个真实绑定线程与8个未绑定实时状态的规划归组；规划项不使用旧演示的运行/完成状态。关闭后隐藏规划、等待及已完成执行线程，仅保留运行线程与M监督节点。开关由浏览器记住，不暂停/恢复或派工；当前零执行时只剩M，表示没有模型工作而非流程被删除。

自动模式继续批准任务图；手动模式暂停新派工/审核，编辑任务模型与拓扑依赖。默认调优未启动任务优先免费 `devin/swe-2`，保留既有模型/会话。保存草稿、原生Approve应用、恢复执行分开；当前CLI整项交付，生产部分审核尚未升级。

在 `C:/Projects/Fucode/Fufucode` 中执行：

```powershell
pwsh -NoProfile -File scripts/automation-console-control.ps1 -Mode Status -Repository C:/Projects/Fucode/Fufucode
pwsh -NoProfile -File scripts/automation-console-control.ps1 -Mode Start -Repository C:/Projects/Fucode/Fufucode
pwsh -NoProfile -File scripts/automation-console-control.ps1 -Mode Stop -Repository C:/Projects/Fucode/Fufucode
```

Stop只关可视化服务。开发暂停/恢复使用页面按钮，或分别对 `scripts/agent-workflow.ps1`、`scripts/agent-gate-control.ps1` 执行 `-Mode Pause/Resume -Repository C:/Projects/Fucode/Fufucode`。手动模式须先切回自动再恢复。

宿主是当前用户登录任务计划程序。关闭浏览器/CLI不停止；睡眠暂停执行，重启后用户登录启动，本轮未整机重启。日志 `tmp/automation-console/server.stderr.log`，操作审计 `tmp/automation-console/audit.jsonl`。15分钟heartbeat仍暂停。原生侧栏tab归U3。

入口：[index.html](index.html)。直接双击 HTML 即可；无外部资源、模型请求或项目写入。页面使用示例数据，刷新或“重置演示”回到初态；“重启门房”是状态交互预演，不是真的启动 Windows 进程。

最新布局：功能区单个tab，项目/阶段/Harness的大层级包围9个对话线程节点；线程内50个任务各有进度，任务可继续拆成部分交付。51条连线连接具体任务端口。数据与布局单独保存在thread-flow-data.js，不再用闭环步骤冒充线程节点。

点击“播放部分打回”或单步11次，观察 D1-P / stop 打回修复，而同任务 message 已集成部分及其他任务不变。点击线程内任务行，在图下勾选交付部分并“仅打回勾选部分”。“全图”看首轮/后续层级，“阅读比例”回80%，图内滚动看完整范围。Harness/阶段分组可折叠，单个任务点击不会把整个线程标失败。

顶部计数仅表示示例中的审核调用，不统计集成/执行模型调用或真实费用。

“暂停动画”只控制演示播放，不能冒充真实后台任务暂停。原型无需用户决定后台内部实现；研究与原生接入阶段见 [U 任务书](../../tasks/automation-workbench.md)。本轮替换先前完整工作台样式，不保留第二应用外壳。

浏览器验收与截图见 [U0 记录](../../records/U0-automation-workbench-2026-10-10.md)。
