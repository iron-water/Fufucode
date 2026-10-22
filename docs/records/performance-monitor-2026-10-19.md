# 性能监控插件（performance.monitor）交付记录 — 2026-10-19

## 交付内容

| 层 | 位置 | 作用 |
|---|---|---|
| 核心埋点 | `src/Fufucode.Core/Diagnostics/Perf/` | `PerfTracer`/`PerfSpan`（AsyncLocal 父子关系）、`PerfOwnership`（唯一接收端），未启用时零分配 |
| FUMO 埋点 | `Backend/` 下 8 个文件 | 回合→模型→流→工具→命令→持久化完整 span 树，见 `docs/decisions.md` |
| 插件 | `src/Fufucode.Plugins.PerformanceMonitor/` | 接收端、脱敏、环形缓冲、JSONL、聚合、异常规则、停靠页、设置视图 |
| 宿主 | `src/Fufucode.App/Features/Performance/` | 进程/UI 采样、UI 响应探针、全局未处理异常、回合聚合落盘、插件服务交付 |
| 接线修正 | `PluginPackageManager`、`PluginStateStore`、`PluginWorkspacePageHost` | 可注入服务解析器、多实例状态合并写盘、接受 `IPluginPageProvider` |

## span 一览（FUMO）

```
turn (model, executionMode, promptChars, queueMs, outcome)
├─ context.build (messages, toolDefinitions)
├─ engine.turn (gateWaitMs)
│  ├─ context.compact
│  ├─ agent.iteration (n)
│  │  ├─ model.request (tools, streaming, historyMessages)
│  │  │  └─ model.http (status) → model.stream (frames, ttftMs, maxGapMs, p95GapMs, usage)
│  │  ├─ tool (tool, ok, outputChars)
│  │  │  ├─ tool.approval (approved)
│  │  │  └─ [run_command] tool.process (command, exitCode, timedOut, stdoutBytes, stderrBytes)
│  │  └─ tool.batch
│  └─ subagent / mcp.call
└─ persist.history (messages, bytes)
```

## 2026-10-20 启动性能实测与整改

真实 FUMO 会话（数据根副本、同一会话、Debug 构建）的启动分解：

| 阶段 | 整改前 | 整改后（Debug） | 整改后（Release+R2R，热） |
|---|---:|---:|---:|
| 窗口构造完成 | 4.50s | 4.65s | 2.24s |
| 会话打开完成 | 7.99s | 6.20s | 2.84s |
| 主视图模型挂接完成 | 10.65s | 8.15s | 3.63s |
| 首帧 | 12.50s | 8.70s | 3.81s |
| 布局/渲染空闲 | 12.53s | 9.88s | 3.89s |

整改前的主要拖累（按耗时）：

1. `ModelCapabilityProbe.SupportsImageInputAsync` 阻塞会话打开：本地网关未就绪时固定等满 2 秒。
2. `ApplyCustomization` 在启动期被调用两次（`InitializeCustomization` + `AttachCustomization`），每次重建整套样式并触发全树重新求值，单次约 0.2~0.9 秒。
3. `RenderOverview` 在 AttachRealSession 同步执行，约 0.6~0.9 秒，挡在开始页面之前。
4. `AttachSession` 的 `DataContext = vm` 触发所有页面模板实例化，约 0.43~1.2 秒。
5. 窗口构造 1.1~2.2 秒：工作区外壳、皮肤/样式、浏览器设置、插件页。
6. JIT/类型加载：Debug 下 `app.theme.end → window.ctor.begin` 约 0.9~1.0 秒；Release+R2R 降到约 0.4 秒。
7. 窗口显示后的首次布局约 0.5 秒。

已实施：后台图片探测、视觉配置签名去重（布局单独判断）、概览移到 `ApplicationIdle`、非裁剪 Release 默认 R2R、`publish-local.ps1 -ReadyToRun`。

剩余可做（按收益）：启动占位窗（冷启动首帧约 5.4 秒时先显示窗口）、设置/扩展/停靠页懒创建、浏览器设置延后初始化、App XAML/主题资源按需加载。

## 2026-10-20 默认启用与启动链路

- 插件目录改为默认安装 + 默认启用；App 启动时 `PluginBootstrapper.EnsureBundled` 幂等同步 `plugins-dist/performance.monitor` 到数据根（宿主程序集不复制）。
- 新增 `StartupTrace`：`process.main` → `avalonia.setup` → `window.ctor.begin/shell/plugins/end` → `session.bootstrap` → `session-ready`，逐条写 `perf/startup-*.jsonl`；结束后回放为一棵 `startup` span 树（真实起止时间），性能页瀑布选择器出现“启动”项，概览显示“最近启动”。
- 修复 JSONL 实时可读性（逐条 Flush）。
- 验证：真实 exe 冷启动 `startup-*.jsonl` 记录 9~10 个里程碑；`perf-*.jsonl` 只有一棵 `startup` trace；`host-*.jsonl` 持续采样。
- 未安装/已卸载：在 `plugins.json` 显式写 `isInstalled=false` 后启动，不重新部署载荷，`sinkActive=false`，符合用户卸载决定。
- 发布渠道注意：默认启用依赖 `plugins-dist/performance.monitor` 载荷存在；安装包当前仍把插件作为独立下载资产，未随包时设置页会显示缺失原因，不会伪造启用。

## 2026-10-20 入口去重

按用户指令，删除“诊断与系统”里已被性能监控覆盖的旧入口：

| 删除 | 原因 |
|---|---|
| 查看调用轨迹（`trace` + `MainWindow.Trace.cs`） | 性能页的回合瀑布、慢调用与异常取代 Pi 诊断日志列表 |
| 运行状态与资源（`help.tasks`） | 性能页概览与 `perf/host-*.jsonl` 已持续采集内存/CPU/线程/句柄/GC |
| 开始/结束性能跟踪（`help.performance`） | 性能页提供实时、持续、可导出的采样 |

性能监控在辅助栏只保留一个入口“性能监控”（`perf`），收进“诊断与系统”子菜单并作为默认项；导出、清空与异常视图保留在性能页和设置页内。“导出完整诊断包”“故障排除”“系统状态”保留，它们覆盖非性能信息（后端日志、系统版本、路径）。

## 验证状态

- `dotnet build Fufucode.sln -c Debug`：0 错误 0 警告。
- `dotnet run --project tests/Fufucode.PerformanceMonitor.Tests`：全部通过（8 组）。
- `dotnet run --project tests/Fufucode.App.Backend.Tests`：825 项通过。
- `dotnet run --project tests/Fufucode.App.Settings.Tests`：`PluginGatingTests` 在基线同样失败（预制缺陷），目录断言已更新。
- 真实 exe（`--mock --data-root`，插件安装并启用）：`perf/host-*.jsonl` 产生 `process.workingSetMb`/`process.cpuPercent`/`session.uiBatchMs` 样本；日志显示 `wiring=ok; page=ok; registered=True`。

- `WorkbenchCustomizationSelfTest`（`--selftest --r05-only`）：2 项失败，与基线 HEAD 完全一致（“settings page assignments…” 与第 258 行 `Sequence contains no matching element`），非本次引入。

## 未验证

- 真实模型端点的流式帧间隔统计（单元测试以脚本化 SSE 响应覆盖同一解析路径）。
- 安装器打包冒烟（`package-native.ps1` 已加入载荷与禁止清单，未执行）。
- `PerfDockPage` 在桌面窗口中的视觉呈现（无头预览只能核对页面已注册，无法可靠截到停靠页）。

## 已知限制

- 插件 `IsEffectivelyVisible` 依赖宿主停靠区可见性，隐藏时暂停实时刷新（重新显示时下一次刷新）。
- 宿主样本以同步追加方式写 `host-*.jsonl`（每 2 秒若干行），极端磁盘阻塞时可能短暂拖慢采样线程。

## 2026-10-22 概览/分析双视图重构

- 新的 `PerfCenterView` 取代单一横向工具栏页面：左栏“概览 / 分析”，概览只保留用户指标，分析提供启动总览、Span 树、共享时间轴和检查器。
- 时间线改为自绘控件：7 条轨道、TTFT 段、UI 阻塞刻度、审批等待、持久化、CPU 采样；轮滚缩放、双击复位；点击条与 Span 树、检查器联动。
- Span 树用虚拟化 `ListBox`，支持搜索、折叠、过滤时保留命中节点及祖先；`ui.stall`/`ui.batch` 事件补成树节点，保证阻塞在树里也能看到。
- 页面首次打开改为 1180×760 浮窗（`FloatDockPage` 增加首选尺寸），仍可停靠回任意栏；右栏收起不再影响性能页。
- 快照改为后台线程构建，UI 线程只应用结果；刷新节拍 1.5s，实时开关可关闭。

### 验证

- `dotnet build Fufucode.sln -c Debug`：45 工程 0 错误 0 警告。
- `dotnet run --project tests/Fufucode.PerformanceMonitor.Tests`：全部 8 组通过；新增 `snapshot` 组覆盖层级、Token、阻塞节点、过滤。
- 审阅构建 `feature/perf-ui@fbd00a91` 已生成并启动浮窗；无头 `invoke perf --allow-action --perf-turn` 执行成功，无异常输出。

### 未验证

- 真实桌面浮窗在真实会话数据下的视觉密度与滚动/缩放手感；需审阅窗口确认。
- 命令 stdout/stderr 正文未进入性能页，检查器只显示字节数；完整输出仍在聊天工具卡/日志。

