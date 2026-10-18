# CS-3 原生会话流式时间线交付

日期：2026-10-15。状态：模块已提交，待主控审核；未集成 main，未宣布完整原生内核生产验收。

## 身份与范围

- 任务：CS-3。
- 执行线程：`01a13f5d-ef1c-7a80-b126-f692c6334f24`（由应用线程列表核实）。
- 绑定主控：`01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07`。
- 工作树：`C:/Projects/Fucode/worktrees/cs3-streaming`。
- 分支：`agent/cs3-streaming`。
- 起点：`fd7b34da2398ba5bf7f3ad9991eaca546f196f93`，开工时工作树干净，包含 CS-2。
- 实现和验证证据提交：`afd9d5ceb60be4df86c99ba6bdc649100502e6e2`。
- 收尾提交另含本记录及测试文件末尾空白修正。最终真实 HEAD、提交列表和停写状态见本树 `tmp/CS3-delivery.json` 与 `tmp/CS3-handoff.md`；这两个最终交接产物在所有 Git 写入结束后生成。
- 派单未给出 runId/turnId，不编造；交付版本为 revision 1，以线程、任务、版本和最终 HEAD 组成稳定 eventId。

主控已在本线程明确批准：最小扩展 `PureCSharpAgentEngine` 与 `NativeAgentLoop` 的可选观察通知，保留原 `IPureCSharpAgentEngine`、`IChatSession` 契约；工具状态严格使用 `PureToolResult.Success`；撤销共享入口试验改动。`App/Integration/NativeSessionBootstrap.cs` 最终没有改动。主区和其他工作树未写入，未合并、未推送、未清理工作树。

## 实现

| 文件 | 交付行为 |
| --- | --- |
| `src/Fufucode.Core/Backend/NativeAgentCallbacks.cs` | 新增可选 `IObservablePureCSharpAgentEngine` 和每次调用独立的流式、消息追加、工具开始/结束观察回调。 |
| `src/Fufucode.Core/Backend/PureCSharpAgentEngine.cs` | 沿原引擎保存真实历史并向原 `NativeAgentLoop` 透传通知；原 `RunTurnAsync` 保留。 |
| `src/Fufucode.Core/Backend/NativeAgentLoop.cs` | 派发工具前通知开始，返回真实 `PureToolResult` 后通知完成；未配置调度器仅通知失败，不伪造执行开始。 |
| `src/Fufucode.Core/Backend/NativeCSharpChatSession.cs` | 原有引擎构造方式可观察 SSE；增量立即变成 `TextAppended` / `ToolCallAppended`。每个模型轮独立 MessageId，按首次出现顺序分配连续 contentIndex，每轮收束 `BlockFinished` / `MessageFinished`。 |
| `tests/Fufucode.App.Backend.Tests/NativeChatSessionStreamingTests.cs` | 真实 HTTP 模型客户端、SSE 字节夹具、真实文件工具和桌面 `MessageRebuilder` 测试；额外提供显式免费模型的真实端点探针。 |
| `tests/Fufucode.App.Backend.Tests/Run-NodeFreeSessionProbe.ps1` | 直接启动编译后的 .NET 测试 DLL，将子进程 PATH 设为空并移除 NODE_PATH/NODE_OPTIONS，采样该探针的进程后代，保存输出及 JSON。 |
| `tests/Fufucode.App.Backend.Tests/Program.cs` | 注册本任务测试、`--session-only` 和 `--live-session`，默认其他套件保留。 |

工具执行中的事件使用原枚举 `ToolCallStatus.Running`，对应任务文字中的“Executing”；未增加或重命名公共枚举。完成/失败直接取 `PureToolResult.Success`。文件内容即使包含“未知工具”等文字，也不能被误判成失败；失败结果即使输出为 `ok` 也保持 Failed。最终工具块包含真实输出和 `IsErrorOutput`。

`SendAsync` 按 `IChatSession` 文档在接受时返回，运行结束由事件通知。运行/取消期间拒绝并发发送，避免覆盖原取消源。`StopAsync` 等待工具取消清理及本轮收束，`DisposeAsync` 等待 SSE 取消完成。订阅者异常隔离，不把 UI 回调异常包装为模型运输错误。流内错误和取消均收束已有部分消息，分别报告 Failed / Interrupted；每次发送只发送一个 TurnSettled。

没有把内部后端名 `csharp-native` 发给真实供应商。未给 session modelId 时由模型客户端使用其自身配置，CurrentModel 保持未知/null；可显式传入实际模型 ID。旧的非观察引擎仍可回放真实聚合结果，不能声称它具有实时分片能力。

## 实际验证

环境：本机 .NET SDK `10.0.401`，沿用工程 `net10.0` 与既有依赖，未升级包、未引入其他 Runtime。

| 命令/场景 | 结果 | 可读证据 |
| --- | --- | --- |
| `dotnet build Fufucode.sln -v m` | exit 0，0 警告，0 错误 | `docs/records/CS3-evidence/solution-build.log` |
| `dotnet run --project tests/Fufucode.App.Backend.Tests` | exit 0，479 项断言通过 | `docs/records/CS3-evidence/backend-tests.log` |
| `pwsh -NoLogo -NoProfile -NonInteractive -File tests/Fufucode.App.Backend.Tests/Run-NodeFreeSessionProbe.ps1 -OutputPrefix docs/records/CS3-evidence/nodefree-fixture` | exit 0，PATH 为空的独立 .NET 子进程，会话测试 98 项通过 | `nodefree-fixture.json`、`nodefree-fixture.stdout.log`、空 stderr |
| 同脚本加 `-Live -Model devin/swe-2 -OutputPrefix docs/records/CS3-evidence/nodefree-live` | exit 0，真实本机兼容网关工具循环通过，模型显式使用免费 `devin/swe-2` | `nodefree-live.json`、`nodefree-live.stdout.log`、空 stderr |

以上 nodefree 文件均在 `docs/records/CS3-evidence/`。后端测试工程原本未列入 solution，因此单独运行；没有为此修改共享 solution。479 为全套断言数，本任务会话专属子集为 98，不能把旧 CS-2 文档中的历史计数当作本基线计数。

真实端点最终证据：

- 地址：`http://127.0.0.1:3425/v1`，非公网官方供应商。
- 真实 `read_file` 读取临时文件 `cs3-live.txt`，得到 `CS3-LIVE-MARKER-4471`；工具结果回填后模型最终文本包含该标记。
- 助手文本增量 10 个，工具开始/结束状态事件 2 个，TurnSettled 1 个且为 Completed。
- 首个助手文本增量约 9846 ms，轮次耗时约 9964 ms；这些是本次观测，不是性能保证。
- 探针进程 37504；17 次后代采样只见 `conhost.exe`，未观察到 Node/Python/Agent CLI。fixture 进程 36092，同样未观察到这些 Runtime。
- 子进程 PATH 为空，工作树 `runtime/node/node.exe` 不存在；没有改变主进程全局环境、安装软件或关闭其他进程。

增量是否实时不只看最终计数：`GatedSseStream` 在第一条 SSE 文本帧后暂停传输，确认 `SendAsync` 已返回、首个 TextAppended 已到达，且没有 MessageFinished/TurnSettled；解除暂停后剩余文本才到达。整段聚合回放反例只产生一个增量，生产会话产生三个，保留红/绿对照。

还覆盖：

- 中文跨 1 字节网络切分、工具 JSON 分片交错及稀疏索引 4/9；
- 只有工具的轮次从 contentIndex 0 开始，多块不留空洞；
- 下一模型轮复用相同工具 ID，不串到上一条消息；
- `MessageRebuilder` 重建后的工具状态、输出和最终文本，没有重复追加；
- `Success=true` 的“未知工具”正文与 `Success=false` 的 `ok` 反例；
- 工具取消期间 StopAsync 尚未完成，清理结束后 Running→Cancelled，无虚假 Completed；
- 无调度器失败、有界轮次提示、流内错误、取消、异步释放和订阅者异常隔离；
- 原有模型客户端默认配置未被后端显示名覆盖。

## 过程失败与修正

早期全 solution 并行构建和 Avalonia 编译曾因 `OutOfMemoryException` / MSB4166 失败。先以单节点构建通过；主控随后明确告知已处理内存压力。最终上述普通 solution build 已正常通过，没有修改编译选项或放宽断言绕过错误。

早期真实探针暴露了内部显示名 `csharp-native` 覆盖客户端默认模型，网关返回 HTTP 404。此问题已修正，并以请求体断言及最终真实探针复核。早期草案的工具输出文本分类和旁路 loop 方案也已替换为原引擎观察通知及真实结果判定。过程失败没有算作通过，最终证据均来自修正后的实现。

## 接线和验收边界

1. **本模块已交付**：真实 SSE → 原模型客户端 → 原 Agent Loop → 原引擎 → NativeCSharpChatSession → 桌面 MessageRebuilder 的链路及真实工具结果均有证据。
2. **M 仍需公共组合根接线**：目前 `NativeSessionBootstrap` 默认仍构造内置确定性客户端。请将真实模型设置、endpoint、认证和实际 modelId 送入已有 `PureCSharpAgentEngine(dispatcher, client)` 及会话构造。此任务没有越权改设置或 Bootstrap，也没有新建专用桌面入口。
3. **未验证**：原生桌面窗口实际打字机视觉、干净安装后的完整产品路径、真正卸载/隔离所有系统 Runtime 的 OS 沙箱、正式供应商端点、手机远控、恢复/审批/插件/子代理等全内核能力矩阵。不得将 PATH-empty 会话探针称为整包无 Node 生产验收。
4. **进程证据为采样**：采样未见 Runtime 子进程，不等于操作系统级完整进程创建审计。网关作为外部模型服务独立运行，不属于会话子进程。
5. **历史范围**：本任务保证继续使用原引擎历史，不新增持久化机制；中断工具后的历史恢复/后续真实会话重开不在本轮验证范围。

所有测试进程和本任务探针已退出。临时文件目录由测试 finally 删除；无设备/权限/系统设置临时更改。未清理其他会话进程或工作树。最终交接卡在本记录提交后生成并读回，随后停止源码、构建及 Git 写入，向绑定主控发送一次版本化交付通知；通知仅表示待审，不代表主控已验收。
