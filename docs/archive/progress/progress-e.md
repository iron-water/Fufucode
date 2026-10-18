# E 轨进度（E1 内置浏览器）

> worktree：`C:\Users\21894\.codex\worktrees\track-e-browser\Fufucode`，分支 track-e。
> 任务书 `docs/archive/assignments/track-e-browser.md`。
> 所有权：只写 `src/Fufucode.App/Views/BrowserView*`（新文件）、`src/Fufucode.App/Infrastructure/Browser/`（新目录）+ csproj + `docs/archive/progress/progress-e.md`；右栏接线仅在 `WorkspacePanelView.axaml` 加 E-seam 一行 + 必要 xmlns。
> **不**触碰 `WorkspacePanelView.axaml.cs`、`MainWindow*`、`SelfTest*`（临时取证部分已全部回退，git status 不含其变更）。

## 一、验收

- `dotnet build src/Fufucode.App/Fufucode.App.csproj`：**0 警告 0 错误**。
- `dotnet run --project src/Fufucode.App -- --selftest`：**EXIT=0，354 项 OK / 0 FAIL**（完整 2 m45 s）。
  - 构成：非 E 基线 **321** = E1 入仓时的 283 + D5(Wave5-R4 Markdown)/I1 合入后新增的 38；E 轨 **33** = E1 的 29 + E2 入口新增的 4。
  - 注：中途一次构建出现 23 个 MSB3061（文件被上一次仍在跑的 Fufucode.App 进程锁定）是本地并发竞态，串行重跑后为 0 警告 0 错误。

## 一·补、E2 收口（协调者派单，2026-10-02）

| 项 | 状态 |
|---|---|
| 0 状态同步 | 已 `git merge main`（35 文件，含 D5 Wave5-R4 Markdown、I1 研究）；E1 的 13 个产物确认在仓 |
| 1 selftest 常驻 | ✅ `SelfTestRunner.cs` 已按第七节第 2 项一字插入（仅此一处），E1 的 29 项从取证转常驻 |
| 2 E 侧入口 | ✅ `BrowserView.TryPreviewFile(Control? panel, string? path)` + `x:Name="PreviewBrowser"` 契约 |
| 3 提交 | ✅ 提交到 `track-e`，未合并 main |

- E2 新增 4 项断言（均在 `Views/BrowserView.SelfTest.cs`）：面板为空/面板无浏览器时入口返回 false 且不抛异常；按 `x:Name` 契约可在**真实** `WorkspacePanelView` 内定位到内置浏览器；定位后路径确实进入文件预览模式。
- 发现并修复一处真实缺陷：`Control.FindControl<T>` 在没有 NameScope 的控件上抛 `InvalidOperationException` 而不是返回 null；`TryPreviewFile` 现在先判 `NameScope.GetNameScope(panel)` 为空再兜 `catch (InvalidOperationException)`，右栏收起/未初始化时一律安全返回 false。
- 截图证据：`tmp/e1/browser-skeleton-headless.png`（无头骨架：工具栏、地址栏、模式/状态栏；中间空区因无头没有真 Win32 句柄，WebView2 不会初始化）。
  - 来源标注：无头渲染骨架；非 WebView2 真实导航/预览渲染。
  - 真实导航态/文件预览态截图由用户在实机完成，列在下方『用户实测清单』。

## 二、NuGet 三项

| 项 | 值 |
|---|---|
| 包 | `Microsoft.Web.WebView2` |
| 版本 | `1.0.4258.31`（最新稳定版，2026-09；本机探测到的运行时 `154.0.4258.48` 与之匹配） |
| 许可证 | **BSD-3-Clause**（包内 `LICENSE.txt` 含 source/binary redistribution 与 disclaimer 三段） |
| 体积 | nupkg 9.4 MB（压缩比 ≈ 6：1）；解压 58 MB（含 native WebView2Loader.dll for x86/x64/arm64） |
| 协议依据 | 官方下载入口 `https://developer.microsoft.com/microsoft-edge/webview2/consumer/`（Microsoft Learn 文档记录的最终用户下载页；不臆造未核实的 `aka.ms`/`go.microsoft.com` 短链） |
| 目标框架 | 包内仅 `lib/net462`（托管程序集）+ `lib_manual/{netcoreapp3.0, net5.0-windows10.0.17763.0}`；本轨只用 Core，宿主通过 `CoreWebView2Controller` + 父窗口 HWND 直接使用，无需切换 TFM，仍是 `net10.0`。 |

### 0 警告消解

包的 `build/Microsoft.Web.WebView2.targets` 129-135 行会按 TFM 自动注入三份 `Reference`：
- `lib_manual\netcoreapp3.0\Microsoft.Web.WebView2.Core.dll` ✅ 保留
- `lib_manual\netcoreapp3.0\Microsoft.Web.WebView2.WinForms.dll` ❌ 移除（本轨不用）
- `lib_manual\net5.0-windows10.0.17763.0\Microsoft.Web.WebView2.Wpf.dll` ❌ 移除（本轨不用，会引发 MSB3277 WindowsBase 冲突）

在 `Fufucode.App.csproj` 加 `StripUnusedWebView2ManagedRefs` Target，于 `ResolveAssemblyReferences` 前精确按注入路径剥离两份 `Reference`（路径含 `buildTransitive\..\\lib_manual\…`，双反斜杠在 XML 项里直接写出）。若将来需要 WPF/WinForms 宿主控件，删除该 Target 即可恢复。

```xml
<Target Name="StripUnusedWebView2ManagedRefs" BeforeTargets="ResolveAssemblyReferences">
  <ItemGroup>
    <Reference Remove="$(NuGetPackageRoot)microsoft.web.webview2\$(WebView2Version)\buildTransitive\..\\lib_manual\net5.0-windows10.0.17763.0\Microsoft.Web.WebView2.Wpf.dll" />
    <Reference Remove="$(NuGetPackageRoot)microsoft.web.webview2\$(WebView2Version)\buildTransitive\..\\lib_manual\netcoreapp3.0\Microsoft.Web.WebView2.WinForms.dll" />
  </ItemGroup>
</Target>
```

## 三、运行时检测（不崩）

`Infrastructure/Browser/WebView2RuntimeGate.Probe()`：
- 调用 `CoreWebView2Environment.GetAvailableBrowserVersionString()`。
- 捕获 `WebView2RuntimeNotFoundException` / `DllNotFoundException` / `BadImageFormatException` / `FileNotFoundException` / `FileLoadException` → 归为 `Missing`。
- 捕获其他 → 归为 `Unknown`。
- **永不抛出**；VM 拿到 `BrowserRuntimeProbe(State, Version, Message)` 后，`Guidance` 给出可操作提示而不是错误码字符串。

返回结构：
```csharp
public sealed record BrowserRuntimeProbe(BrowserRuntimeState State, string? Version, string Message)
{
    public bool IsAvailable => State == BrowserRuntimeState.Available;
    public string Guidance => ...;  // "未检测到 WebView2 运行时…安装 Evergreen Runtime…"
}
```

宿主避免「探测必须先显示才能触发」的死锁：宿主永远可见，runtime 提示层覆盖在宿主之上（`HasRuntimeNotice = !IsAvailable && Message 非空`），避免初始 VM 把宿主永远隐藏。

## 四、安全边界

- 协议白名单 `http` / `https` / `file` / `about:blank`，其余（含 `javascript:`、`data:`、`ftp:`）一律 `Rejected`；地址栏主机端口 `localhost:3000` 不当作协议，补全为 `https://`。
- 本地路径解析：`Path.IsPathRooted` 路径**也会**校验 `File.Exists`，不存在的路径被拒绝（第一次 selftest 正是据此发现并修复）。
- 不注入脚本到第三方页面；`CoreWebView2.Settings.AreHostObjectsAllowed = false`；`NewWindowRequested` 拦截就地导航，单 tab 起步。
- 下载走系统默认下载文件夹（`%USERPROFILE%\Downloads`），文件名取自下载 URI 的 `LocalPath` 段并做非法字符清洗，失败则交回系统默认弹框。
- 浏览数据（缓存、cookie、历史）一律落 `%LocalAppData%\Fufucode\webview`（自测模式重定向到隔离临时根），不写项目目录、不写安装目录。

## 五、E-seam（最右侧接线）

`src/Fufucode.App/Views/WorkspacePanelView.axaml` 仅 5 行新增（git diff 见下）：

```
  xmlns:views="using:Fufucode.App.Views"   ← 必需
  <!-- E-seam：…  -->
  <views:BrowserView Grid.Row="2"
                     IsVisible="{Binding SelectedTab.PanelId,
                         Converter={x:Static vm:BrowserPreviewSeam.Instance}}"/>
```

不触碰该文件的任何其他区域、不触碰 `WorkspacePanelView.axaml.cs`、不触碰 `MainWindow*`。

`BrowserPreviewSeam`（`Fufucode.App.ViewModels` 命名空间，复用已有的 `vm:` 前缀）是只依赖 `PanelId` 字符串约定（`|preview` 后缀）的 `IValueConverter`；不引用 D 轨 `WorkspacePanelViewModel` 内部任何类型，最小合并冲突面。

> 一旦协调者按下列项 1 把 Preview tab 加入 `WorkspacePanelViewModel`（`PanelId` 末段为 `|preview`），E-seam 立即点亮；当前尚无 Preview tab，故 BrowserView 折叠。

## 六、变更清单（严格在 E 轨所有权内）

新增（11）：
```
src/Fufucode.App/Infrastructure/Browser/BrowserAddressResolver.cs
src/Fufucode.App/Infrastructure/Browser/BrowserFilePreview.cs
src/Fufucode.App/Infrastructure/Browser/BrowserUserDataLayout.cs
src/Fufucode.App/Infrastructure/Browser/WebView2RuntimeGate.cs
src/Fufucode.App/Views/BrowserView.axaml
src/Fufucode.App/Views/BrowserView.axaml.cs
src/Fufucode.App/Views/BrowserView.PreviewSeam.cs
src/Fufucode.App/Views/BrowserView.SelfTest.cs
src/Fufucode.App/Views/BrowserViewHost.cs
src/Fufucode.App/Views/BrowserViewModel.cs
docs/archive/progress/progress-e.md
```
修改（2）：
```
src/Fufucode.App/Fufucode.App.csproj                  +15 行（PackageReference + 消警告 Target + WebView2Version 属性）
src/Fufucode.App/Views/WorkspacePanelView.axaml       +5 行（xmlns + 注释 + BrowserView 宿主）
```

未触碰（含取证后已回退）：`WorkspacePanelView.axaml.cs`、`MainWindow*`、`SelfTest/SelfTestRunner.cs`、`Program.cs`。

## 七、需协调者合并处理

> 这部分改动 E 轨**不会**自提交，由协调者代验代提；E 轨提供的具体改动逐字如下。

### 1) 右栏 Preview tab 真实激活（D 轨）

`src/Fufucode.App/ViewModels/WorkspacePanelViewModel.cs`：
```csharp
// enum 增加
public enum WorkspacePanelKind { Files, Changes, Preview }

// TabViewModel.Title 增加分支
public string Title => Kind switch { Files => "Files", Changes => "Changes", Preview => "Preview" };

// RecreateTabs 内追加
Tabs.Add(new(identity + "|preview", WorkspacePanelKind.Preview));
SelectedTab ??= Tabs.First(x => x.Kind == WorkspacePanelKind.Files);

// 末尾追加命令
[RelayCommand] private void OpenPreview() => Open(WorkspacePanelKind.Preview);
```
`src/Fufucode.App/Views/WorkspacePanelView.axaml` 飞出 `+` 菜单里 Preview 按钮：把 `IsEnabled="False"` 删掉并加 `Command="{Binding OpenPreviewCommand}"`（同时去掉 `预览内容提供者待接入` 提示行）。这一行改动在原 D 轨代码区内，由 D 轨或协调者处理。

> 不做这步 → E-seam 永远折叠，Preview 不可见；E 轨无法单方面完成（D 轨 `WorkspacePanelKind` 是 sealed partial）。

### 2) E1 selftest 正式接线 ✅ 已由 E2 完成（无需协调者再动）

`src/Fufucode.App/SelfTest/SelfTestRunner.cs`，紧跟 `await RunWave5R3ChecksAsync();` 之后：
```csharp
        await RunWave5R3ChecksAsync();

        // E1 内置浏览器：地址归一化、文件预览分类、数据目录隔离、VM 状态机、控件骨架（无头可断言部分）
        await RunOnHeadlessUiThreadAsync(() =>
        {
            Fufucode.App.Views.BrowserViewSelfTest.Run(Pass, Fail);
            return Task.CompletedTask;
        });
```

### 3) Files tab 选中文件进入预览态 —— E 侧入口已就绪，D 侧调用点由 D 轨接

E 侧公开签名（全部位于 E 轨自有文件内）：

```csharp
// src/Fufucode.App/Views/BrowserView.axaml.cs
public const string PreviewControlName = "PreviewBrowser";

/// 面板未挂载 / Preview 未启用时返回 false 且不抛异常（右栏可整体收起，这是正常状态）。
public static bool BrowserView.TryPreviewFile(Control? panel, string? path);

// 同一文件：直接对已有实例推送
public void BrowserView.PreviewFile(string? path);

// src/Fufucode.App/Views/BrowserViewModel.cs：实例方法，供 D 侧持有 VM 时直接调用
public void BrowserViewModel.PreviewFile(string? path);
```

D 轨 `WorkspacePanelView.axaml.cs` 中的调用形态（`panel` 即 `WorkspacePanelView` 自身）：
```csharp
if (!BrowserView.TryPreviewFile(this, file.FullPath))
    /* 未挂载/未启用：按 D 侧既有习惯提示即可 */;
```
契约由 `WorkspacePanelView.axaml` 的 E-seam 行 `x:Name="PreviewBrowser"` 保证，已有断言用**真实** `WorkspacePanelView` 实例验证过（E2 四项之一）。
若 D 轨希望先切到 Preview tab 再预览，顺序由 D 侧决定（E 侧不改动 tab 选中状态）。

## 八、用户实测清单（E 轨无法用无头断言替代的部分）

> 这一节是 E 轨**无法**用 selftest 0/0 覆盖的项目；用户在实机开窗口逐项确认后回填结论。

1. [ ] 打开右栏 Preview tab（需上述协调项 1 完成）→ 工具栏/地址栏/状态栏出现。
2. [ ] 地址栏输入 `example.com` → 渲染 example.com，地址栏同步为 `https://example.com/`，「←」「→」按历史可用性启用/禁用。
4. [ ] 「文件…」选择 `.html` → 渲染该 HTML（含相对资源）；`.png`（<4 MB）→ 跟随系统主题底色内联预览；`.txt`/`.log` → 等宽字体 + 文本折行；含 `<script>...</script>` 的 `.txt` → 仅当字符串展示，不执行。
5. [ ] 故意输入 `javascript:alert(1)` → 状态栏「不支持 javascript 协议…」，当前页面不变。
6. [ ] 故意输入不存在路径 → 状态栏「找不到该本地文件。」，当前页面不变。
7. [ ] 关闭/卸载 WebView2 Evergreen Runtime 后启动 Fufucode → 浏览器区显示「未检测到 WebView2 运行时…」，附「打开下载页」「复制链接」按钮；点击「打开下载页」拉起系统浏览器。
8. [ ] 浏览过程中下载一个文件 → 落在 `%USERPROFILE%\Downloads`；项目目录无新增。
9. [ ] 检查 `%LocalAppData%\Fufucode\webview` 下确有缓存/cookie 目录；无 `..\` 逃逸到上一级。
10. [ ] 实机截图 `tmp/e1/browser-real.png`（自由导航态）与 `tmp/e1/browser-preview-html.png`（html 预览态）、`tmp/e1/browser-preview-text.png`（文本预览态）；标注"实机截图，2026-10-XX"。

## 九、诚信与回归说明

- 自测首次运行发现两处真实缺陷，均已修复（见五 / 「地址归一化」）：
  - `ToFile` 对绝对路径不校验 `File.Exists` → 现在统一校验；原本会把不存在的路径当 `File` 目标。
  - `javascript:`/`data:` 因无 `://` 漏判协议 → 现在按 `colon` 前后区分协议 vs 主机端口；dangerous 协议一律拒绝。
- 临时接线（`SelfTestRunner.cs`）已全部回退，git status 不出现。
- 资产/品牌：未复制任何 `assets/` 内容；工具栏用 `←→⟳■` 文本字符，不引入品牌素材。