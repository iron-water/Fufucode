# C5 高级待办原生表单（第二增量）

任务/软件：C5，Codex Desktop 主控 M，L3，main 单写。工作目录 `C:/Projects/Fucode/Fufucode`，起点 `ab7cef55db1500a3033b602300724eabe8f731ff`。实现提交 `0bcaf831feb52b988c6be4c81785920ba0b65152`，integrationStatus=integrated 仅第二增量；补录不改变已验源码，未 push。此次无需模型请求；本地协议子进程显式 `devin/swe-2`，没有昂贵测试 lead。

改动12文件：App的AutomationControlViewModel及新增.Decisions.cs、AutomationControlPageView.axaml、AutomationControlSelfTest及新增AutomationControlAdvancedSelfTest；另为任务书、拓扑、当前记录、failure-registry、台账、progress、decisions。未修改Core/Host、共享样式或组合根。当前状态：本增量已集成，M已停写本增量源码；余项待后续明确增量，不标整项验收。

## 实际功能和边界

- 知识提案展示原文、来源、提出者与原知识版本，可发布/说明理由拒绝。发布不自动采用；过期竞争提案只能拒绝。
- 定向问答展示双方规范 ID/版本和问题，独立回答框，空回答不能提交。记录的是人类回答，不自动发布知识。Unicode 编译绑定保真，稳定刷新保留草稿。
- 知识采用展示目标规范、目标、知识版本与来源；明确采用后生成并批准新规范版本，不启动模型或改变交付/集成。
- 竞争选择展示共同基准、比较依据和各候选状态；选择器只提供有任务的已批准/已接受候选，保留原 ID 和证据 token，不按相同标题分流。Core 再核查共同基准；胜者选择不代表集成。
- 重组展示原规范与后继目标/范围/验收/模型/依赖/知识、目标映射及关系映射。批准/拒绝接现有 Core；后继不继承执行、接受或集成。过期方案只保留拒绝路线。
- 按当前 Options 渲染对应动作，避免一排与当前待办无关的按钮。原主要审核、授权及模式接口不变；高级命令同样捕获数据库、版本、对象和原输入，同 ID 原回执重放。

没有创建第二数据库、后台宿主或另一套控制器。设置/辅助栏样式与极扁状态栏默认关闭基线保持。用户继承的 demo JSON 和未跟踪目录未改、未暂存；原 U3 工作树及历史交付仍保留 rework，未自行恢复旧 worker。

## 独立核验

最终 App 全量 **1076 ok / 0 失败**，其中 C5 **72**（比首增量增加31项），exit 0。Control **511**、Core **340**、Pi **21**均 exit 0；Atria 实模型选跑 SKIP，未将其记为通过。最终 solution 构建 **0 警告、0 错误**。

命令均显式实际 workdir：

```powershell
dotnet build Fufucode.sln --no-restore -v quiet
dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest
dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest --automation-only
dotnet run --no-build --project tests/Fufucode.Control.Tests
dotnet run --no-build --project tests/Fufucode.Core.Tests
dotnet run --no-build --project tests/Fufucode.Pi.Tests
```

日志：`tmp/m-c5-advanced-{solution,app,targeted,control,core,pi}.log`。独立定向首次稳定版 71 检查通过；最终全量新增严格窄栏视口检查，不能把早先定向计数当作该新增断言的证据。真实 SQLite/Core 命令、compiled bindings、实际 Window/Canvas、焦点 Enter 的按钮提交，均是无头 Avalonia 证据，不等于系统指针/IME/DPI。

最终截图目录 `C:/Users/mio/AppData/Local/Temp/fufucode-selftest-91064dd8dd2a49eaacc2949ca469f1a1/automation`，`native-question.png` 已目视回答框/Unicode文本，严格窗口坐标断言确认340px窄栏视口内实际可见；`native-narrow.png` 保留图与返回路径。Core/Control 本轮没有改码，无须因工装时序修正重复其已通过的全量。

新增反例覆盖：空回答/未选胜者、同名候选身份、发布不采用、采用不执行、外部 revision/问答规范/重组规范/知识版本变更拒绝原请求、fresh 显式采用、原回执幂等、返回清输入、窄栏回答框实际进入视口。

首次过期重组用例在等待布局期间被正常两秒刷新移走旧按钮，出现 `Sequence contains no matching element`。修正测试顺序为完成布局/聚焦后、注入 Enter 前改变外部状态；保留产品刷新和严格旧请求拒绝条件，没有停用刷新或放宽断言。首次命令误用 net8.0 未运行，核对项目 net10.0 后纠正；最后加强视口断言漏了 Avalonia namespace 导致一次构建错误，已补引用，最终构建已归零。

加强视口检查后的全量暴露另一个工装竞态：新知识提案已在库中，观察仍忙时测试刷新被 CanRefresh 拒绝，错误地把旧投影当完成刷新。红日志 `tmp/m-c5-advanced-refresh-red.log` 保留58项C5通过后真实异常；改为等待观察退出，再请求刷新/操作按钮。没有修改产品并发条件，也没有删掉严格的提案与采用检查。

## 监督、未验证与下一步

第 28 轮（触发 2026-10-12 23:50 UTC）：旧调度 PID 23020/22868 不在，已知 E2-B 保存终态无后续启动，原树干净且 HEAD `0fe7d9889315959edbb1876f7545ae608cf80f32`。旧队列只检查元数据并忽略夹具，无新结构化待审卡；app wait 仍 Transport closed，未重复创建/投递/接管未知会话。M 继续独立就绪范围，文档/提交持 main-writer 文件租约。

本增量交付限定高级已有待办，不标 C5 整项或六项生产门全部完成。创建提案/问答/重组的原生入口、图编辑/动画/性能、生产宿主连接与消费者、跨重启回执/草稿恢复、真实指针/IME/DPI仍待；未验证新包与干净安装，旧固定候选不变。下一步先补原生创建与持久化恢复，再推进生产宿主/剩余门；皮肤等待用户风格选择，macOS 后置。
