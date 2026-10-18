# 轨道 C 进度：UI 外壳与消息时间线（mock 数据）

更新：2026-09-28

## 当前状态

| 项 | 状态 | 备注 |
|---|---|---|
| `IChatSession` 冻结接口 + 独立契约类库 | 已完成 | 冻结面见 `docs/decisions.md` [轨道C] 条目 |
| Avalonia 外壳骨架（可构建空窗口） | 已完成 | 0 错误 0 警告 |
| mock 会话 + 消息重建器（§6.6） | 已完成 | 无头自测断言通过 |
| 时间线 / 输入区 / 侧栏 / 状态栏 Views+VMs | 已完成 | 无头自测断言通过；真实桌面启动确认 |
| 无头自测（`--selftest`）与验收证据 | 已完成 | 32 项断言 0 失败；稳定性 20/20 次 |
| ≥500 条长会话性能自测 | 已完成 | 500 条 → 1000 条目，0.95–0.96s（无头环境，见验证记录） |

轨道 C 的 M2 界面部分（mock 数据驱动）全部完成，等待轨道 A 的 Core 集成。

## 已完成内容

### 契约层（已冻结，见 `docs/decisions.md`）

- `src/Fufucode.App/Abstractions/`：`IChatSession.cs`、`SessionEvents.cs`、`ChatModels.cs`、`SessionEnums.cs`。
- 编译为独立类库 `Fufucode.App.Abstractions`（纯 BCL，不引用 Avalonia）：`src/Fufucode.App/Abstractions/Fufucode.App.Abstractions.csproj`。拆库理由是消除 App↔Core 循环依赖：Core 实现接口时引用契约库，App 外壳也引用契约库，两者互不引用；M2 集成时再由 App 引用 Core。
- App 工程用 `<Compile Remove="Abstractions\**\*.cs" />` + ProjectReference 引契约库。

### 外壳与组件

- `Mock/MockChatSession.cs`：`IChatSession` 的 mock 实现——流式 delta 节奏、排队、停止（清空队列并回传待恢复草稿）、失败演示。
- `Mock/MockScenario.cs`：脚本化事件流——思考块、文本增量、工具调用成功/失败、JSON 分片、累计用量。
- `Streaming/MessageRebuilder.cs`：§6.6 全部规则（分块追加/块替换/`MessageFinished` 整体替换/usage 累计覆盖/合并刷新/`GetEntry` 快照）。
- `Infrastructure/`：`IRefreshScheduler`（`DispatcherRefreshScheduler` 合并刷新 + `SynchronousRefreshScheduler` 供自测确定性）、`IClipboardService`、`TextSegmenter`、`ImeEnterGuard`、`StatusColors`。
- `ViewModels/`：`MainViewModel`、`TimelineViewModel`、`InputViewModel`、`StatusBarViewModel`、`SidebarViewModel`、块 VM 与段 VM。
- `Views/MainWindow.axaml(.cs)`、`Styles/FufucodeTheme.axaml`、`App.axaml(.cs)`。
- `SelfTest/SelfTestRunner.cs`：`--selftest` 无头验收入口。
- 工程配置：net10.0；Avalonia 12.1.3（Desktop / Themes.Fluent / Fonts.Inter / Headless）；CommunityToolkit.Mvvm 8.4.2；`AvaloniaUseCompiledBindingsByDefault=true`（任务书强制；所有 DataTemplate 带 `x:DataType`）。

## 验证记录

环境：Windows 11 + .NET 10 SDK（`net10.0`），Avalonia 12.1.3，2026-09-28。除"真实桌面启动"外均为无头模式（`UseHeadless`）+ 软件渲染。

| 验证 | 命令 | 结果 | 备注 |
|---|---|---|---|
| 契约类库构建 | `dotnet build src\Fufucode.App\Abstractions\Fufucode.App.Abstractions.csproj -c Debug` | 0 警告 0 错误 | |
| App 构建 | `dotnet build src\Fufucode.App\Fufucode.App.csproj -c Debug` | 0 错误，2 警告（`Bitmap.Save` 旧重载、冗余 using，均无害） | |
| 无头自测 | `dotnet run --project src\Fufucode.App\Fufucode.App.csproj -c Debug -- --selftest` | 退出码 0；32 项断言 0 失败 | IME 守卫 4、文本分段 3、重建器 3、usage 1、mock 流程 9、停止/草稿 7、长会话 3、窗口渲染 1 + 1 信息行 |
| 自测稳定性 | 上述命令连续运行 20 次（修复前后各 10 次） | 20/20 退出码 0 | 见下"竞态修复" |
| 长会话性能 | 自测内 `GenerateLongConversationAsync(500)` | 500 条 → 1000 个时间线条目，生成耗时 0.95–0.96s（含每 8 条一次让出）；首尾条目可访问 | 无头 + 软件渲染环境，不代表真实桌面性能；同步调度下条目 VM 数量=1000 |
| 窗口渲染（无头） | 自测内 `CaptureRenderedFrame()` | 位图 22169 字节，980x700，>5000 字节阈值 | 非空白 |
| 真实桌面启动 | `dotnet run --project src\Fufucode.App\Fufucode.App.csproj -c Debug --no-build` | 主进程 `Responding=True`，窗口标题 `Fufucode（mock 演示）`，无 stderr 输出；干净关闭 | 窗口 1243x922 |
| 真实桌面渲染 | 桌面运行时 `CopyFromScreen` 截图 `tmp\desktop-window.png` | 529542 字节；4000 次像素采样得 900 种不同颜色、3246 个暗（文字级）像素 | 证明渲染了含中文文本的真实界面，非空白 |

### 竞态修复（"正常回合未产生助手消息"）

早期自测偶发失败：正常回合结束后 `timeline.Entries` 查不到助手消息。机制已定位并确定性修复：

- `MockChatSession` 的回合跑在 `Task.Run` 后台线程。收尾时先 `Raise(MessageFinished)`、`Raise(TurnSettled)`，在 `finally` 里才 `SetActivity(Idle)`。这些事件经 `TimelineViewModel.OnSessionEvent` 用 `Dispatcher.UIThread.Post(...)` 投递到 UI 线程（`Post` 不带优先级时实测默认 `DispatcherPriority.Default`）。
- 旧测试的 `WaitForSettledAsync` 只轮询 `session.Activity`。一旦读到 `Idle` 就去查 `timeline.Entries`，但此时 `MessageFinished` 的 UI 回调可能仍排在 dispatcher 队列里没执行——助手消息尚未生成，断言失败。这是真实的调度竞态，不是逻辑错误：`Activity` 的内存可见性早于 UI 队列排空。
- 修复：`WaitForSettledAsync` 在观察到 `Idle` 后追加阶段三，`await Dispatcher.Yield(DispatcherPriority.Background)`。实测优先级 `Background`=-2 严格低于 `Default`=0，dispatcher 按优先级出队，所有已入队的终态回调必然先执行完，再回到断言。
- 该修复是无条件的（依赖优先级偏序，不依赖本机时序），修复前后各连跑 10 次均 0 失败。

## UniGetUI 研究的采纳记录（任务书要求）

研究文档为设计参考（非需求规范），需求权威是计划 §9 与任务书。以下逐条记录采纳与否：

**采纳：**

1. `AvaloniaUseCompiledBindingsByDefault=true` + 所有 DataTemplate 带 `x:DataType`（任务书直接强制；研究 §6.2.7）。
2. CommunityToolkit.Mvvm + C# 事件，不引入 Messenger（研究 §3.3）：VM↔View 通信走事件，VM 不直接 `new` 具体窗口。
3. 样式集中为资源字典 + 手工 Fluent 令牌（研究 §3.4 的精简版）：Light/Dark 双字典派生应用级令牌，控制样式不散落在各控件。
4. 大列表虚拟化（研究 §3.5）：时间线用 `ItemsControl` + `VirtualizingStackPanel`，长输出与代码块按可见性折叠。
5. 显示规则做成可单测的纯类（研究 §2.4 的 `OperationCardController` 思路）：消息重建（§6.6）放在无 UI 依赖的 `MessageRebuilder` 里，自测直接断言其行为。
6. 刷新合并在状态层而非渲染层（计划 §6.6）：重建器累积脏标记，由调度器按节拍批量通知 UI。

**不采纳 / 暂缓：**

1. 无 DI 容器 + 大量静态单例（研究 §6.3.1）：首版外壳直接构造 VM 即可，但不引入静态全局会话状态，会话由组合入口注入。
2. DataGrid 长列表方案（研究 §3.5）：时间线不是表格，用 `ItemsControl` 虚拟化。
3. 提权助手、IPC API、单实例转发、Toast 系统（研究 §5/§2.5）：属于后续里程碑，M2 外壳不做。
4. TranslateExtension 本地化（研究 §3.4）：首版界面直接写中文文案，不做多语言基础设施。
5. NativeAOT 硬约束（研究 §1）：M2 不作为约束，但编译绑定纪律现在就执行。

## 未验证 / 风险项（如实标注）

- 尚未连接真实 Pi（轨道 A 的 Core 未完成）；一切会话行为均由 mock 表达，真实协议行为未经本轨道验证。
- 中文输入法组合（IME）的真实设备验证未做：Avalonia 12.1 无公开 IME 组合 API（反射确认 `InputElement` 只有 `TextInput`），`ImeEnterGuard` 改用"文本变化形状 + 150ms 吞回车窗口"启发式（组合式变化=非空、不短于旧文本、非尾部追加）。失效方向偏向"误吞回车"而非"误发消息"；真实 IME 交互留待 M6 人工验证。逻辑层 4 项断言已覆盖三种形状与窗口边界。
- 无头自测不等于真实桌面渲染性能：≥500 条性能数据（0.95–0.96s）标注为无头模式 + 软件渲染环境，仅作回归基线，不代表真实桌面吞吐。
- 无头截图与真实桌面截图均为字节级/像素级证据，未经人工肉眼逐项确认布局。
- `MessageFinished` 必须携带完整最终块（不能传空块数组）：§6.6 整体替换语义，否则抹掉已流式内容——已写进 `MessageRebuilder` 断言。

## 下一步

1. 等轨道 A 的 M2 Core 完成 `IChatSession` 实现后，在 App 组合入口替换 `MockChatSession` 为真实 Core 会话，验证真实流式/停止链路。
2. M3 与轨道 B 的审批桥接扩展对接（扩展部分由 B 负责，外壳只提供入口）。
3. IME 真实设备验证留待 M6。
