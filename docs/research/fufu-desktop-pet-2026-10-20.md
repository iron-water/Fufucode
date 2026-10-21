# Fufucode「召唤 FUFU」桌面宠物调研与技术方案

日期：2026-10-20  
范围：只调研，不修改产品代码，不提交 Git。  
结论状态：Avalonia API 已用本地 12.1.3 XML 和编译探针核对；透明窗口的真实 Windows 行为、混合点击穿透和多 DPI 仍需 P0 原型。

## 结论先行

建议把 FUFU 做成独立可选插件 `pet.fufu`，归类为后台服务类插件。默认包不带 FUFU 程序集和素材。插件拥有自己的窗口、动画、状态映射和偏好文件。App 只提供托盘菜单、当前会话桥接和插件生命周期。

最保守的渲染方案是少量原创 PNG 状态图，加 Avalonia 的 `Animation`/`Transitions` 做透明度、位移和缩放过渡。P0 先只显示一张占位图，整窗可交互、可拖动；混合点击穿透放到 P2。不要在首版引入 Lottie、Spine 或额外 shader 运行时。

最大风险是 Windows 分层透明窗口的命中测试、`WS_EX_NOACTIVATE` 与拖动/点击的组合，以及不同显示器 DPI 下的坐标恢复。P0 必须在真实 Windows 桌面上验证这些行为。

## 1. 当前代码和约束

- App 工程是 `net10.0`，引用 `Avalonia`、`Avalonia.Desktop`、`Avalonia.Themes.Fluent`、`Avalonia.Skia`，版本均为 `12.1.3`；SkiaSharp 为 `3.119.4`。证据：[src/Fufucode.App/Fufucode.App.csproj](../../src/Fufucode.App/Fufucode.App.csproj:1)。
- `App.OnFrameworkInitializationCompleted` 创建唯一主窗口并设置 `desktop.MainWindow`。证据：[src/Fufucode.App/App.axaml.cs](../../src/Fufucode.App/App.axaml.cs:57)。
- 后台常驻默认关闭。开启 `AppSettings.Background.CloseToTray` 后，主窗口隐藏但会话和后端继续运行；托盘退出才结束进程。证据：[src/Fufucode.App/Views/MainWindow.axaml.cs](../../src/Fufucode.App/Views/MainWindow.axaml.cs:1248)、[docs/decisions.md](../decisions.md:666)。FUFU 应跟随这一生命周期：隐藏主窗口不应自动销毁宠物，显式退出必须关闭宠物。
- 托盘服务在每次菜单弹出前通过 `NativeMenu.NeedsUpdate` 调 `RebuildMenu`，由组合根填充菜单。当前菜单已包含显示主窗口、固定/最近会话、 新建会话、关于 Fufucode、主题色、设置、FUFU 占位和退出。证据：[src/Fufucode.App/Integration/TrayIconService.cs](../../src/Fufucode.App/Integration/TrayIconService.cs:11)、[src/Fufucode.App/Views/MainWindow.axaml.cs](../../src/Fufucode.App/Views/MainWindow.axaml.cs:1300)。
- 调研开始时工作树尚未找到 `FillTrayMenu` 方法或“召唤 FUFU（即将推出）”文本；随后主会话已补上动态托盘菜单并完成构建。当前菜单保留 FUFU 禁用占位项，真实宠物仍待独立插件实现。
- 会话契约已经提供跨后端的活动和终态事件：`SessionActivity` 为 `Idle/Running/Compacting/Retrying/Cancelling`，`SessionOutcome` 为 `Completed/Failed/Interrupted/Unknown`；`IChatSession.Event` 可在任意线程触发，订阅方必须封送 UI 线程。证据：[src/Fufucode.App/Abstractions/IChatSession.cs](../../src/Fufucode.App/Abstractions/IChatSession.cs:7)、[src/Fufucode.App/Abstractions/SessionEnums.cs](../../src/Fufucode.App/Abstractions/SessionEnums.cs:42)、[src/Fufucode.App/Abstractions/SessionEvents.cs](../../src/Fufucode.App/Abstractions/SessionEvents.cs:39)。
- AGENTS.md 要求新增能力先判断能否独立安装、更新、禁用和卸载；插件目录或开关不能冒充真实安装架构。FUFU 符合独立插件条件。证据：[AGENTS.md](../../AGENTS.md:62)。

## 2. Avalonia 12.1.3 窗口路径

### 2.1 已核实的 API

本机文件存在：

`C:\Users\mio\.nuget\packages\avalonia\12.1.3\lib\net10.0\Avalonia.Controls.xml`

关键成员和文档位置如下：

| 需求 | Avalonia API | 本地证据 |
|---|---|---|
| 透明背景请求 | `TopLevel.TransparencyLevelHint`、`WindowTransparencyLevel.Transparent` | `Avalonia.Controls.xml:22226-22243`、`24326-24349` |
| 判断平台实际结果 | `TopLevel.ActualTransparencyLevel` | `Avalonia.Controls.xml:22234-22237` |
| 透明失败回退 | `TopLevel.TransparencyBackgroundFallback` | `Avalonia.Controls.xml:22239-22243` |
| 无边框 | `Window.WindowDecorations = WindowDecorations.None` | `Avalonia.Controls.xml:23609-23626`、`23820-23828` |
| 置顶 | `WindowBase.Topmost` | `Avalonia.Controls.xml:24082-24090` |
| 不进任务栏 | `Window.ShowInTaskbar = false` | `Avalonia.Controls.xml:23687-23691`、`23835-23840` |
| 首次显示不激活 | `Window.ShowActivated = false` | `Avalonia.Controls.xml:23682-23685`、`23830-23834` |
| 禁止用户缩放 | `Window.CanResize = false` | `Avalonia.Controls.xml:23717-23721`、`23852-23855` |
| 手动位置 | `Window.WindowStartupLocation = Manual`、`Window.Position` | `Avalonia.Controls.xml:23885-23893`、`24281-24298` |
| 拖动 | `Window.BeginMoveDrag(PointerPressedEventArgs)` | `Avalonia.Controls.xml:23900-23908` |
| 获取 HWND | `TopLevel.TryGetPlatformHandle()` | `Avalonia.Controls.xml:22253-22264` |
| 屏幕和 DPI | `WindowBase.Screens`、`Screens.All`、`Screen.Bounds`、`Screen.WorkingArea`、`Screen.Scaling`、`TopLevel.RenderScaling`、`WindowBase.DesktopScaling` | `Avalonia.Controls.xml:17789-17860`、`22312-22324`、`24079-24093`、`27556-27608` |
| 屏幕/缩放变化 | `Screens.Changed`、`TopLevel.ScalingChanged`、`WindowBase.PositionChanged` | `Avalonia.Controls.xml:17809-17812`、`22201-22215`、`24051-24055` |
| Win32 样式 | `Win32Properties.AddWindowStylesCallback` / `RemoveWindowStylesCallback` | `Avalonia.Controls.xml:16117-16129` |
| Win32 消息 | `Win32Properties.AddWndProcHookCallback` / `RemoveWndProcHookCallback` | `Avalonia.Controls.xml:16131-16143` |
| 非客户区命中 | `Win32Properties.SetNonClientHitTestResult` | `Avalonia.Controls.xml:16145-16157` |

另外用引用 `Avalonia 12.1.3` 的临时编译探针验证了属性和方法的成员存在，结果为 0 错误、0 警告。探针也验证了：

- `Window` 没有 `AllowsTransparency` 属性。WPF 的 `AllowsTransparency` 不能照搬；Avalonia 12.1.3 应使用 `TransparencyLevelHint`。
- `Win32Properties.CustomWindowStylesCallback` 的签名返回 `(uint style, uint exStyle)`，参数是 `uint style, uint exStyle`。
- `Win32Properties.CustomWndProcHookCallback` 的参数是 `IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled`，返回 `IntPtr`。
- `Win32HitTestValue` 只有 `Nowhere/Client/Caption/.../Close`，没有 `Transparent` 成员。它不能直接表达“把鼠标交给下面的其他应用”。混合穿透需要 WndProc 返回 Win32 的 `HTTRANSPARENT`，或在可交互态和穿透态之间切换窗口样式。
- 直接在未初始化 Avalonia 桌面平台时创建 `Window` 会抛 `Unable to locate 'Avalonia.Platform.IWindowingPlatform'`。这说明编译探针不能替代真实桌面运行验证；P0 需在正常 `AppBuilder`/Windows 桌面进程中验证。

### 2.2 推荐的 P0 窗口配置

实现时可从以下属性集合开始，具体 `Background`/资源加载方式在 P0 原型中确认：

```csharp
var pet = new FufuPetWindow
{
    TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
    TransparencyBackgroundFallback = Brushes.Transparent,
    WindowDecorations = WindowDecorations.None,
    Topmost = true,
    ShowInTaskbar = false,
    ShowActivated = false,
    CanResize = false,
    WindowStartupLocation = WindowStartupLocation.Manual,
    Background = Brushes.Transparent,
};
```

显示前把 `Position` 设为经过屏幕工作区校正的 `PixelPoint`。首次召唤只调用 `Show()`，不要调用 `Activate()`。点击角色时可允许宠物获得焦点；点击穿透区域时不应把焦点带离主窗口。

`TransparencyLevelHint` 是请求，不是保证。显示后读取 `ActualTransparencyLevel`。如果不是 `Transparent`，记录诊断并退回不透明占位窗口或隐藏宠物，不能显示黑/白矩形后宣称透明成功。

### 2.3 点击穿透和可交互区域

建议分三档实现：

1. **P0：整窗可交互。** 不设置 `WS_EX_TRANSPARENT`，所有命中都落到宠物窗口。这样先验证透明、拖动、焦点和生命周期，避免把两个问题混在一起。
2. **P1：按状态切换。** “互动模式”整窗可点；“观赏模式”设置 `WS_EX_NOACTIVATE`，并通过 `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`，尽量不抢主窗口焦点。仍不承诺任意透明像素穿透。
3. **P2：像素/区域命中。** 在 WndProc 钩子处理 `WM_NCHITTEST`。角色轮廓或交互热区返回 `HTCLIENT`；非交互区返回 `HTTRANSPARENT`。如果跨进程穿透在某些桌面组合器上不稳定，回退到矩形窗口或“点击穿透开关”，不能依赖 `WS_EX_TRANSPARENT` 单独实现命中测试。

Windows 样式的建议用途：

| 样式 | 用途 | 结论 |
|---|---|---|
| `WS_EX_LAYERED` | 分层/透明窗口 | Avalonia 透明后端通常会负责；只有 HWND 检查证明缺失时再补，不要无条件重复设置。 |
| `WS_EX_TOOLWINDOW` | 工具窗口，隐藏 Alt+Tab，并辅助避免任务栏按钮 | 建议 P0 设置并用 Alt+Tab 实测。仍保留 `ShowInTaskbar=false`。 |
| `WS_EX_NOACTIVATE` | 鼠标点击不激活窗口 | 适合观赏/穿透态；会影响键盘焦点和部分拖动语义，必须原型验证。 |
| `WS_EX_TRANSPARENT` | Windows 的绘制顺序语义 | 它不是可靠的鼠标点击穿透开关。不能单独作为 FUFU 命中方案。 |

`Win32Properties.AddWindowStylesCallback` 可在创建时设置样式；`AddWndProcHookCallback` 可处理 `WM_NCHITTEST`、`WM_MOUSEACTIVATE` 和必要的窗口消息。回调必须在关闭/卸载时移除，防止插件卸载后仍引用窗口或插件对象。

## 3. 多显示器、DPI、焦点和生命周期

- `Window.Position` 是屏幕坐标；XML 明确说明它是 screen coordinates。`Screen.Bounds` 和 `Screen.WorkingArea` 是像素区域，`Screen.Scaling` 是操作系统缩放因子。
- 启动时用 `pet.Screens.ScreenFromWindow(pet)` 或按 `Screens.ScreenFromBounds` 找显示器。优先恢复保存的 `DisplayName`；显示器不存在时选主屏，再将位置夹到 `WorkingArea`。
- 不只保存裸 `x/y`。建议保存：`screenDisplayName`、工作区内归一化锚点、DIP 偏移、窗口 DIP 尺寸、保存时 scaling。显示器更换或 DPI 变化时先换算，再按当前 `Screen.Scaling` 重新夹紧。
- 订阅 `Screens.Changed`、`ScalingChanged` 和 `PositionChanged`。位置写盘使用 250–500 ms 防抖；关闭/隐藏前强制 flush。不要在每一个 `PositionChanged` 事件同步写 JSON。
- 不设置 `Owner = MainWindow` 作为默认方案。Owner 会把关闭/激活关系带入宠物窗口；后台托盘模式下宠物需要独立隐藏和恢复。由 App 组合根显式管理宠物窗口引用和关闭顺序。
- 召唤时保存主窗口当前焦点元素，宠物 `ShowActivated=false`；主窗口保持活动时不调用 `Activate`。宠物被用户点击后若要“互动但不打扰输入”，用 `WM_MOUSEACTIVATE` 和 `ShowActivated` 组合验证，不能只凭属性名称推断。
- 主窗口 `HideToTray` 时，若用户已召唤 FUFU，宠物可以继续显示；`ExitApplication`、`OnClosed` 和插件 `ShutdownAsync` 必须按“先取消事件订阅，再关闭窗口，再释放资源”的顺序执行。
- 系统休眠、注销和显示器断开时，先保存状态并隐藏。恢复后重新校正位置。显示器消息和电源消息属于 P/Invoke/WndProc 原型项，Avalonia 的屏幕事件不能覆盖所有 Windows 电源边界。

## 4. 动画方案对比和选型

| 方案 | 内存 | CPU/GPU | 启动和依赖 | 评价 |
|---|---|---|---|---|
| 逐帧 PNG / 小序列 | 与缓存帧数线性增长；原始 RGBA 约为 `宽×高×4×帧数`。例如 256×256×24 帧约 6 MiB；多个状态会继续相加 | 解码和位图切换有成本；预缓存少量帧可控 | 无新增库；PNG 资源路径直观 | 最容易回退和验收。首版只缓存当前状态的 1–2 帧。 |
| Avalonia `Animation` / `Transitions` | 很低，不复制整组位图 | 透明度、Transform、Scale 等适合合成；已有代码在使用 `DoubleTransition` | 已在 Avalonia 12.1.3 和 App 中可用 | 适合状态切换和轻微呼吸/漂浮，不适合复杂逐帧角色本体。 |
| Skia/SKSL shader | 纹理和 shader 状态较小，但每帧绘制复杂度取决于采样和效果 | 依赖 GPU/Skia；无 GPU 时需完整回退 | App 已引用 `Avalonia.Skia` 和 `SkiaSharp`，但这会把 FUFU 与 App 皮肤管线耦合 | 适合光晕、波纹等程序效果，不适合作为首版角色动画。 |
| Lottie | 通常比 PNG 小，运行时需 JSON 解析和矢量栅格化 | 复杂路径、蒙版和文本会增加 CPU/GPU | 当前项目未找到 Lottie 包或现有接线；需另选库并核许可证 | 首版不引入，避免新增包、裁剪和资源回退问题。 |
| Spine 类库 | 骨骼动画内存较省，换皮能力强 | 运行时和授权边界更复杂 | 当前项目未找到 Spine 包或接线；授权需单独核对 | 不适合 P0/P1 的保守交付。 |

选型：**原创 PNG 状态图 + Avalonia `Animation`/`Transitions`**。P1 可以在 `Idle/Thinking/Completed/Error` 之间切换 PNG，并只用 `Opacity`、`RenderTransform`、`Scale` 做状态过渡。动画定时器必须在隐藏、停用和关闭时停止；系统减少动画偏好下退化为静态图。

当前 App 已有 `Avalonia.Animation`、`DoubleTransition`、`RequestAnimationFrame` 和 SKSL 的实际使用证据，但 FUFU 插件尚未拥有任何动画管线。证据：[src/Fufucode.App/Views/MainWindow.axaml.cs](../../src/Fufucode.App/Views/MainWindow.axaml.cs:1183)、[src/Fufucode.App/Features/Skins/LiquidGlass/LiquidGlassDrawOperation.cs](../../src/Fufucode.App/Features/Skins/LiquidGlass/LiquidGlassDrawOperation.cs:15)。

## 5. 资源、体积和分发

### 5.1 现有资源事实

生产资产根目录当前有 `icon/`、`empty/`、`actions/`、`titlebar/`、`installer/` 等族。完整清单称生产文件为 441 SVG、576 PNG、1 ICO；`review/` 和 `reference/` 不是生产载荷。证据：[assets/manifest.md](../../assets/manifest.md:1)、[assets/manifest.md](../../assets/manifest.md:30)。

当前没有 FUFU 角色素材。对 `assets/` 的 `fufu` 搜索没有命中；`assets/reference/workbuddy/` 是参考资料，不应把其中素材复制进产品。素材必须原创或有明确许可。

### 5.2 推荐新资源布局

插件源码：

```text
src/Fufucode.Plugins.Fufu/
  Fufucode.Plugins.Fufu.csproj
  manifest.json
  FufuPetPluginPackage.cs
  FufuPetController.cs
  FufuPetWindow.axaml
  FufuPetWindow.axaml.cs
  Assets/
    idle/*.png
    thinking/*.png
    completed/*.png
    error/*.png
    skins/<skin-id>/*.png
```

推荐把资源放进插件包自己的 `Assets/`，而不是加入 `Fufucode.App.csproj`。可选插件不应把角色素材带进默认 App 包。两种载荷方式都可行，但应在 P0 选定一种并固定：

- `AvaloniaResource Include="Assets\\**\\*"`：资源编译进插件程序集；用插件程序集的 `avares://` URI 读取。需要验证动态 `AssemblyLoadContext` 卸载前没有静态 URI/Bitmap 引用。
- 普通文件随插件 zip 分发：从 `PluginPackageContext.PackageDirectory` 的相对 `Assets/` 读取。动态卸载更直观，也便于后续换皮和资源完整性校验。

本项目已有插件 zip、manifest、分发目录和动态加载路径。`PluginDistributionService` 将分发包解压到 `<dataRoot>/distribution/<id>`；`PluginPackageManager` 再校验 manifest、加载入口程序集并调用生命周期。证据：[src/Fufucode.Core/Plugins/PluginDistributionService.cs](../../src/Fufucode.Core/Plugins/PluginDistributionService.cs:7)、[src/Fufucode.Core/Plugins/PluginPackageManager.cs](../../src/Fufucode.Core/Plugins/PluginPackageManager.cs:129)。

建议 FUFU 资源随 `pet.fufu.zip` 分发，包内包含 `manifest.json`、程序集和 `Assets/`。先用压缩包 SHA-256 校验，再解压到 staging，最后原子切换。素材尺寸和帧数写入 manifest 或资源索引，P1 验证安装后总包体积和启动解码耗时。

## 6. 插件归属和宿主接线

### 6.1 归属判断

选择独立插件 **`pet.fufu`**，而不是 `workspace.fufu`：

- 它不是工作区页面，不需要占用 Dock 或页面页签。
- 它可以独立安装、禁用、更新和卸载。
- 它有独立资源、窗口、动画、会话订阅和偏好。
- 它可在后台托盘模式下继续显示，生命周期与工作区页面不同。

插件市场已有 `Service` 分类，也已有非内置、默认未安装的插件模式。建议新增目录项时使用：

```text
Id: pet.fufu
Category: Service
DefaultInstalled: false
DefaultEnabled: false
IsCoreBuiltin: false
```

不要把它加入 `PluginBootstrapper.BundledPluginIds`。默认包只随核心和已明确默认分发的插件播种；FUFU 应走用户主动安装路径。插件目录、状态和开关必须由真实载荷驱动，不能只登记一行市场数据。

### 6.2 现有宿主接线点

- 市场目录：`src/Fufucode.Core/Plugins/PluginMarketplace.cs` 的 `PluginMarketplaceCatalog.All`。
- 安装/更新/卸载/启用：`PluginPackageManager.InstallPackage`、`UpdatePackage`、`SetPackageEnabled`、`UninstallPackage`。
- 分发和 SHA-256：`PluginDistributionService.InstallAsync`。
- 动态入口契约：`IPluginPackage.InitializeAsync`、`ShutdownAsync`、`CreatePageContribution` 等，证据：[src/Fufucode.Core/Plugins/IPluginPackage.cs](../../src/Fufucode.Core/Plugins/IPluginPackage.cs:61)。
- 运行时服务注入：`PluginPackageContext.GetService<T>()` 和 `PluginPackageManager.ServiceResolver`，现有性能插件已使用同一模式。证据：[src/Fufucode.App/Features/Performance/PerfHostWiring.cs](../../src/Fufucode.App/Features/Performance/PerfHostWiring.cs:49)。
- 页面宿主只处理 `CreatePageContribution`。FUFU 不应伪造空页面；应走独立的服务/控制器贡献。证据：[src/Fufucode.App/Features/Plugins/PluginWorkspacePageHost.cs](../../src/Fufucode.App/Features/Plugins/PluginWorkspacePageHost.cs:12)。

现有 `IPluginPackage` 没有“独立窗口/服务贡献”的专门契约。建议在实现 FUFU 前增加一个最小、与 Avalonia UI 解耦的宿主合同，例如 `IFufuPetHost`/`IFufuPetController`，放在 Core 插件契约或现有 App Abstractions 中：

```text
IFufuPetController
  IsAvailable
  IsVisible
  ShowOrToggle()
  Hide()
  SetSession(IChatSession? session)
  ShutdownAsync()
```

`FufuPetPluginPackage.InitializeAsync` 通过 `context.GetService<IFufuPetHost>()` 获取宿主桥，创建自己的 `FufuPetController` 并注册到宿主服务注册表。若当前插件宿主没有通用 service contribution 注册表，先增加一个只服务于这类生命周期型插件的注册/注销接口；不要把 FUFU 的窗口字段硬塞进 `MainWindow` partial。

插件本身可以引用 Avalonia 来创建 `Window`，但 Core 契约只传会话和无 UI 的宿主能力。这样 Core 不需要引用具体 `FufuPetWindow`，卸载时可以先关闭窗口，再卸载 `AssemblyLoadContext`。

### 6.3 禁用、卸载和数据保留

- 禁用：先调用插件 `ShutdownAsync`，取消会话事件、停止动画、隐藏并关闭窗口，注销 `IFufuPetController`，再写 `IsEnabled=false`。
- 卸载：先禁用，再确认窗口句柄、Bitmap、计时器和事件订阅都释放，之后移除程序集和依赖。
- 当前 `PluginPackageManager.UninstallPackage` 会删除 `<dataRoot>/plugins/<id>`；因此不要把用户的 FUFU 偏好放在 payload 目录。应新增或约定独立数据目录，例如 `<dataRoot>/plugin-data/pet.fufu/state.json`，卸载时保留该目录并明确提供“同时删除设置”的后续入口。
- 当前 `AppDataLayout` 已有 `Root`、`PluginsDirectory` 等路径，但没有 `PluginDataDirectory`。这是实现前需要补的宿主路径能力，不应由插件通过字符串猜测目录层级。

## 7. 托盘、会话和持久化交互

### 7.1 托盘菜单

`TrayIconService` 的菜单在 `NativeMenu.NeedsUpdate` 时重建。因此组合根应提供实际的 `FillTrayMenu(NativeMenu menu)`，顺序建议：

```text
显示主窗口
召唤 FUFU / 隐藏 FUFU
────────────
退出
```

菜单动作规则：

- 插件已安装且启用：调用 `IFufuPetController.ShowOrToggle()`，菜单文案按 `IsVisible` 变为“召唤 FUFU”或“隐藏 FUFU”。
- 插件未安装：显示“召唤 FUFU（安装插件）”或禁用项，并给出设置/插件市场入口；不能调用空对象。
- 宠物窗口创建失败：菜单保留，显示失败原因或下一次重试；主窗口和托盘仍可用。
- 关闭主窗口进入托盘时，不关闭宠物。显式退出时先把宠物加入关闭顺序。

当前工作树的 `FillTrayMenu` 缺失，且构建已被该缺失阻断。该问题与 FUFU 研发无关，但必须先修复/对账后才能接菜单。

### 7.2 会话状态映射

插件通过 `IChatSession.Event` 订阅状态，不读取 VM 私有字段，也不从时间线文本猜状态。映射建议：

| 会话事实 | FUFU 状态 |
|---|---|
| 无会话、`Stopped`、尚未连接 | `Offline`/待机 |
| `Connected + Idle` | `Idle` |
| `ActivityChanged(Running)` | `Thinking` |
| `Compacting` 或 `Retrying` | `Thinking` 的特殊帧/忙碌帧 |
| `Cancelling` | `Cancelling` |
| `TurnSettled(Completed)` | 播放短暂 `Completed`，然后回 `Idle` |
| `TurnSettled(Failed)` 或 `SessionError` | 播放短暂 `Error`，然后按连接状态回 `Idle`/`Offline` |
| `TurnSettled(Interrupted)` | 可复用中性停止帧，不显示成失败 |

事件可能在后台线程触发。按项目既有约定使用 `Dispatcher.UIThread.Post(...)`；若要在 `Activity=Idle` 后读取终态，先排空 UI 队列，不能依赖事件到达时序。证据：[docs/decisions.md](../decisions.md:73)。

会话切换时先从旧会话退订，再订阅新会话；用会话对象和连接代次保护迟到事件，不能让旧会话的完成/错误帧覆盖当前会话。

### 7.3 偏好文件

建议插件独立保存版本化 JSON：

```text
<dataRoot>/plugin-data/pet.fufu/state.json
{
  "version": 1,
  "visible": true,
  "screenDisplayName": "DISPLAY1",
  "anchorX": 0.86,
  "anchorY": 0.82,
  "offsetDipX": 0,
  "offsetDipY": 0,
  "sizeDip": 128,
  "skin": "default",
  "interactionMode": "interactive"
}
```

`visible` 表示用户最后一次显式隐藏/显示决定；插件禁用时不删除它。位置写入应做原子替换、保留损坏文件，未知版本不能静默覆盖。`skin` 只保存插件拥有的皮肤 ID；未知皮肤回退默认并保留原值。

## 8. Windows 原生边界和回退

### 8.1 需要的 P/Invoke 或 WndProc 消息

优先用 Avalonia API。只有以下行为无法由 Avalonia 属性保证时才使用 Win32：

- `GetWindowLongPtrW` / `SetWindowLongPtrW`：读取和修改 `GWL_EXSTYLE`，设置 `WS_EX_TOOLWINDOW`、`WS_EX_NOACTIVATE`，检查 `WS_EX_LAYERED`。
- `SetWindowPos`：仅在需要原生调整置顶或无激活移动时使用；通常先用 Avalonia `Topmost`、`Position`。
- `GetDpiForWindow` 或 `WM_DPICHANGED`：核对窗口实际 DPI；Avalonia `RenderScaling`/`ScalingChanged` 仍是首选。
- `MonitorFromWindow` / `GetMonitorInfoW`：仅作为 Avalonia `Screens` 失败时的 Windows 回退。
- `WM_NCHITTEST`：返回 `HTCLIENT` 或 `HTTRANSPARENT`，实现混合命中。
- `WM_MOUSEACTIVATE`：返回 `MA_NOACTIVATE`，实现观赏态不抢焦点。
- `WM_DISPLAYCHANGE`、`WM_DPICHANGED`：触发位置重算。
- `WM_POWERBROADCAST`：休眠前保存、恢复后重新校正；若消息链不稳定，至少在 `Screens.Changed`/窗口显示时校正。

Win32 回调必须绑定到 `TryGetPlatformHandle()` 返回的当前 HWND，并在 `Closed`/插件卸载时解除。不能按裸 HWND 长期缓存；窗口重建后要重新绑定。

### 8.2 失败回退

| 失败 | 回退 |
|---|---|
| `ActualTransparencyLevel != Transparent` | 显示不透明占位卡并提示“当前桌面不支持透明宠物”，或隐藏宠物；不显示黑底伪透明。 |
| 无法取得平台句柄 | 禁用 Win32 命中扩展，整窗可交互；保留 Avalonia 拖动和隐藏。 |
| `WS_EX_NOACTIVATE` 造成点击/拖动异常 | 移除该样式，改为点击后记住并恢复主窗口焦点；仍保留“整窗可交互”。 |
| `HTTRANSPARENT` 跨进程行为不稳定 | 关闭混合穿透，改为整窗交互或显式“观赏/互动”切换。 |
| 显示器断开或位置越界 | 选主屏 `WorkingArea`，将宠物完全夹回可见区域。 |
| 插件卸载时窗口/位图仍存活 | 拒绝立即删除，报告“待关闭应用后清理”；不能假称卸载完成。 |
| 远程桌面、软件渲染或 DWM 组合异常 | 退回不透明静态图；动画也退化为静态。 |

Alt+Tab 隐藏优先验证 `WS_EX_TOOLWINDOW`；不要把 `ShowInTaskbar=false` 误认为同时保证 Alt+Tab 隐藏。

## 9. 分阶段实施计划

### P0：占位窗口、召唤、拖动和关闭

范围：

- 新建独立 `pet.fufu` 插件的最小包和状态文件。
- 创建透明、无边框、置顶、无任务栏、首次不激活的 `FufuPetWindow`。
- 托盘菜单接入“召唤 FUFU/隐藏 FUFU”。
- 一张原创占位 PNG 或简单矢量占位图。
- 左键拖动用 `BeginMoveDrag`；提供明确隐藏/关闭入口。
- 保存和恢复位置、可见性；无效位置回主屏工作区。
- 插件停用、主窗口显式退出、应用关闭均正确释放窗口。

验收：

- 从托盘第一次召唤成功，第二次不创建第二个窗口。
- 宠物透明区域没有黑/白大底；`ActualTransparencyLevel` 和失败原因可观察。
- 不出现在任务栏，Alt+Tab 行为符合设计决定。
- 召唤不激活主窗口以外的输入焦点；主窗口输入内容不丢失。
- 可以拖动；重启后位置恢复，显示器不存在时回主屏。
- 开启/关闭后台托盘模式、隐藏主窗、托盘退出各走正确生命周期。
- Windows 100%、125%、150% DPI，双显示器含负坐标布局至少各测一次。

P0 未验证项：混合点击穿透、透明像素命中、休眠恢复、远程桌面和软件渲染。

### P1：动画和会话状态

范围：

- `Idle/Thinking/Completed/Error/Cancelling/Offline` 状态图。
- 订阅 `IChatSession.Event`，统一封送到 UI 线程。
- PNG 状态切换和 `Animation`/`Transitions` 的轻量过渡。
- 状态动画在隐藏、停用、关闭和减少动画设置时停止/退化。
- 切会话时解绑旧会话，防止迟到事件污染当前宠物。

验收：

- Running、Compacting、Retrying、Cancelling、Completed、Failed、Interrupted 都能映射到可见状态。
- 完成/错误短动画后回到正确连接状态，不永久停在完成或错误。
- 长时间空闲 CPU 可接受，资源释放后没有计时器或事件订阅泄漏。
- 同一会话重启后旧代次事件不会改变新会话状态。

### P2：互动、穿透和皮肤

范围：

- `WM_NCHITTEST` 区域命中；观赏态/互动模式切换。
- 点击、右键、悬停、简单气泡和动作回调。
- 皮肤资源索引、皮肤回退和插件资源更新。
- 高 DPI 资源选择和更完整的休眠/显示器变化处理。

验收：

- 角色交互区可点击和拖动，非交互透明区不拦截底层窗口；失败时可切回整窗交互。
- 点击宠物不会意外把主窗口置前或丢失用户编辑焦点。
- 皮肤切换不改变位置/可见性，未知皮肤保留原值并回退默认。
- 禁用/卸载后窗口、计时器、订阅、程序集和资源都可确认释放。

## 10. 未验证项和实现前门禁

1. 未在真实 Windows Avalonia 桌面进程中验证 `TransparencyLevelHint=Transparent` 的最终分层窗口效果。
2. 未验证 `WS_EX_NOACTIVATE` 与 `BeginMoveDrag`、鼠标点击和触摸的组合行为。
3. 未验证 `HTTRANSPARENT` 对不同线程/不同进程窗口的跨进程穿透行为。
4. 未验证窗口动态加载程序集的 `avares://` 资源在卸载后是否完全释放；P0 应优先使用普通插件文件资源或做专门探针。
5. 当前仓库没有 FUFU 素材，也没有 Lottie/Spine/Rive 依赖；角色美术、许可证和最终体积未确定。
6. 当前工作树托盘 `FillTrayMenu` 缺失导致 App 编译失败；必须先对账托盘基线，再接入 FUFU 菜单。
7. `PluginPackageManager.UninstallPackage` 删除插件载荷目录；独立偏好目录和卸载保留策略尚未实现。
8. 系统休眠、显示器热插拔、远程桌面、软件渲染和 HDR/多 DPI 组合尚未验证。

建议下一步只做一个可丢弃的 P0 原型：使用真实 Windows 桌面启动 App，创建单色透明占位窗口，记录 HWND、`ActualTransparencyLevel`、`RenderScaling`、屏幕工作区、焦点变化和 Alt+Tab 结果；原型通过后再冻结 `pet.fufu` 契约和素材分发格式。
