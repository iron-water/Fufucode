# UniGetUI 源码设计研读 —— 供 Fufucode（Avalonia AI Harness）借鉴

日期：2026-09-28
阅读对象：`C:\Projects\Fufucode\reference\UniGetUI`（克隆自 github.com/marticliment/UniGetUI，main 分支快照，提交 `9f55ba5f86e070bd74ce933b6c16e2c759fa545c`，2026-09-23）
阅读方式：静态阅读，未构建、未运行；结论以本快照代码为准，未逐一运行验证的部分在文中标注"未核实"。

## 0. 为什么读它，以及快照的重要事实

Fufucode 是 Windows 优先的 Avalonia 桌面 coding agent 客户端，通过 Pi 官方 RPC 驱动 Pi 进程。UniGetUI 与 Fufucode 的核心课题同构：**一个桌面 GUI，包装一个/多个外部进程（CLI 包管理器），处理启动、参数拼装、流式输出、进度、取消、失败重试、权限提升，并把这一切映射到可绑定的 UI**。这正是 AI harness 包装 agent 进程要解决的问题。

阅读本快照前需要知道的三个事实：

1. **UI 已全面迁移到 Avalonia**（Avalonia 12.0.4，TFM net10.0 / net10.0-windows10.0.26100）。仓库中没有任何 WinUI3/WinForms 残留（无 `UseWinUI`、无 Windows App SDK 引用）。它展示的是一个大体量成熟应用的 Avalonia 写法，而不是历史包袱。
2. **本快照包含 Devolutions 参与的分支改动**：`UniGetUI.PackageEngine.AgentBroker`（基于 `Devolutions.Now.Policy.Client` 的策略代理执行路径）与 `UniGetUI.AgentPolicy.ElevatedHelper`（一次性提权助手进程）等，上游历史版本未必有；遥测端点也指向 devolutions.net。这些是快照事实，读文档时按"设计参考"对待即可。
3. 上游著名的 `Core.Tools.ProcessStartBuilder` 在本快照中**已不存在**（全仓 grep 零命中），进程启动职责拆成了 `ManagerStatus.ApplyArguments`（参数拼装）+ `AbstractProcessOperation`（进程生命周期）+ `CoreTools` 的若干进程工具函数。

---

## 1. 总体架构：分层与项目组织

`src/` 下约 40 个工程，按依赖方向严格分层，每层一个或多个独立 csproj：

| 层 | 工程 | 职责 |
|---|---|---|
| UI（唯一可执行） | `UniGetUI.Avalonia` | Program 入口、AXAML、ViewModels、Infrastructure（应用宿主与 UI 服务） |
| 横切接口 | `UniGetUI.Interface.IpcApi` / `.Telemetry` / `.Enums` | 本地 REST API、遥测、共享枚举 |
| 引擎接口 | `UniGetUI.PackageEngine.Interfaces` | `IPackageManager`、`IPackage`、`IManagerSource` 等纯契约 |
| 引擎实现 | `UniGetUI.PackageEngine.PackageManagerClasses`（抽象基类）、`UniGetUI.PackageEngine.Managers.*`（每管理器一个工程，约 15 个） | 外部 CLI 工具的具体包装 |
| 引擎运行时 | `UniGetUI.PackageEngine.PackageLoader` / `.Operations` / `.PackageEngine` / `.Serializable` / `.AgentBroker` | 列表加载器、操作队列、装配根、序列化 |
| 基础服务 | `UniGetUI.Core.Settings` / `.Logger` / `.Tools` / `.Data` / `.LanguageEngine` / `.IconStore` / `.SecureSettings` / `.Classes` | 设置、日志、工具函数、路径、语言、图标缓存 |
| 平台扩展 | `WindowsPackageManager.Interop`（WinGet COM）、`ExternalLibraries.FilePickers/Clipboard`（Win32 手写互操作）、`UniGetUI.AgentPolicy.ElevatedHelper` | Windows-only，按 TFM 隔离 |

组织要点：

- **两个解决方案**：`UniGetUI.Avalonia.slnx`（跨平台子集，不含 Windows-only 管理器）与 `UniGetUI.Windows.slnx`（超集，CI/打包用）。工程归属靠 slnx 裁剪，而不是把所有代码塞进一个大工程再用 `#if` 屏蔽。
- **平台隔离用双 TFM 机制**：`Directory.Build.props` 定义 `PortableTargetFramework=net10.0` 与 `WindowsTargetFramework=net10.0-windows10.0.26100`，每个 csproj 按构建机器 OS 单选；Windows-only 代码同时用 `#if WINDOWS` 守卫。跨平台库不引用任何 Windows 工程。
- **每管理器一个工程**（`Managers.WinGet`、`Managers.Scoop`、`Managers.Pip`……），依赖只指向 `Interfaces` 与 `PackageManagerClasses`，互不引用。新增一个被包装的 CLI 工具 = 新建一个工程、一个类、三个 Helper，不碰任何现有代码。
- **无 DI 容器**。全仓没有 `IServiceCollection`/`Microsoft.Extensions.DependencyInjection`。服务是静态类或单例（`PEInterface`、`Settings`、`IconDatabase.Instance`），ViewModel 由 View 直接 `new`，依赖走构造参数或静态访问。仓库自己的 AGENTS.md 明说这是 NativeAOT 约束下的刻意选择："禁止运行时反射激活，偏好显式注册、直接构造"。
- **NativeAOT 纪律贯穿全仓**：Release 是 self-contained + `TrimMode=full` + `PublishAot=true`（pubxml profiles 覆盖 win/osx/linux × x64/arm64）。类库层靠 source-generated JSON（`JsonSerializerContext`）与手写 `Utf8JsonReader`/`JsonNode` 保持无反射；发布路径上每个 IL2xxx 警告被视为缺陷。

## 2. PackageEngine：包装外部 CLI 的范式（迁移价值最高的一层）

### 2.1 接口层：描述"是什么、能做什么"

`UniGetUI.PackageEngine.Interfaces\IPackageManager.cs` 的核心成员：

```csharp
public ManagerProperties Properties { get; }      // 名称/图标/InstallVerb 等动词/已知源 —— 把 CLI 动词也参数化了
public ManagerCapabilities Capabilities { get; }  // 全 bool 的能力声明结构体
public ManagerStatus Status { get; }              // exe 路径/调用参数向量/版本/是否找到
public IManagerLogger TaskLogger { get; }         // 每任务日志（记录子进程 stdout/stderr/stdin）
public IMultiSourceHelper SourcesHelper { get; }
public IPackageDetailsHelper DetailsHelper { get; }
public IPackageOperationHelper OperationHelper { get; }
public IReadOnlyList<IPackage> FindPackages(string query);
public IReadOnlyList<IPackage> GetAvailableUpdates();
public IReadOnlyList<IPackage> GetInstalledPackages();
public void AttemptFastRepair();                  // 自愈钩子（WinGet 重连 COM 等）
public int? CompareVersions(string a, string b);  // 返回 null 表示两版本不可比较
```

两个值得注意的设计取向：

- **manager 本身不暴露事件**。列表类结果是同步返回值；进度与状态由独立的 Operation 对象的事件模型承担（见 2.4）。"查询"与"操作"是两条管线，不混在一个 API 面上。
- **能力用声明式结构体表达**（`ManagerCapabilities`：`CanRunAsAdmin`、`SupportsCustomSources`、`CanRunInteractively`……），UI 据此渲染可用操作，而不是每个 manager 自己写 UI 分支。

### 2.2 三 Helper 拆分："怎么调用"从 manager 里剥离

每个 manager 由一个主类 + 三个 Helper 组成（基类在 `PackageManagerClasses\Manager\Helpers\`）：

- `*PkgDetailsHelper`（继承 `BasePkgDetailsHelper`）：详情、版本列表、图标、截图的获取与解析；
- `*PkgOperationHelper`（继承 `BasePkgOperationHelper`）：把"包 + 安装选项"翻译成命令行参数，以及**从输出判定结果**；
- `*SourceHelper`（继承 `BaseSourceHelper`）：源的枚举与增删参数。

`Managers.Scoop\Scoop.cs` 是仓库 AGENTS.md 推荐的参考实现：构造函数里声明依赖、`Capabilities`、`Properties`（10 个已知 bucket 预建为 `ManagerSource`），组装三个 Helper。CLI 表格解析放在 `Helpers\ScoopTable.cs`（正则剥 ANSI 色彩 + 按 `---` 分隔行定位列），解析函数做成 `internal` 以便测试。解析噪声用黑名单过滤：

```csharp
public static string[] FALSE_PACKAGE_IDS = ["No", "WARN"];
public static string[] FALSE_PACKAGE_VERSIONS = ["Matches", "Install", "failed", ...];
```

`_getOperationResult` 里的输出驱动重试是教科书级样例：

```csharp
if (output_string.Contains("Try again with the --global (or -g) flag instead")) {
    package.OverridenOptions.Scope = PackageScope.Global;
    package.OverridenOptions.RunAsAdministrator = true;
    return OperationVeredict.AutoRetry;   // 改选项后自动重跑
}
```

### 2.3 `_UnSafe` 模式：防护集中在基类，实现允许裸奔

公共 API 全部是"线程安全包装 + 私有不安全实现"：

```csharp
public IReadOnlyList<IPackage> FindPackages(string query) => _findPackages(query, false);

private IReadOnlyList<IPackage> _findPackages(string query, bool SecondAttempt)
{
    if (!IsReady()) return [];
    try {
        return RunListingTaskWithTimeout(() => FindPackages_UnSafe(query), "_findPackages");
    }
    catch (Exception e) {
        if (!SecondAttempt && (RetryListingTasksOnTimeout || e is not TimeoutException)) {
            AttemptFastRepair();                 // 先自愈
            return _findPackages(query, true);   // 再重试一次
        }
        return [];                               // fail-safe：失败返回空列表
    }
}
```

`FindPackages_UnSafe` 等是 `protected abstract`，由各 manager 裸跑进程、直接解析输出——名字里的 "UnSafe" 就是字面意思：不设防，防护全在基类。

查询管线的线程模型（`PackageManager.cs` 的 `RunListingTaskWithTimeout`，约 376 行处）：

- `Task.Run` 包住调用，**同步等待最多 60 秒**（`PackageListingTaskTimeout`）；
- 超时后 `KillListingProcesses`：用 `AsyncLocal<List<Process>>` 收集本次任务启动的全部子进程（子类启动进程后调 `RegisterListingProcess(p)` 注册），`process.Kill(entireProcessTree: true)` 全树击杀；
- 失败自愈：先 `AttemptFastRepair()` 再重试一次，仍失败返回空列表，绝不向上抛。

初始化同样容错：`Initialize()` 流程为 `_ensurePropertlyConstructed()` → `_performPreInitializationSteps()` → 检查 `IsEnabled()` → `_loadManagerExecutableFile()`（abstract）→ `_loadManagerVersion()`（abstract）→ `_performExtraLoadingSteps()`，整体包在 try/catch 中，失败只记日志、`IsReady()` 返回 false。

### 2.4 操作管线：一个操作就是一个事件源 + 静态队列

变更型操作（install/update/uninstall）走完全独立的对象体系（`UniGetUI.PackageEngine.Operations\`）：

```
AbstractOperation → AbstractProcessOperation → PackageOperation → Install/Update/UninstallPackageOperation
```

`AbstractOperation`（777 行）是事件总线：

```csharp
public event EventHandler<OperationStatus>? StatusChanged;
public event EventHandler<EventArgs>? CancelRequested;          // 触发杀进程
public event EventHandler<(string, LineType)>? LogLineAdded;    // 行级输出 → UI
public event EventHandler<OperationProgress>? ProgressChanged;  // 结构化进度 record
public event EventHandler<OperationVeredict-adjacent 事件族>? OperationStarting/Finished/Succeeded/Failed/Enqueued;
```

- **队列是静态的**：`static readonly List<AbstractOperation> OperationQueue` + `static int MAX_OPERATIONS`（默认 1 串行，可由设置覆盖）。排队用轮询实现：入队后 `while (IndexOf(this) >= MAX_OPERATIONS) await Task.Delay(100);`。简单粗暴，但 UI 可以直接把队列渲染成卡片列表。
- **取消三层**：`CancellationTokenSource.Cancel()`（逻辑）→ 状态置 Canceled 并级联 `Kill(entireProcessTree:true)`（进程）→ `AsyncLocal` 注册表兜底清理孤儿。
- **`OperationVeredict { Success, Failure, Canceled, AutoRetry }`**：`_runOperation()` 的骨架是 PreOperations（失败可中止）→ `do { result = await PerformOperation(); } while (result == AutoRetry);` → PostOperations。UI 侧的"以管理员重试"就是 `Retry(RetryMode.Retry_AsAdmin)`：改 Options 后重新排队，实现极简。

`AbstractProcessOperation`（295 行）是进程封装范本，值得逐行读：

- 进程配置：`UseShellExecute=false`、重定向 std 三流、UTF8、`CreateNoWindow`；
- **stdout 逐字符读**（`ReadAsync(char[1])`）：`\n` 结束的行是 `Information`，`\r` 结束的行是 `ProgressIndicator`——这是正确处理 winget/scoop 之类单行刷新进度条的方式；
- 校验 `PrepareProcessStartInfo()` 已被子类填好（用一个哨兵值检测漏调，讨巧但有效）；
- 结束后 `GetRawOutput()` 保留原始日志（结果解析以原始输出为真值源，显示层才做脱敏），交抽象方法 `GetProcessVeredict(returnCode, output)` 由子类判定；
- 提升双轨：短平快路径把 `FileName` 换成 Elevator（gsudo/sudo/pkexec，`CoreData.ElevatorPath` 启动时探测），参数加 `ElevatorArgumentPrefix()`；高保障路径走 AgentBroker（见 2.5）。

UI 绑定方式：fire-and-forget 启动 `_ = op.MainThread()`，随后订阅事件。`OperationCardController` 是一个 UI 线程拥有的纯状态机（`TryApplyLogLine` / `ApplyProgress` / `ApplyStatus`），决定"结构化进度持有状态行时 gate 掉原始进度行"等显示规则——把显示逻辑做成可单测的纯类，是很好的模式。

### 2.5 提权与 out-of-process 代理（对 harness 最像的一节）

**AgentBroker**（`UniGetUI.PackageEngine.AgentBroker\`）：操作执行有第二条路径——`PackageOperation.PerformOperation()` 先判断 `ShouldUseAgentBroker()`（设置开关 && broker 支持该 manager && 非虚拟源），是则 `PerformBrokerOperation()`：

1. 先走 `OperationHelper.ApplyElevationRequirements()` 保证两条路径提升判定一致；
2. `client.IsAvailable()` 探测，**不可用绝不回退本地执行**（回退会绕过策略或重复执行），改为触发 `BrokerUnavailable` 事件给 UI 弹错；
3. 策略判定 `Decision == Allow` 才继续；跟踪用 `CancellationTokenSource.CreateLinkedTokenSource(用户取消, 1小时超时)` 双重限时；
4. broker 通告 `EventChannel` 则 `await foreach` **流式收 stdout/stderr 行**（复用 `Line()` 通道进 UI），失败降级为 500ms 轮询终态；
5. 取消走 `CancelBrokerOperation`（5s 接受 + 30s 终态确认）。

这套"可选第二执行路径 + 策略判定 + 事件流/轮询双模 + 明确不降级"就是给外部进程做 broker 化的完整蓝图。

**AgentPolicy.ElevatedHelper**：一次性提权助手进程。主程序用 `ShellExecuteEx + runas` 启动它，命令行只带路由信息（管道名、父 PID、父进程创建时间），**全部请求载荷走认证命名管道，不落临时文件**；进程"只处理一个连接、读一个请求、写一个响应、退出"。安全设计包括父子进程互相认证（路径/签名/身份 + 自身位于受保护安装目录）、匿名令牌连接管道、每阶段独立超时、父进程断连监视、枚举型退出码。**启示：提权面压缩成一个最小、无状态、强认证的一次性进程。**

**同一接口、双后端择一**：WinGet 同时有 COM（`WindowsPackageManager.Interop`）与 CLI 两个实现，`_loadManagerExecutableFile` 按优先级选后端（捆绑的 pinget.exe → CLI → COM，COM 激活失败自动落 CLI），所有 `_UnSafe` 只面向 `IWinGetManagerHelpers`。换后端时要补上原本由后端提供的并发保证（CLI 后端加了 `_cliInvocationLock`，Monitor + 120s 安全阀）——这条经验对"同一 agent 多版本/多传输方式并存"的场景直接适用。

### 2.6 列表加载与装配容错

`UniGetUI.PackageEngine.PackageLoader\` 有 4 个 loader（Installed/Upgradable/Discoverable/Bundles），共同基类 `AbstractPackageLoader`：

- 存储 `ConcurrentDictionary<long, IPackage>`，键是 `Hash($"{Manager}\\{Source}\\{Id}")`——**跨 loader 实例用 long 键对齐，不依赖引用相等**；
- 事件 `PackagesChanged(added/removed, proceduralChange)`、`StartedLoading`、`FinishedLoading` 是 UI 唯一绑定点；
- 刷新时对每个就绪 manager `Task.Run` 并行拉取，**结果边到边出**（增量 `InvokePackagesChangedEvent`，不是攒齐再发），并用 `LoadOperationIdentifier` 世代号防止过期结果回填；
- 暴露 `WaitForCurrentLoadAsync`（`TaskCompletionSource`）供需要等待的调用方。

装配根 `UniGetUI.PackageEngine.PackageEngine\PEInterface.cs` 是静态类：静态只读字段构造所有 manager，`Create<T>` **吞掉一切构造异常**（连日志调用都包 try/catch），因为静态字段初始化器抛异常会以 `TypeInitializationException` 杀死整个引擎——"一个 manager 失败不能拖死其他 manager"。`LoadManagers()` 用 `Task.WhenAll` 并行初始化，60 秒总超时。平台裁剪用 `#if WINDOWS` + Linux 读 `/etc/os-release` 选家族。

### 2.7 序列化的 AOT 双轨

- **跨进程/持久化 DTO**：source-generated `JsonSerializerContext`（每工程自定义，如 `CoreToolsJson.cs`、`IpcJson.cs`、`SettingsJson.cs`）；
- **面向用户的版本兼容文件**（bundle、安装选项）：抽象基类 `SerializableComponent` 要求 `Copy()/LoadFromJson(JsonNode)/AsJsonNode()`，**手工 JsonNode 逐字段读写**，换取宽松的旧版本加载兼容。
- 序列化热点（语言文件）用 `Utf8JsonReader` 手写流式解析。

## 3. Avalonia UI 层设计风格

### 3.1 启动引导：两层静态宿主，无 DI 容器

- `Program.cs` 极薄：更新交换期守卫 → Windows 盖 AUMID → `AvaloniaAppHost.Run(args)`。
- `Infrastructure\AvaloniaAppHost.cs`（静态类）负责：挂 `AppDomain.UnhandledException` → CLI 预处理（pre-UI flag 本地执行；IPC 动词作为客户端连运行实例后退出）→ 无头模式分流（`--headless` 走 `HeadlessDaemonHost`，完全不建 UI）→ **单实例注册**（Windows 命名 Mutex；macOS 用文件锁，注释明言 NativeAOT 下命名 Mutex 跨进程不可靠；抢锁失败把参数以换行分隔写入命名管道转发给首实例后退出）→ `StartWithClassicDesktopLifetime`。
- `AppBuilder` 配置点：默认字体策略（env var 可覆盖）、Windows 问题机器强制软件渲染、`LogToTrace()`。
- `App.axaml.cs`：`Initialize` 装全局行为补丁（`ButtonActivationGuard.Install()`、`SmoothScrollManager.Install()` 全局平滑滚动）+ 条件合并 Mica 样式字典；`OnFrameworkInitializationCompleted` 里**先显示 Splash，再 `Dispatcher.UIThread.Post(StartMainWindow, DispatcherPriority.Background)` 延迟重活**；主题在任何窗口显示前应用；启动时优先弹上次崩溃报告。
- `Infrastructure\AvaloniaBootstrapper.cs`：`Task.WhenAll` 并行初始化共享服务（语言引擎、代理、自动更新循环、IPC API 服务器、遥测、Elevator 探测、图标库）与 `Task.Run(PEInterface.LoadManagers)`，随后完整性校验、缺失依赖逐个弹窗。

**没有 DI 容器**：ViewModel 由 View 直接 `new`，跨页共享状态走静态注册表（如 `AvaloniaOperationRegistry`：静态 `ObservableCollection<OperationViewModel>`）。

### 3.2 导航：ViewModel 持有导航，ContentControl 驱动切换

`MainWindow.axaml` 结构（自上而下）：自绘标题栏（拖拽区/汉堡/返回/全局搜索框 + 建议弹层 + 自绘最小化最大化关闭）→ 左侧导航同一 `SidebarViewModel` 的三种形态（64px NavRail / 250px NavDock / 滑入 NavFlyout，用 `DataTemplate DataType="vm:SidebarViewModel"` 切换）→ **`TransitioningContentControl Content="{Binding CurrentPageContent}"` + 自定义 `EntrancePageTransition`（淡入 + 上滑 28px，模仿 WinUI Frame）** → 底部操作面板（GridSplitter + `ItemsControl` 渲染操作卡片）→ 右下角 Toast 宿主 + `ModalLayer` 窗口内模态。

导航逻辑在 `MainWindowViewModel.NavigateToAsync`：`PageType` 枚举 + `GetPageForType` switch（常用页面常驻字段，其余懒加载缓存），信号量防并发，`IAsyncLeaveGuard.CanLeaveAsync` 拦截未保存更改，`NavigationHistory` 支持返回，`IEnterLeaveListener.OnEnter/OnLeave` 生命周期回调（WebView 页离开即 Dispose 释放 WebView2 进程）。**没有导航服务抽象，ViewModel 直接持有页面实例**——朴素但完全无反射。

### 3.3 MVVM：CommunityToolkit.Mvvm + C# 事件（不用 Messenger）

- `ViewModelBase` 只是 `ObservableObject` 子类；ViewModel 大量用 `[ObservableProperty]`（PackagesPageViewModel 有 38 个）、`[RelayCommand]`、`[NotifyPropertyChangedFor]`、`partial void OnXxxChanged` 联动。
- **全仓无 WeakReferenceMessenger**。VM↔View 通信靠 C# 事件：VM 声明 `HelpRequested`、`PackagesLoaded` 等，code-behind 订阅后弹窗/导航。
- 线程封写法统一：入口先 `if (!Dispatcher.UIThread.CheckAccess()) { Post(...); return; }`。
- **没有 DialogService**：ViewModel 直接构造窗口 `win.ShowDialog(mainWindow)`；文件选择用 `TopLevel.GetTopLevel(visual).StorageProvider`。作者接受"VM 认识具体窗口类"换取简单。

### 3.4 主题与样式：手工 polyfill Fluent 设计令牌

- `App.axaml` 极简（FluentTheme + 样式 Include + 注册 ViewLocator）；真正的样式在 `Assets\Styles\Styles.Common.axaml`（850+ 行，以 `AvaloniaResource` 嵌入）：`ThemeDictionaries` 内 Light/Dark 两套字典，**手工补齐 WinUI Fluent v2 令牌**（`AccentFillColorDefaultBrush`、`TextFillColorPrimaryBrush`、`ControlElevationBorderBrush`……）并派生应用级令牌（`AppWindowBackground`、`AppBorderBrush`、`AppDialogBackground`……）。
- Mica 条件启用（Windows 11 + 用户开启才合并样式字典）；macOS/Linux 用条件 NuGet 包补主题。
- 自绘控件补 Avalonia 缺口：`SettingsCard` 系列（注释明说是 CommunityToolkit.WinUI SettingsCard 的 Avalonia 等价物）、`SvgIcon`、`InfoBar`、`StatusBadge`。
- 本地化用 `MarkupExtensions\TranslateExtension.cs`（`Text="{t:Translate Some text}"`，**加载时求值一次，不随语言热切换**）+ code-behind 用的 `TranslatedTextBlock` 控件。
- 转换器极少（只有一个 `ScaleClampConverter`）——逻辑都放 VM。

### 3.5 大列表与异步加载（对消息流 UI 直接可抄）

- **长列表用 DataGrid**（`AbstractPackagesPage.axaml`）：`ItemsSource="{Binding FilteredPackages}"` + 全 `DataGridTemplateColumn`，自带 UI 虚拟化；排序因 AOT 改用强类型 `CustomSortComparer`（默认的 `SortMemberPath` 反射排序 trim 后失效——这个坑值得记住）。
- **附加属性实现"仅可见行才加载"**：`PackageIconLoader.Track="True"` 在容器 attach/recycle 时回调 `EnsureIconLoaded()`，VM 侧 `PreloadPackageIconsAsync` 设上限并可用 CTS 取消。
- **异步加载反馈三件套**：页顶 indeterminate ProgressBar 绑 `IsLoading`；侧栏导航项 loading 动画（loader 的 `StartedLoading/FinishedLoading` 事件驱动）；空态插画 + "still loading" 副标题。加载完 `MemoryTrimmer.RequestTrimAfterIdle()` 归还内存。

### 3.6 编译绑定纪律（Avalonia + AOT 的命门）

csproj 设 `AvaloniaUseCompiledBindingsByDefault=true`，每个 DataTemplate 带 `x:DataType`，父级 VM 强转写法 `$parent[UserControl].((vm:PackagesPageViewModel)DataContext).Name`。参数化窗口"直接在代码里构造，不经运行时 XAML loader"（csproj 顶层注释）。`ViewLocator` 只服务一个映射且零反射：

```csharp
public Control? Build(object? param)
{
    if (param is SidebarViewModel sidebar) return new SidebarView { DataContext = sidebar };
    return new TextBlock { Text = "Not Found: " + param.GetType().Name };
}
```

### 3.7 其他 UI 基础设施（`Infrastructure\` 42 个文件，节选）

| 文件 | 模式 |
|---|---|
| `TrayService.cs` | 托盘图标 + 按运行状态切换角标色 |
| `Win32ToastNotifier.cs` + `WindowsAppNotificationBridge.cs` | 直接用 UWP toast API（无 Windows App SDK）；点击 toast → `unigetui://` 深链 → 单实例转发 → 解析动作 |
| `MaintenanceScheduler.cs` | 30s DispatcherTimer（无头模式用 System.Timers）驱动维护任务 |
| `ApplicationShutdownCoordinator.cs` | 退出协调（IsQuitting、询问未保存状态） |
| `MotionPreference.cs` | 读 OS"减少动态效果"无障碍偏好，UI 过渡遵循 |
| `AccessibilityAnnouncementService.cs` | 静态事件 → MainWindow 的 1x1 隐藏 Live 区域播报（屏幕阅读器） |
| `MemoryTrimmer.cs` | Windows-only 归还工作集 |
| `RelayCommand.cs` / `AsyncCommand.cs` | 手写极简 ICommand，用于生成器不适用处 |

## 4. Core 服务层

### 4.1 Settings：文件存在型存储（有争议但自洽）

`UniGetUI.Core.Settings\SettingsEngine.cs`：`static partial class Settings`，键是枚举 `Settings.K`（170+ 项，`SettingsEngine_Names.cs` 巨型 switch 映射磁盘文件名）。

- **bool = 文件存在**：`Set(key, false)` 就是 `File.Delete`；**string = 文件内容**，`SetValue(key, "")` 语义为"未设置"（删文件）。`Get(K, bool invert)` 的 invert 参数实现"默认 true"型开关。
- 内存 `ConcurrentDictionary` 读缓存；List/Dictionary 存 `.json` 文件走 source-gen context。
- **没有设置变更事件**（已核实全仓无此机制）——UI 直接读。
- 导入/导出带 `_nonPortableSettings` 排除清单。

**评估**：优点是写放大极小、天然原子（文件存在性）、无解析即无损坏、AOT 友好；缺点是一个设置一个文件、无变更通知、无 schema/默认值集中管理、跨进程修改不同步。对 Fufucode 的建议：设置项少且多为开关时可以照抄这个思路；若需要变更通知或结构化配置，用 JSON + source-gen context 更工程化。**无论选哪种，都值得抄它的可测试性注入口：核心静态类暴露 `TEST_*` internal 覆盖点（如 `CoreData.TEST_DataDirectoryOverride`），测试零 mock 直测真实 IO。**

### 4.2 机密：ACL 而非加密，凭据走系统凭据库

- `UniGetUI.Core.SecureSettings`：**不是 DPAPI/加密**，是文件系统 ACL 模型（装在 Program Files 下普通用户不可写），改设置需自我提权重启（`runas` 启动自身带 `--enable-secure-setting` 参数）。带严密的路径穿越防护。
- 真正的机密在 `Core.Data\CoreCredentialStore.cs`：Windows 用 **Windows 凭据管理器（PasswordVault）**，非 Windows 降级明文文件。

### 4.3 Logger：三路 sink + 脱敏

`UniGetUI.Core.Logger\Logger.cs`：Debug 输出 + `%TEMP%\UniGetUI\session.log`（会话级天然隔离，无轮转）+ 内存列表（供日志窗口）。API：`Debug/Info/Warn/Error/ImportantInfo` × (string/Exception)，`CallerMemberName` 自动记调用点。`Logger.Redact()` 在设置开启时把用户名替换为 `****`，入列前统一过一遍，诊断面复用同一入口。

### 4.4 CoreTools：翻译、版本比较、进程工具

- `CoreTools.Translate()` 三重载 + `ReloadLanguageEngineInstance` 热切换。
- **三套版本比较器按生态选用**：`CoreTools.Version`（数字分段，Homebrew 修订语义）、`SemanticVersion`（SemVer 2.0）、`PythonVersion`（PEP 440）。`IPackageManager.CompareVersions` 返回 null 表示不可比较。**同一字符串（`1.0.0-1`）在不同生态排序相反——比较语义属于被包装的工具，不属于框架**。这条对 harness 解析 agent/模型版本号同样适用。
- 进程工具：`TryReadStandardOutput(ProcessStartInfo, TimeSpan, out string)`（超时 + `Kill(entireProcessTree)`）、`StartAndCloseStandardInput`（修 npm/PowerShell shim 卡 stdin 的坑）、`Which/WhichMultiple`、`EscapeCommandLineArgument`、`IsOptionSafeIdentifier/Value`（命令行注入校验）、`CacheUACForCurrentProcess`（UAC 缓存）。全是 Windows 边缘情况实战注释的沉淀。

### 4.5 其余

- `Core.Data\CoreData.cs`：版本常量（由 `scripts/set-version.ps1` 盖章）、数据目录族（便携模式优先 + 旧目录迁移）、`ElevatorPath` 可变静态、`TEST_*` 注入口。`AppPaths.cs`（在 Logger 工程里，命名空间却属 Core.Data——历史遗留）：安装目录/便携判定/Scratch 目录。
- `Core.LanguageEngine`：翻译是**扁平 JSON（英文原文为键）**，40+ 语言，**不打进程序集**而是以 Content 方式部署到 exe 旁 `Assets\Languages\`（支持不改包换翻译）；`Utf8JsonReader` 手写解析（无反射、AOT 安全、容忍尾逗号），占位符用 `Jeffijoe.MessageFormat`，locale 别名表 + 逐级回退到 en。
- `Core.IconStore`：远端图标数据库（1 天刷新）→ `TaskRecycler` 去重下载（30s 结果缓存 + 负缓存）→ `<manager>\<pkgId>_<sha256前8>` 缓存目录 + 元数据校验 → MagicScaler 缩放。
- `Core.Classes\TaskRecycler.cs`：按委托哈希去重的任务池，并发同参调用附着到同一在途 Task（`RunOrAttach` + cacheTimeSecs 结果保留）。整个引擎广泛使用——**"查询去重"用一个小类解决，而不是引入缓存框架**。

## 5. 横切设施

### 5.1 IpcApi：AOT 桌面进程内 REST 服务器

`UniGetUI.Interface.IpcApi\IpcServer.cs`：`WebApplication.CreateSlimBuilder` + Kestrel，传输三选一——Windows 命名管道 / Unix socket（默认）、localhost TCP（可选）。minimal API 注册全部端点（`/uniget/v1/...`，涵盖 operations/managers/sources/settings/packages/bundles/logs/backups）。要点：

- **认证**：每 session 运行期生成 64 字符随机 token；除 status 外全部端点校验 `?token=`；**端点元数据（transport/pid/sessionId/token）落盘到 `IpcApiEndpoints/<sessionId>.json`**，客户端从持久化注册里发现并连接——比固定端口 + 共享密钥更适合多实例。
- **UI 封送**：UI 相关端点用委托回调，`AvaloniaBootstrapper` 接线时包 `Dispatcher.UIThread.InvokeAsync`；headless 下同一 IpcServer 但委托直接抛异常变 HTTP 400。
- `IpcJson.cs` 定义 103 个 `[JsonSerializable]` 的 source-gen context（含显式注册 `IReadOnlyList<T>`）。
- GUI 内启动方式：`_ = Task.Run(InitializeIpcApiAsync)` fire-and-forget，Kestrel 自管线程。
- `WindowsConsoleHost.cs`：WinExe 下 `AttachConsole(ATTACH_PARENT_PROCESS)` + 重绑 UTF8 std 流，让 GUI 进程能输出 CLI 结果。

### 5.2 CLI 三层 + 单实例转发

1. **Pre-UI flag**（`--export-settings` 等）：`SharedPreUiCommandDispatcher`（共享源码 Compile-Include 进 UI 工程）本地执行后退出；
2. **动词 CLI**（`unigetui package install ...`）：`IpcCliCommandRunner` **作为 IPC 客户端**连到运行中的实例执行后退出（即"单实例转发"的实现方式）；退出码枚举 0 成功 / 1 失败 / 2 参数错 / 3 IPC 不可用 / 4 未知命令；
3. **`--headless`**：纯 API 守护进程，同一 IpcServer，`SessionKind` 区分。

第二实例 GUI 用命名管道把 args 转发给首实例（事件先缓冲到队列直到订阅者挂上，5 秒预算 + 100ms 重试）。

### 5.3 崩溃处理：无第三方 SDK

`AppDomain.UnhandledException` → `CrashHandler.ReportFatalException`：生成文本报告（环境 + 完整性校验结论 + 异常链），经 `Logger.Redact` 脱敏后：打印 stdout → **写入 `%TEMP%\UniGetUI\pending_crash.txt`，下次正常启动由 `CrashReportWindow` 展示** → `Environment.Exit(1)`。文件缺失型 HResult 特判弹 Win32 MessageBox 提供一键重装。无 Watson/任何 crash SDK。

### 5.4 打包、CI、密钥

- 发布：6 个 `PublishProfiles/*-NativeAot.pubxml`（self-contained + Trim full + 非单文件）；`scripts/build.ps1` 流程 = test → publish → staging → 删大 PDB → **刷新完整性树**（全文件 SHA256，运行时 `IntegrityTester` 校验，崩溃报告附带结论）→ zip → Inno Setup 安装器（`PrivilegesRequired=lowest`）。
- 提权助手 exe 由发布后 Target 单独 NativeAOT 发布并 staged（签名 + 进完整性树）。
- CI：`dotnet-test.yml`（restore/format --verify/build/test 四连）；release workflow 里**先签名后生成完整性树**（helper 签名被运行时校验）。
- 密钥：第三方凭据（OAuth、遥测）走**构建期环境变量 → `generate-secrets.ps1` 生成 `Secrets.Generated.cs`**；未配置（社区构建）时遥测整体禁用。运行期 IPC token 与构建期密钥无关。
- 遥测：opt-out 模型，设备标识是本地随机字符串非硬件 ID，事件类只有 3 个字段级属性 + source-gen 序列化。

## 6. 对 Fufucode 的启示（按"直接搬 / 改造 / 不学"分类）

### 6.1 直接可搬（Pi RPC harness 场景一一对应）

1. **进程封装范本**（`AbstractProcessOperation`）：逐字符读 stdout 区分 `\r` 进度行与 `\n` 内容行、stderr 异步读防死锁、`Kill(entireProcessTree:true)`、取消三处检查点、`GetRawOutput` 保留真值源。Pi 进程的输出泵可以按这个骨架写。
2. **Operation 对象 + 事件 + 队列**：Fufucode 的"工具执行卡片"与 UniGetUI 的操作卡片几乎同构——`StatusChanged/LogLineAdded/ProgressChanged/CancelRequested` 事件面、静态队列 + MAX 并发、`AutoRetry` 输出驱动重试、`OperationCardController` 式纯状态机 gate 显示逻辑。连"结构化进度持有状态行时抑制原始行"这种细节都有现成答案。
3. **崩溃 pending 文件**：`AppDomain.UnhandledException` → 落盘 → 下次启动展示。零依赖，直接抄。
4. **单实例 + 第二实例参数转发**：Mutex/flock + 命名管道缓冲转发（含"事件先缓冲到队列直到订阅者挂上"）。
5. **设置引擎的 `TEST_*` 注入口模式**：无论设置存储选什么方案，这个可测试性手法直接抄。
6. **Avalonia 惯用法**：`TransitioningContentControl` + 自定义 `IPageTransition` 导航；`AvaloniaUseCompiledBindingsByDefault=true` + `x:DataType` 纪律；DataGrid/ItemsControl 虚拟化 + 附加属性按需加载（消息列表图标/头像同理）；indeterminate ProgressBar + 侧栏 loading 动画 + 空态三件套；Toast 上限 + 悬停暂停；`MemoryTrimmer`。
7. **source-gen JSON 全覆盖**：Pi RPC 的 DTO 本来就是固定 schema，`JsonSerializerContext` 是正解；语言/配置文件解析避开反射反序列化。
8. **装配容错**：静态工厂吞异常 + 并行初始化带总超时 + 结果增量事件 + 世代号防过期回填——多 Pi 会话/多 provider 场景照用。

### 6.2 改造后用

1. **三 Helper 拆分 + `_UnSafe` 模式**：Fufucode 第一版只有 Pi 一个"后端"，但接口层（`IAgentProcess` 之类）+ 抽象基类（超时/重试/自愈骨架）+ 具体实现（Pi RPC）的形状可以先立起来；将来加本地模型或别的 agent 后端时收益兑现。`Capabilities` 声明式能力结构体对"Pi 扩展能力探测 → UI 渲染"也成立。
2. **查询/操作双管线**：对应"元数据查询（会话列表、配置）"与"会话消息流（长连接事件流）"分离。UniGetUI 的教训是两条管线线程模型完全不同（同步限时 vs 事件驱动），不要混在一个 API 上。
3. **IpcApi 的 token 落盘发现**：Fufucode 是 RPC 客户端侧，但如果将来提供本地自动化 API 或多会话管理，"每 session 随机 token + 端点元数据 JSON 落盘 + headless/GUI 同服务器"是现成蓝图。它同时演示了 GUI 进程内宿主 Kestrel 的正确姿势（fire-and-forget + 委托封送 UI 线程）。
4. **提权一次性助手进程**：Fufucode 首版无提权需求，但"提权面压缩成最小无状态强认证进程"的思路在将来需要写受保护位置时直接取用；AgentBroker 的"不可用绝不降级本地执行"是任何策略化执行路径的铁律。
5. **Settings 文件存在型**：Fufucode 设置项少，两种选择——照抄（简单可靠）或 JSON + source-gen（可通知、可结构化）。**建议 JSON + source-gen**，但保留 `K` 枚举集中键名、导入导出排除清单、`invert` 默认值这三个具体手法。
6. **Fluent 令牌 polyfill**：直接抄一份 Light/Dark 令牌字典作为主题基座（可精简），比从零设计色板快且效果接近 WinUI。
7. **构建约定**：双 TFM 机制、`Directory.Build.props` 集中管理、`EnforceCodeStyleInBuild`、共享 `SharedAssemblyInfo.cs` 链接引入。NativeAOT 对 Fufucode 首版可以不作为硬约束（代价高），但**编译绑定纪律与 source-gen JSON 应当现在就执行**，将来要 AOT 时不用返工。

### 6.3 不学 / 存疑

1. **完全无 DI 容器 + 大量静态单例**：UniGetUI 体量（40 工程）下这是务实选择，且 NativeAOT 理由充分；但它让隐式依赖顺序（`PEInterface.LoadLoaders()` 必须先于 `new MainWindow()`）散落在调用序里。Fufucode 体量更小，简单手动构造 + 少量静态服务是合适的，不必引入容器，也**不必为了"现代化"引入**——跟随UniGetUI 的判断即可，只是新代码要注意别把初始化顺序藏得太深。
2. **ViewModel 直接 new 具体窗口**（无 DialogService）：代码短，但测试性差、平台耦合上移。Fufucode 若要测试会话流 UI 逻辑，值得在 VM 事件 → View 弹窗这条线上保持纪律（VM 发事件，View 决定弹什么）。
3. **`OperationVeredict` 拼写**：仓库自认的历史 misspelling，勿复制。
4. **设置无变更通知**：如果 Fufucode 有"设置改了、运行中的会话要感知"的需求（大概率有，如模型切换），UniGetUI 的方案不够用，需要自己加事件层。
5. **一个 Infrastructure 文件夹塞 42 个文件**：边界从"启动引导"蔓延到"自动更新、GitHub 备份、WebView 探测"。Fufucode 建议按职责分目录（host/ services/ behaviors/），避免重演。
6. **导航懒加载页面的内存驻留策略**、Linux 不强单实例的原因等本快照中未核实，引用前需回查源码。

## 7. 关键文件索引（回查用）

| 主题 | 路径（相对 `reference/UniGetUI/`） |
|---|---|
| 仓库自述架构说明 | `AGENTS.md` |
| 引擎接口 | `src/UniGetUI.PackageEngine.Interfaces/IPackageManager.cs` |
| 抽象基类（`_UnSafe`/超时/自愈） | `src/UniGetUI.PackageEngine.PackageManagerClasses/Manager/PackageManager.cs` |
| 操作事件总线 | `src/UniGetUI.PackageEngine.Operations/AbstractOperation*.cs` |
| 进程封装范本 | `src/UniGetUI.PackageEngine.Operations/AbstractProcessOperation.cs` |
| 参考管理器实现 | `src/UniGetUI.PackageEngine.Managers.Scoop/Scoop.cs` + `Helpers/` |
| 装配根（容错工厂） | `src/UniGetUI.PackageEngine.PackageEngine/PEInterface.cs` |
| 列表加载器 | `src/UniGetUI.PackageEngine.PackageLoader/AbstractPackageLoader.cs` |
| App 宿主 | `src/UniGetUI.Avalonia/Infrastructure/AvaloniaAppHost.cs`、`AvaloniaBootstrapper.cs` |
| 主窗口/导航 | `src/UniGetUI.Avalonia/Views/MainWindow.axaml(.cs)`、`ViewModels/MainWindowViewModel.cs` |
| 包列表页（虚拟化样板） | `src/UniGetUI.Avalonia/Views/SoftwarePages/AbstractPackagesPage.axaml(.cs)` |
| 按需图标加载 | `src/UniGetUI.Avalonia/Views/Controls/PackageIconLoader.cs` |
| Fluent 令牌字典 | `src/UniGetUI.Avalonia/Assets/Styles/Styles.Common.axaml` |
| 本地化标记扩展 | `src/UniGetUI.Avalonia/MarkupExtensions/TranslateExtension.cs` |
| IPC 服务器 | `src/UniGetUI.Interface.IpcApi/IpcServer.cs`、`IpcJson.cs` |
| 单实例转发 | `src/UniGetUI.Avalonia/Infrastructure/SingleInstanceRedirector.cs` |
| 崩溃处理 | `src/UniGetUI.Avalonia/CrashHandler.cs` |
| 设置引擎 | `src/UniGetUI.Core.Settings/SettingsEngine.cs` |
| 凭据存储 | `src/UniGetUI.Core.Data/CoreCredentialStore.cs` |
| 工具集（翻译/版本/进程） | `src/UniGetUI.Core.Tools/Tools.cs` |
| 任务去重池 | `src/UniGetUI.Core.Classes/TaskRecycler.cs` |
| AgentBroker | `src/UniGetUI.PackageEngine.AgentBroker/` |
| 一次性提权助手 | `src/UniGetUI.AgentPolicy.ElevatedHelper/Program.cs` |
| 打包脚本 | `scripts/build.ps1`、`scripts/publish-nativeaot.ps1`、根目录 `UniGetUI.iss` |
| 构建/发布文档 | 根目录 `docs/CLI.md`、`docs/IPC.md` |

---

*本文为源码研读笔记，描述的是 UniGetUI 快照（提交 9f55ba5f）的设计现状；其中"对 Fufucode 的启示"是建议而非已定实现方案，采纳与否以开发计划为准。*
