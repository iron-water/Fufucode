# 轨道 D 进度

## ⛔ Wave5-R4 开工前阻塞上报（2026-10-02 09:28 -0700，交协调者处置）

**结论：本轮 R4 未开始，未改动任何一行代码。** 任务书阶段 0.2 规定 `git status --short` 必须干净，实际不干净，按铁律立即停止并上报，不进入阶段 0.5 及后续任何阶段。

`git status --short` 原文：

```
 M src/Fufucode.App/SelfTest/SettingsDestinationSelfTest.cs
 M src/Fufucode.App/Views/SettingsPage.axaml.cs
```

`git branch --show-current` = `track-d`；`git log --oneline -3`：

```
450a581 fix: 驱动器轨道正则放宽 A-G→A-I（I1 被静默过滤）
7592451 docs: E1/I1 任务书+入队——浏览器并行轨+多agent协作研究轨
d5ee899 docs: 立轨道 E（内置浏览器）与 I（多agent协作可视化研究）
```

### 取证

- 归属：**不是本轮 R4 执行者产生的**。HEAD 提交于 `08:44:14`，两文件 mtime 同为 `08:54:54`（同一秒内的一次成对编辑），本会话在 `09:27` 才开始；`docs/archive/progress/progress-d.md` 自身 mtime 为 `08:13`，早于这两次改动，说明改动者没有留下任何进度记录。当前无任何 `dotnet.exe` 进程，改动距今约 33 分钟，不像实时并发写入。
- 范围：两个文件都在 `src/Fufucode.App/` 内（D 所有权区），未越界，无 Core/Cli/runtime/scripts 污染。
- 内容（`git diff` 共 +3/-1）：
  1. `SettingsDestinationSelfTest.cs:75` — 给 `Context(store) with { ... }` 增补 `SetModel = (_, _, _) => Task.FromException<PiRpcResponse>(new InvalidOperationException("Running settings must not send RPC"))`，属**自测加固**：断言回合进行中改模型不得发 RPC。
  2. `SettingsPage.axaml.cs:54` — 在滚动复位块内新增 `_restoringScroll = false;`，属**滚动状态复位修复**（与同类 `_scrollGeneration++` 相邻）。
- 基线健康状况（本轮实测，仅诊断未改代码）：`dotnet build Fufucode.sln` → **0 个警告 / 0 个错误**；`dotnet run --project src/Fufucode.App -- --selftest` → **== 自测结束：0 项失败 ==**。即携带这两个改动时基线全绿，符合阶段 0.5 的起点要求。
- 注意：`track-e` worktree 当前也指向 `450a581`，与 `track-d` 同头，若这两个改动来自误入本 worktree 的其他轨执行者，请一并核对归属。

### 请协调者二选一后重新派发 R4

- **方案 A（推荐）**：把这两个改动确认为合法成果 → 代为 `git commit`（建议信息如 `test: 断言运行中改模型不发 RPC；fix: 设置页滚动复位标志`），工作区变干净后重新派发本任务书。
- **方案 B**：确认为需丢弃的残留 → 自行 `git checkout --` 这 2 个文件（本轮执行者按铁律不代为丢弃他人工作），再重新派发。

两方案之后基线预期均为全绿（已实测），R4 四个阶段（Markdown 行内渲染 / OS toast / 队列条编辑删除 / 实机自验）尚未开始，无「交 A 清单」条目。

> **已解除（2026-10-02 09:4x）**：协调者采纳方案 A，改动以 `137b57e` 收编并经 `af32c47` 合并，HEAD 现为 `fcb8c3c`。工作区复核干净，基线重测 build 0 警告 0 错误、`--selftest` 0 失败，R4 已开工。以下为本轮实际交付记录。

### 协调者处置（2026-10-02 21:5x，裁决 D5）

- **产物已代为提交并合并**：本轮四阶段改动由协调者提交为 `track-d` 的 **`d8481d6`**，并已合并入 `main`（合并前 build 0 警告 0 错误、selftest 0 失败；合并后复验由协调者进行）。D 轨执行者全程未执行 commit/merge/push，工作区处置权在协调者。
- **阶段0 争议已终结**：那两个晚到改动按**方案 A** 收编（见上条 `137b57e`），等待中的二选一裁决到此结束，不再挂起任何后续动作。
- **阶段2 OS toast 正式结案**：采纳路线 c 回退，判定为**本机环境限制**（`SHGetPropertyStoreFromParsingName` 对 `.lnk` 与 `.txt`、在本进程与独立 PowerShell 进程中均返回 `E_NOINTERFACE`），非实现问题；本轮及后续不再在此方向投入。取证与可复用结论已固化到 `docs/decisions.md`「[轨道D] Wave5-R4 阶段2：OS 系统通知 toast 结案」。
- **D-R4-1 单独立项为 D6，待排期**（本轮不修）：窗口宽 1024 逻辑像素且右栏以抽屉模式打开时，抽屉与 composer 及标题栏动作区重叠——Files 抽屉底部「复制路径」压在输入区「发送」上，"选择文件查看完整路径 / 0 项"与 composer 提示行交叠，标题栏「会话详情/工作区面板」图标右上角还多出抽屉的 pin/close 小标记；左右半屏两张截图表现一致（`tmp/w5r4/real-03-snapped-left.png`、`real-04-snapped-right.png`）。
  - **修复前需要两个设计判断（需 G/产品侧定调，D 不自行拍板）**：
    1. **抽屉对中央区是「覆盖」还是「挤压」**：现实现为覆盖（overlay），故压到 composer。若定为覆盖，抽屉必须显式让开 composer 与输入区高度（底边止于 composer 上沿）；若定为挤压，则半屏宽度下中央聊天列会被压到极窄，需要同时决定此时是否改为「全屏切换」（抽屉占满工作区、聊天区收起）。
    2. **抽屉动作徽章是否允许叠到标题栏图标**：现有 pin/close 小标记画在标题栏「会话详情/工作区面板」图标右上角，属两栏 chrome 在同一水平带上抢位。需定：徽章移入抽屉自身头部条，还是窄宽下隐藏标题栏那两个入口，还是允许叠加但改为不遮挡的排布。
  - 归属：`MainWindow.Workspace` 抽屉布局（D 所有权内），但上述两点未定前动手会返工，故挂 D6。
- **阶段3 边界已确认**：Core 无单条队列接口，界面与命令已就位、按钮对 Pi 持有项禁用并指向可用的「停止」路径；待 **A-R4-1** 落地后把预览项标记为本地持即可点亮，无需再改界面代码。详见 `docs/decisions.md`「[轨道D] Wave5-R4 阶段3」。
- **D 轨本轮结项**：三件收口事项（本段 + `docs/decisions.md` 两条）完成后原地待命，不开启新编码任务、不自行提交。

## Wave5-R4 阶段1：Markdown 行内渲染（完成）

**症状**：聊天消息正文原样直出，用户看到 `**粗体**` 的星号与 `` `代码` `` 的反引号本身。

**路线选择：b（自写轻量解析器，零新依赖）。** 理由：`dotnet add package` 会引入第三方依赖与体积，而聊天渲染只需行内四类 + 块级三类，用索引扫描即可覆盖；同时保持 `Run` 参与文本流，行内代码能随段落换行（若改用 Border/InlineUIContainer 包裹代码，长代码片段会变成不可断行的整块）。未引入任何 NuGet 包。

**实现**：新增 `src/Fufucode.App/Infrastructure/MarkdownTextBlock.cs`。
- 关键坑确认无误：`TextBlock.Inlines` 不能直接数据绑定，故用**附加属性** `anim:MarkdownTextBlock.Markdown`，在属性变更回调里重建 `Inlines`；XAML 侧不碰 Inlines。
- 解析器为纯索引扫描，无正则、无嵌套回溯；先取代码段（`...`）内容不再二次解析。
- 回退保护三层：`Parse` 抛异常 → 用单个 `Run` 还原原文；`Inlines` 集合本身不可用 → 退回 `TextBlock.Text`。任何情况下不丢消息内容。
- 颜色/字体全部走令牌（`AppMonoFont`/`AppCodeBackground`/`AppAccentText`/`AppTextSecondary`），零硬编码色值；游离元素取不到作用域资源时退回 `Application.Current` 资源根，并在 `ActualThemeVariantChanged`/`Loaded` 时重建，主题切换后颜色不滞留。
- 安全：Markdown 链接属模型输出的不可信输入，点击仅放行 `http/https` 绝对 URL（拒绝 `javascript:`/`data:`/`file:`），经 `UseShellExecute=true` 交系统默认浏览器。

**覆盖元素**：行内代码（等宽+浅底）、`**粗体**`、`*斜体*`/`_斜体_`（`snake_case` 不误判）、`[文字](url)`（可点击、下划线、accent 色）、`#`–`######` 标题（一级 20px 加粗，逐级递减）、`- `/`* `/`+ ` 列表（`• ` 前缀 + 全角空格近似层级缩进）、``` 围栏代码块（等宽多行、去围栏标记）、`\` 转义。未覆盖：表格、嵌套列表编号语义、引用块、删除线（按普通段落直出，不影响可读性）。

**接入点**：`Views/ChatPage.axaml` 两处消息正文——用户气泡（`UserMessageViewModel.Text`）与助手普通文本段（`PlainSegmentViewModel.Text`）。助手的 ``` 围栏仍由既有 `TextSegmenter` 切成代码卡片（带复制/折叠），渲染器不重复处理；用户气泡无分段器，故渲染器自带围栏分支。

**验证（实际运行）**：
- 新增 `SelfTest/MarkdownSelfTest.cs`，在 `SelfTestRunner.cs` 注册为 `RunWave5R4ChecksAsync()`（并加 `--r4-only` 快跑通道）。
- 全量：`dotnet build Fufucode.sln` → 0 警告 0 错误；`--selftest` → **0 项失败**（含既有 R2/R3 全部用例，无回归）。
- 断言覆盖：解析不抛异常、Inlines 19 段（>1）、行内代码带等宽 FontFamily 与背景、语法符号不再直出、内容无丢失、列表符号、标题 20px 且逐级递减、链接容器生成 + 协议白名单放行/拒绝、围栏去标记、snake_case 不误斜体、null/空、10 组畸形与截断输入 fuzz 全部不抛、附加属性写回生效、**真实 ChatPage 模板端到端**（经 `Timeline.LoadHistory` 注入，在视觉树中找到等宽 Run 且星号已消失）。
- 截图：`tmp/w5r4/markdown.png`、`markdown-light.png`、`markdown-dark.png`。另存 `tmp/w5r4/probe-codecrop.png`（4× 放大裁剪）作为像素取证——确认 Avalonia 12 的 `TextElement.Background` **确实被绘制**（代码段可见浅色底片），这是选择 Run 方案而非 Border 容器的依据。

**过程中修掉的两个真实缺陷**（首跑 selftest 抓到）：① 游离 TextBlock 作用域拿不到资源导致行内代码无背景 → 增加应用级资源根回退；② 标题断言最初用 `###` 样本（16px）却按一级标题断言 → 改为分别断言 `#`=20px 与层级递减，暴露的是测试写错而非实现错。

---

## Wave5-R4 阶段2：OS 系统通知 toast（受阻 → 已回退到 R3 现状）

**结论：本机（Windows 10.0.26300 / Dev 通道构建）无法投递未打包应用的系统 toast，已按路线 c 回退，页内通知保持 R3 现状不变。** 代码未留在树里（避免为不工作的功能改构建面）。

### 路线尝试与取证（全部实测，非推断）

1. **路线 a 的前提不成立**：任务书称 `Microsoft.Toolkit.Uwp.Notifications` 的 `ToastNotificationManagerCompat` 会在首次发 toast 时自动创建带 AUMID 的开始菜单快捷方式。实测 7.1.3 各 TFM 变体（net461 / netcoreapp3.1 / net5.0 / net5.0-windows10.0.17763）二进制里**不含任何** `Start Menu`/`IShellLink`/`.lnk` 字符串，只有 `Aumid`/`AumidAndComServer`/`AumidFromPackageManifest`；其 XML 文档里 `ToastNotificationManagerCompat` 只有 `CreateToastNotifier`/`History`/`Uninstall`/`WasCurrentProcessToastActivated`/`OnActivated`，快捷方式需自行创建；7.1.x 新增的 `DesktopNotificationManagerCompat.RegisterAumidAndComServer<T>` 走的是 COM LocalServer 注册（写注册表 + 需要 activator 类），面更大。**建议后续任务书修正这一条描述。**
2. 引包时 restore 报 **NU1904：传递依赖 `System.Drawing.Common` 4.7.0 存在严重漏洞 GHSA-rxg9-xrhp-64gj**（修复版本 ≥4.7.2/5.0.3）。我显式抬到 8.0.0 后警告清零，未采用抑制警告的做法。
3. **改走"TFM 升 `net10.0-windows10.0.19041.0` + 系统 WinRT 投影直发"**（可去掉该 NuGet 包及其漏洞传递依赖）。TFM 单改后 build 0 警告 0 错误、`--selftest` 0 失败，Avalonia 12.1.3 与该 TFM 兼容，`Windows.UI.Notifications`/`Windows.Data.Xml.Dom` 投影可用。
4. **AUMID 派生与快捷方式（路线 b 的 COM 部分）实测结果**：`IShellLinkW.SetPath/SetDescription/SetWorkingDirectory` + `IPersistFile.Save` 全部 `hr=0`，`.lnk` 落盘 1310 字节，`GetPath` 回读正确，字节里能搜到目标 exe 名——**建快捷方式这一步是通的**。
5. **卡死点：无法给 `.lnk` 写 `System.AppUserModel.ID`**。
   - 直接 QI ShellLink 对象取 `IPropertyStore` → `E_NOINTERFACE (0x80004002)`（ShellLink 不实现该接口，属预期）。
   - 改用文档要求的 `SHGetPropertyStoreFromParsingName` → 同样 `0x80004002`；`GPS_DEFAULT` 只读也失败；核对 `typeof(IPropertyStore).GUID` 与字面量 `886D8EEB-8CF2-4446-8D02-CDBA1DBDCF91` 完全一致，排除 IID 传错。
   - **对照组**：同一 API 作用在普通 `.txt` 上也返回 `0x80004002`；换到**独立的 PowerShell 普通进程**里（非无头 Avalonia 进程）对 `.txt` 与 `.lnk` 仍全部 `0x80004002`。→ 判定为**本机 shell 属性存储子系统不可用**，不是我的 interop 写错，也不是无头环境特有。
6. **最后一问：跳过快捷方式，只 `SetCurrentProcessExplicitAppUserModelID` 后直接发** → `ToastNotificationManager.CreateToastNotifier()` 抛 `COMException`（空消息，即无包标识/无注册 AUMID 的典型失败）。故本机连"不显示快捷方式也要能弹"的退路也没有。

### 已验证可复用的部分（受阻前测通，供未来重试）

- 与 R3 偏好联动的策略判定：`proactive` 同源复用（不另写一套），实测 6 个场景全部符合预期——总是/失焦×前台/不提醒/待审批×失焦与前台/总开关关闭。
- 偏好开关落盘与 **v1 旧文件兼容读取**（`SchemaVersion` 放宽到 1–2，缺字段按默认补齐），避免升级后设置页报"格式无法识别"。
- toast 内容构造：只含事件名 + 会话标题，实测断言"不含消息正文"，标题/会话名做 XML 转义；activation 参数 `fufucode-session=<id>` 与命令行回读解析。
- AUMID 按安装路径哈希派生（同路径稳定、不同安装互不覆盖）。
- 以上代码随回退一并移除，需要时可从本轮记录重建。

### 未探索选项（留给后续，勿在本轮继续硬试）

- 手工按 `.lnk` 二进制 Property Store 扩展块（`0xAFB0AF01` 签名）写入 AUMID，绕开坏掉的 shell 属性存储 API——但读取侧是否可用同样未经证实，工作量大。
- 给应用加 MSIX/稀疏包标识（轨道 F 范畴），从根上不需要 AUMID 快捷方式。
- 换一台正常 Windows 10/11 正式版构建重试本记录第 3–5 步，判断是否为本机 Insider 构建特有问题。

### 用户实测清单（阶段2）

- 若日后在别的机器上启用：安装版实机跑一次"完成/失败/待审批"三类通知，确认右下角弹出、内容只有事件名+会话标题、点击后回到对应会话。
- 当前无需实测：本轮未改动任何用户可见行为（设置页通知区仍是 R3 原文案"系统通知尚未启用"）。

---

## Wave5-R4 阶段3：Composer 队列条编辑/删除（部分完成——按 Core 能力边界收敛）

**结论：界面入口已做出并接上真实命令；但因 Core 无单条队列接口，对已进入 Pi 队列的条目一律禁用 + tooltip 说明。未改动 Core 一行。**

### 能力边界取证（只读理解，未修改）

- `IChatSession` 只暴露 `QueuedCount`，没有任何队列变更方法（`Abstractions/IChatSession.cs`）。
- `ChatSessionCoordinator` 的 `_queuedTexts` 是私有状态：入队只在 `SendAsync` 时 `Add`（:256），出队只发生在**投递时**按文本 `IndexOf→RemoveAt`（:477-478）；唯一的显式清空是 `clear_queue` RPC（:359），且只在「停止」路径调用，会连同当前回合一起中止，并把清出的文本放进 `_recoverableQueue` 回灌草稿。
- `InputViewModel.SyncQueue`（:252）对 `QueueChanged` 载荷做**整体 Clear + 重填**，即界面侧 `QueuedTexts` 永远是 Core 的镜像。

推论：任何"本地移除"都会在下一条 `QueueChanged` 被 Core 权威列表覆盖，被删的条目会重新冒出来。这正是任务书预警的脱节场景，实测已把它写成断言锁住（见下）。

### 实际做法

- `QueuedItemViewModel` 增加 `IsLocallyOwned` / `CanEdit` / `CanDelete` / `BlockedReason`；预览项一律由 Core 镜像生成，故当前恒为非本地持有。
- `InputViewModel` 增加 `EditQueuedCommand`（回填输入框 + 移除该条 + 抢焦点）与 `DeleteQueuedCommand`（仅移除该条），两者对非本地持有项**直接不执行**；已有草稿时编辑走追加而非覆盖，沿用 R3「不覆盖用户已输入内容」的纪律。
- `ChatPage.axaml` 队列条每行加「编辑」「删除」两个 `Classes="subtle"` 小按钮（文字入口，未引入图标资源），禁用态绑 `CanEdit/CanDelete`，tooltip 绑 `BlockedReason`。
- tooltip 文案不只说"不行"，还指向**今天真正可用的路径**：按「停止」整列清空并退回输入框再编辑。避免把用户引向死胡同。

### 顺带修掉的一个真实缺陷

`HasQueue`/`QueueSummary`/溢出提示此前只靠 `SyncQueue` 末尾手动 `OnPropertyChanged`，任何其它对 `QueuedTexts` 的改动都不会让队列条展开——自测在真实 ChatPage 上找按钮时拿到 0 个才暴露。改为在构造时订阅 `QueuedTexts.CollectionChanged → RaiseQueueCounts()`，派生属性不再依赖"每个改动点都记得通知"。

### 自测抓到并修掉的第二个问题：空集假通过

`buttons.All(b => !b.IsEnabled)` 在 `buttons` 为空时返回 true，导致"按钮禁用""tooltip 正确"两条在按钮根本没渲染的情况下仍然 ok。已在这些断言前显式要求 `buttons.Count == 2`。（同类风险检查过 `MarkdownSelfTest`：其用的是 `Any()`，空集会正确失败。）

### 验证（实际运行）

- `SelfTest/QueueStripSelfTest.cs`（已注册进 `RunWave5R4ChecksAsync`）15 项断言全绿，覆盖：三条 `QueueChanged` 镜像、Core 持有项禁用、tooltip 含原因与「停止」指引、**对 Core 持有项执行命令不改动队列也不污染草稿**、本地项编辑=回填+移除、本地项删除=计数与顺序正确、删除不回填、已有草稿时追加、**下一次 QueueChanged 按 Core 列表整体覆盖镜像**（把"不能伪删"锁成回归断言）、真实 ChatPage 渲染出两个按钮且为禁用态并带 tooltip。
- 截图 `tmp/w5r4/queue-strip.png`：徽章 + 文本 + 灰化「编辑」「删除」，布局无溢出。
- 全量：`dotnet build Fufucode.sln` 0 警告 0 错误；`--selftest` 0 项失败（R2/R3 及本轮 Markdown 用例均无回归）。

### 交 A 清单（阶段3）

- **A-R4-1：需要 Core 提供队列单条操作接口**。当前 `IChatSession` 无队列变更能力，`clear_queue` 只能整列清空且绑定在「停止」语义上。建议最小接口：`Task<bool> TryRemoveQueuedAsync(string text, int occurrenceIndex)` 与（可选）`TryReplaceQueuedAsync(...)`，语义要求：仅作用于**尚未投递**的条目，命中失败返回 false；成功后必须广播 `QueueChanged` 让界面镜像收敛。有了它，D 侧只需把预览项标记为可操作（`IsLocallyOwned=true`）即可点亮按钮，界面代码已就位。
- 佐证材料：`InputViewModel.SyncQueue` 的整体覆盖行为 + `ChatSessionCoordinator:256/477/478/359` 的行号。

---

## Wave5-R4 阶段4：实机自验（部分完成，发现 1 个真实布局缺陷）

工具：`cua-driver 0.28.2`（守护已在跑）。全程遵守 `docs/archive/coordination/cua-dispatch.md` 规则 8：被测应用用 `launch_app` 隐藏启动（返回 `active:false`，未抢前台）、信息获取一律走 `get_window_state` 后台 UIA + 窗口截图、点击用 `delivery_mode:background` 的 accessibility 路由。**未使用 bring_to_front。** 收尾用应用自身「关闭」按钮正常退出（`kill_app` 因无法自证为驱动启动而拒绝，属预期），退出后 `list_windows(pid)` 为空、进程已消失。

### 环境实测

- 显示器 2560×1600 物理像素，**系统缩放 scale_factor = 1.25（125%）**。
- 窗口逻辑尺寸 1802×1135，回传截图 1568×987（驱动按 0.870 比例下采样）——所有像素动作均按该比例换算，换算正确性由①的实测结果反证。

### ① 真实指针拖动右栏分割柄 —— 通过（量化取证）

`drag` 从分割柄拖到左移约 150 逻辑像素。UIA 前后对比：

| 元素 | 拖前 | 拖后 |
|---|---|---|
| FilesList x / w | 1428 / 439 | 1279 / **587** |
| TimelineScroller w | 948 | **798** |
| InputBox w | 876 | **726** |

右栏按预期变宽、聊天区与输入框同步收窄，无元素消失。截图 `real-01-panel-open.png` → `real-02-after-drag.png`。

### ② 半屏（等价 Win+左 / Win+右 几何）—— 布局未崩，但发现缺陷 D-R4-1

用 `set_window_frame`（`effect:confirmed` + `value_readback`）把窗口设为 1024×1280 的左半屏与右半屏（逻辑半屏 = 2048/2）。UIA 检查：**无任何退化元素（w/h ≤ 0）**，侧栏自动收起为图标轨（TimelineScroller 起点 x=66），右栏按 R2 既有规则切为抽屉模式。

**但截图目检发现真实缺陷（见下）**，因此本项判定为"布局未崩但存在重叠缺陷"，不能报"不炸"。

> **D-R4-1（应修，新发现）**：窗口宽 1024 逻辑像素且右栏以抽屉模式打开时，**抽屉与 composer 及标题栏动作区重叠**——Files 抽屉底部的「复制路径」按钮直接压在输入区「发送」按钮上，"选择文件查看完整路径 / 0 项"与 composer 提示行交叠；标题栏「会话详情 / 工作区面板」图标右上角还多出抽屉的 pin/close 小标记。左右半屏两张截图表现完全一致（同宽同态）。
> 证据：`tmp/w5r4/real-03-snapped-left.png`、`tmp/w5r4/real-04-snapped-right.png`。
> 归属：D 的 `MainWindow.Workspace` 抽屉布局（右栏抽屉的纵向范围应止于 composer 上沿，且不应把动作徽章叠到标题栏图标上）。本轮未修——阶段 4 是验收环节，且修复需要独立的设计判断（抽屉是覆盖还是挤压），留待协调者排期。

### ③ 150% 缩放 —— 未做，转用户实测

改系统缩放会影响用户整个桌面会话，属"影响共享状态"的动作，未执行。已实测确认本机为 **125%**，且应用在该非 100% 缩放下真实运行正常（①②全部测量均在 125% 下取得，本身即非平凡 DPI 证据）。150% 需用户按下方清单自查。

### ④ 明暗主题 —— 无头渲染已覆盖，应用内切换转用户实测

本轮未在被测实例里切主题：设置页主题控件需多级 UIA 交互（进设置→外观→ComboBox 选择），成本高于价值，且离开设置页会触发"丢弃草稿"确认路径，可能干扰后续测量。主题证据改用阶段 1 的真实控件无头渲染：`tmp/w5r4/markdown-light.png`、`markdown-dark.png`（同一套消息模板在两个主题下的完整渲染，含代码底色与链接色随主题切换）。应用内开关仍列入用户实测清单。

### 阶段 4 截图清单（tmp/w5r4/）

`real-01-panel-open.png`、`real-02-after-drag.png`、`real-03-snapped-left.png`、`real-04-snapped-right.png`（实机）；`markdown.png`、`markdown-light.png`、`markdown-dark.png`、`queue-strip.png`、`probe-codecrop.png`（无头取证）。

### 用户实测清单（本轮做不到的项）

1. **150% 缩放布局**：设置 → 系统 → 屏幕 → 缩放 改为 150%，打开 Fufucode，检查五区是否重叠、字号是否仍为 14px 正文、右栏分割柄能否拖动。
2. **应用内明暗主题切换**：设置 → 外观 → 主题，切到浅色再切回深色，确认聊天区代码底色/链接色随主题变化、无残留旧色。
3. **真实中文输入法**：用微软拼音/Rime 在输入框组合"你好"，组合态按 Enter 不应发送；上屏后再按 Enter 才发送。
4. **真 Win+左 / Win+Right 磁贴**（非本次的几何等价）：按 Win+左、Win+右各一次，重点验证 D-R4-1 的重叠是否同样出现，以及 Win+↑ 最大化后抽屉是否恢复正常。
5. **D-R4-1 复现**：把窗口手动拖到约半屏宽，打开右栏 Files，观察「复制路径」是否压住「发送」。

---

## Wave5-R4 总结

| 阶段 | 状态 | 说明 |
|---|---|---|
| 1 Markdown 行内渲染 | **完成** | 路线 b 自写零依赖解析器；行内四类 + 块级三类全覆盖；三层回退保护；真实 ChatPage 端到端断言 + 像素取证；`--selftest` 全绿 |
| 2 OS 系统 toast | **受阻（路线 c）** | 本机 shell 属性存储子系统对 `SHGetPropertyStoreFromParsingName` 一律返回 `E_NOINTERFACE`（.lnk 与普通 .txt、无头进程与独立 PowerShell 进程四种组合一致），无法给快捷方式写 AUMID；跳过快捷方式直接 `CreateToastNotifier()` 抛 `COMException`。已完整回退，页内通知保持 R3 现状 |
| 3 队列条编辑/删除 | **部分完成** | 界面入口 + 真实命令已就位；Core 无单条队列接口，故对 Pi 已持有项一律禁用 + tooltip 指向今天可用的「停止」路径；未改 Core 一行 |
| 4 实机自验 | **部分完成** | ①拖拽量化通过；②半屏无退化元素但发现缺陷 D-R4-1；③④转用户实测（理由见该节） |

### 交 A 清单

- **A-R4-1**：需要 Core 提供队列单条操作接口（`TryRemoveQueuedAsync(text, occurrenceIndex)`，仅作用于未投递项、命中失败返回 false、成功后广播 `QueueChanged`）。D 侧界面与命令已就绪，接口到位后把预览项标记为本地持有即可点亮。详见"阶段3"节。
- 阶段 2 若由 A/协调者接手：未打包应用的 AUMID 写入依赖 shell 属性存储，本机不可用；根治方向是给应用加 MSIX/稀疏包标识（属轨道 F），或换正常 Windows 正式版构建复测。

### 用户实测清单汇总

见"阶段4"节的 5 项（150% 缩放、应用内主题切换、真实中文输入法组合、真 Win+左/右磁贴、D-R4-1 复现）。

### 本轮改动面（收尾三连已核对）

`dotnet build Fufucode.sln` → 0 警告 0 错误；`--selftest` → 0 项失败；改动仅落在 `src/Fufucode.App/`（新增 `Infrastructure/MarkdownTextBlock.cs`、`SelfTest/MarkdownSelfTest.cs`、`SelfTest/QueueStripSelfTest.cs`；修改 `Views/ChatPage.axaml`、`ViewModels/InputViewModel.cs`、`SelfTest/SelfTestRunner.cs`）与 `docs/archive/progress/progress-d.md`。未 commit。

---

## Wave 2：发布候选走查（docs/archive/assignments/track-d-m6m7.md『Wave 2』）

**状态：已合并 main（`7cd7670`）**——阻塞项（D 所有权内）已修复，合并前终验全绿；F-1～F-4 已交协调者派 F。 走查对象：协调者安装的包 `%LocalAppData%\Programs\Fufucode`（build `9862608`，含 Wave 1）；打包缺陷挡住的段落改用本 worktree 源码构建走完。提交：`a88aa10`、`be6fbf6`、`509e8a0`、`31335e6`、`117cc72`、`f4d82e2`（全部限于 `src/Fufucode.App/` 与本文件）。

### 走查结果（安装→信任→对话→审批→停止→重启恢复→扩展启停→设置变更）
- 安装校验：`integrity.sha256.json` 15283/15283 文件哈希一致；打包版 `--selftest` 0 失败（`tmp/w2/00-integrity.txt`、`00-selftest.txt`）。
- **打包版**：信任弹窗、中文+空格路径、扩展页（添加/启用/安全重启/命令出现）、设置页主题持久化均正常；**对话/审批/停止/排队/ui-tour 全部无法进行**——包内缺审批桥（见 F-1），发送按钮禁用、页头“审批不可用”（`tmp/w2/05-uia.txt`、`diag.txt`、`00-realpi-exe.txt`）。
- **源码构建（修复后）**：选择项目 → 对话 → 审批拒绝（`rm out.txt` 被拦，文件仍在）→ 停止显示“已中断”后可继续 → 运行中排队+插入（徽章正确）→ 关窗重开自动回到最近项目、历史/工具卡片/中断标记恢复 → 侧栏跨项目切换往返不串线 → demo 扩展启用+重启+`/demo.ui-tour` 执行（`tmp/w2/26`–`47-*.txt`）。设置页四块经 UIA 核对（`s2-settings.txt`、`s5-keymode.txt`）。走查全程桌面多次锁屏，证据以 UIA 文本为主，截图大多无效。

### 问题分档
| # | 档 | 问题 | 处置 |
|---|---|---|---|
| D-1 | 阻塞 | App 无项目选择：开始菜单/桌面快捷方式 WorkingDirectory=安装目录，Pi 以**安装目录**为项目（计划 M3.1 未落到 UI） | 已修 `a88aa10`：无 `--project` 时回退最近项目，否则“选择项目”态；侧栏项目 Flyout 运行中切换（先开后放、回合中拒绝、失败保留旧会话）；最近项目存全局偏好 `app.recentProjects`（最近在前、上限 10）；`be6fbf6`：启动失败/未信任态可“打开其他项目…” |
| D-2 | 阻塞 | 全新安装无 models.json 时 App 内无法配置任何模型（设置页控件仍为“后端待交付”，而 A 的链路已在 main） | 已修 `509e8a0`/`31335e6`/`117cc72`/`f4d82e2`：接通模型/思考/代理/自定义端点（密钥写 Windows 凭据管理器或用已有环境变量；解析与 Core 一致；设置单一存储 `host.Manager.Settings`，避免两个 `AppSettingsStore` 实例互相覆盖）；realpi 以隔离数据根模拟全新用户：配端点→重启→真实回复 |
| D-3 | 应修 | 扩展行“移除”紧贴启用开关，一点即删 | 已修 `a88aa10`：拉开间距 + 确认 Flyout |
| D-4 | 应修 | 切换会话/项目时旧草稿防抖 Tick 可能写进新会话 | 已修 `a88aa10`：切换前停旧 timer |
| D-5 | 应修 | 设置页模型下拉进入时为空，需手点刷新 | 已修 `f4d82e2`：进入自动拉取，回填不触发 set_model/写设置 |
| D-6 | 记录 | Pi 进程在一次 write 工具完成后 0.2s 以 `0xC0000409` 退出（`tmp/w2/27-approval.txt`；会话日志 write 调用 08:15:33Z→结果 08:15:39Z→进程退出 08:15:39.46Z）；重开后恢复正常，此后未复现 | 交 A 调查 Node/Pi fail-fast。该次 write 的审批来源未能取证：桥代码对 write 必须 `ctx.ui.confirm` 返回 true 才执行（`policy.ts` confirm 名单含 write，无配置覆盖），App 产品路径只有“允许”按钮能产生 true（`AutoResponder` 仅自测设置），自动化在该时段无任何输入；共享桌面上的人工点击无法排除。建议：App 诊断日志记录扩展 UI 请求/应答（方法、id、应答来源），便于此类审计——列入后续 |
| D-7 | 记录 | 系统文件夹对话框在自动化操作失败后自行关闭并返回了当前选中项（`tmp/w2/22-after-pick.txt`） | 系统对话框行为，非 App 缺陷 |
| D-8 | 记录 | 不支持思考的模型（Atria-Dawn-Preview `reasoning=false`）上 `set_thinking_level` 被 Pi 静默钳到 off，RPC 仍返回成功，UI 无法区分 | 需 Core 暴露 `get_available_thinking_levels`（交 A） |

### 交 F 的发布验证问题单
1. **F-1（阻塞）包内缺 `extensions/fufucode-bridge`**：`scripts/packaging/package.ps1` 无复制扩展步骤；Core 按 `runtime/node/../../extensions/fufucode-bridge` 查找（`PersistentSessionManager` 构造）。后果：审批桥永不注册 → 发送禁用、所有 prompt 与扩展命令不可用、打包版 realpi 全段失败。该目录只有 `index.ts`/`policy.ts`/`package.json`，无依赖，整目录复制并纳入 `integrity.sha256.json` 即可；复测请以打包版跑 `Fufucode.App.exe --selftest --realpi`。
2. **F-2（应修）`Fufucode.cmd --selftest --realpi` 丢参**：`--selftest` 分支只转发 `--selftest`，打包版 realpi 无法经启动器运行。
3. **F-3（阻塞，F 已知）`out/Fufucode-0.9.0-setup.exe` 损坏**：缺 Inno SetupLdr 偏移表与版本资源，运行即“The setup files are corrupted”；`1.0.0-setup.exe` 结构完整（仅核对了偏移表与大小，未做 CRC）。发布前请重编并做安装/卸载演练。
4. **F-4（建议）快捷方式 WorkingDirectory**：D-1 修复后不再依赖工作目录，但建议 `[Icons]` 显式 `WorkingDir: "{userdocs}"`，避免任何相对路径落到安装目录。
5. 复测清单：F-1 修后以安装版走一遍“选择项目→对话→审批允许/拒绝→停止→重开恢复→ui-tour”。

### 给用户的已知限制
- 首次启动需选择项目文件夹；之后自动打开最近项目，侧栏项目名处可切换。
- 模型/思考等级：空闲时立即作用于当前会话并保存；回合进行中修改只保存、用于新建会话。部分模型不支持思考等级，会被 Pi 静默按 off 处理。
- 代理与自定义端点需“立即重启”会话后生效；API Key 保存在 Windows 凭据管理器，App 不提供删除（可在“凭据管理器”中手动删除）。
- 扩展启停需安全重启会话生效，不支持热加载。
- 未在交互式桌面验证：Rime/微软拼音实际输入、Mica 观感、窗口动画；Markdown 行内格式（行内代码/粗体/列表）尚未渲染；标题栏为系统标准样式。
- 数据位于 `%LocalAppData%\Fufucode`；安装版与源码构建共用该目录。

### 验证（实际运行）
- 修复后终验（`f4d82e2`）：build 0 错误 0 警告；`--selftest` 152 ok / 0 失败（`tmp/w2/final3-selftest.txt`）；`--selftest --realpi` 212 ok / 0 失败（`tmp/w2/final3-realpi-2.txt`；第 1 轮 1 项因 Atria `Request timed out.` 失败，按约定重跑，断言未改，`final3-realpi.txt`）。
- **合并前终验（`f6d8dd6` = track-d 合并 main `0c97202`，无冲突，main 侧未触及 App）**：build 0 错误 0 警告；`--selftest` 152 ok / 0 失败（`tmp/w2/merge-selftest.txt`）；`--selftest --realpi` 212 ok / 0 失败，一轮全绿（`tmp/w2/merge-realpi.txt`）；Core 测试 159 PASS / 0 FAIL / 1 SKIP（SKIP 为 Atria Core 验收用例按 `FUFUCODE_ATRIA_REAL_MODEL_TEST` 门控，`tmp/w2/merge-core-tests.txt`）。
- **已合并 main `7cd7670`**（`git merge --no-ff track-d`，无冲突）：协调者确认在 main 工作区存在 A 的未提交改动（CLI 第二切片：`src/Fufucode.Cli/*`、`src/Fufucode.Core/*` 若干、测试与文档）时仍合并——track-d 只改 `src/Fufucode.App/` 与本文件，与之无路径重叠，A 的未提交改动未受影响。合并后 `src/` 与终验树 `d4c1e29` 完全一致；终验针对的是已提交 Core，**与 A 未提交的 Core 改动的组合未经测试**，待 A 提交后由其验证组覆盖。
- 密钥框掩码以源码 `PasswordChar="●"` 为证（截图时桌面锁屏，未取得画面证据）。

---

# Wave 1（M6 设置页 + 交互收尾，docs/archive/assignments/track-d-m6m7.md）

**状态：已合并 main**——协调者授权 D 自行合并（D 行为 manual，驱动器跳过；A 不在 main 工作区、无驱动器锁）。先在 track-d `git merge main`（`f038bef`，无冲突，带入 A/F 的 Core/packaging 改动，未触及 `src/Fufucode.App/`）→ 合并提交 `dda0457` 上跑完整终验全绿 → main 工作区 `git merge --no-ff track-d`。Wave 2 待协调者置 pending。

开工 `git merge main`（`eaf5134`，含 G 资产、B 验收扩展集、调度文档）。全部改动限于 `src/Fufucode.App/`。提交：W1-a `6c23e28`、W1-b `115d2d4`。

## 完成
- **设置页**（新导航项）：SettingsCard 式四组。
  - 外观：主题 系统/亮/暗，经 A 的 `AppSettingsStore` 落盘并即时切换；启动时按设置应用；新版本设置文件拒写时卡片内报错、不改主题。
  - 模型与思考、代理与自定义端点：**后端待交付**——A 的设置应用链路（`set_model`/`set_thinking_level`、代理注入、models.json 写入规则）不在 main，控件禁用并标注；不写 `SelectedModel/ThinkingLevel`，App 不发这些 RPC。只读展示真实数据：当前模型、`get_available_models` 列表、`<pi-agent>/models.json` 的 provider/baseUrl/模型；apiKey 只显示“环境变量 X（已设置/未设置）”或“字面密钥（已隐藏）”。
  - 关于：G 图标、应用版本（短哈希）、.NET/Avalonia/Pi/Node 版本（读 package.json 与 node.exe 文件版本，不启动进程）、数据/配置/日志目录打开；“复制诊断摘要”（不含任何密钥或环境变量值，自测断言）；完整诊断包导出待 F/A。
  - 本轮无需重启才生效的设置，未造 InfoBar。
- **G 资产接线**：窗口与 exe 图标、侧栏标识、对话/扩展/中断空态插画（csproj `Link` 引用 `assets/`，未复制）。
- **P2-17 插入/排队**：运行中 Enter=排队（followUp）、Ctrl+Enter 或“插入”按钮=steer（当前步骤工具调用结束后、下一次模型调用前送达），同一 IME 守卫；扩展命令禁用插入（steer 不接受扩展命令）；排队条逐项标“插入/排队”。冻结契约 `IChatSession.SendAsync` 无投递参数，真实路径经公开的 `Coordinator.SendPromptAsync(Steer)`，App 内 `IPromptSteering` 承载。P2-14（重复 using）已不存在；P2-1/P2-18 此前已修；App 所有权内审查 P2 清零。
- **视觉**：页头活动状态改 chip；设置卡片布局统一。
- **标题栏整合尝试后撤回**：`WindowDecorations.Full + ExtendClientAreaToDecorationsHint` 在真实桌面上系统标题文字与侧栏标识重叠、最大化时顶部出现黑带（`tmp/w1-shots/` 真实桌面截图取证），恢复标准系统标题栏。

## 验证（实际运行）
- build 0 错误 0 警告；`--selftest` 127 ok / 0 失败（96→127）。
- realpi：设置/版本探测（Pi=0.87.1、Node=24.15.0）通过；P2-17 真实 steer：`tmp/w1b-realpi-r1.txt`、`-r2.txt` 全绿（steer 被接受、徽章正确、回合 Completed、回复含 STEER-OK）；`-r3.txt` Atria 429 限流，模型段 SKIP。更早一轮出现一次 steer 后 `outcome=Failed`，当时未保存输出；此后加了失败诊断，5 次真实 steer 未见 steer 相关错误，判断为端点限流但**未取得该次原文**。
- **Wave 1 终验（`0865d62`）**：build 0/0；`--selftest` 127 ok / 0 失败 / 0 SKIP（`tmp/w1-final-selftest.txt`）；`--selftest --realpi` 0 失败 / 0 SKIP，Atria 一次全绿，含 P2-17 steer 四项与 M5 扩展段（`tmp/w1-final-realpi.txt`）；Core 测试 150 PASS / 0 FAIL（`tmp/w1-final-core-tests.txt`）；`.cs` 硬编码色零命中。
- **合并前终验（`dda0457` = track-d 合并 main `f038bef` 后）**：build 0 错误 0 警告；`--selftest` 127 ok / 0 失败 / 0 SKIP（`tmp/merge-selftest.txt`）；`--selftest --realpi` 0 失败 / 0 SKIP，Atria 一次全绿，含 M5 扩展段与 P2-17 steer 四项（`tmp/merge-realpi.txt`）；Core 测试 156 PASS / 0 FAIL（A 新增 6 例，`tmp/merge-core-tests.txt`）。本次带入的 A 设置/诊断 Core 改动是否已满足设置页“后端待交付”项，未在本轮评估，留 Wave 2 核对。
- 截图：`tmp/w1-shots/`（设置页 light/dark 两屏、对话空态、扩展 mock 空态、运行中 composer light/dark、真实桌面普通/最大化）。

## 未验证 / 后续清单（交协调者排期）
1. **自绘标题栏**：按 UniGetUI 路线（`BorderOnly` + 自绘 caption 按钮 + WndProc 规避 Avalonia 12 #21160/#21212、WM_NCCALCSIZE/WM_NCHITTEST Snap Layouts）单独立项。
2. **Markdown 行内渲染**：时间线正文的行内代码仍显示原始反引号，粗体/标题/列表未渲染——日常观感差距最大的一项。
3. 设置页“后端待交付”项：A 交付设置应用链路/代理注入/端点写入后接线。
4. 交互式桌面：Rime/微软拼音实际输入、Mica 实际透出、弹窗人工点击、失焦态。
5. 侧栏会话：无搜索、无按日期分组、无删除/右键菜单。
6. 空态在“运行中但时间线为空”时仍显示（仅自测夹具可见，真实流程不会出现，低优先）。

---

# 附：M5 插件管理页 + 视觉升级（已验收合并 `b602c87`）

任务书：`docs/archive/assignments/track-d-m5.md`（高自主版）。开工 `git merge main` 至 `7b452d6`；H2 后再次合并 main（`d2d0bcc`，A 的 M6 Core 基础）。

## 本次完成（2026-09-29）

全部改动限于 `src/Fufucode.App/`。提交：H1 `71e26f7`、H2 `9e5d37d`、H3 `4d68f99`、H4（见 git log）。H3 后合并 main 带入 A 的 `5df7279`（用户扩展加载接口 + F05 Core 修复）。

### H1 反馈快修
- **F06 回车**：根因有二——`AcceptsReturn=True` 的 TextBox 自己消费 Enter，XAML 上的 bubble `KeyDown` 永远收不到；`ImeEnterGuard` 用“文本形状变化”猜组合态，普通中间插入也会被当成组合而吞 Enter。改为 Tunnel 挂接 `KeyDownEvent`，守卫读 `TextPresenter.PreeditText`（非空=组合中）+ 组合结束后 80ms 窗口吞掉提交键，删除形状启发式。自测覆盖：preedit 中 Enter 不发送、Shift+Enter 换行、中间插入后 Enter 发送、无头窗口真实 Enter 路由。
- **F05 中性已中断**：停止窗口（Cancelling→`TurnSettled`/连接关闭）内的 Unknown/UserCancelled 错误统一呈现为灰色“已中断”卡。偏离原建议（“下一次 Running 即关窗”）：真实 Atria 实测序列为 Cancelling→瞬时 Running→`Unknown: Request aborted`→Idle→TurnSettled，按 Running 关窗会漏出红卡。
- **F04 排队指示**：运行中发送按钮变“排队”，输入区说明“发送的内容会在本回合结束后执行”，composer 顶部显示排队条数与前 3 条预览（溢出计数），数据来自 `QueueChanged`。
- **F02**：`Bitmap.Save` 改 `PngBitmapEncoderOptions.Default`（CS0618）；`ExtensionUiDialogWindow` 补仅供 XAML 加载器的无参构造（AVLN3001）。构建 0 警告。
- **P2-18**：后续 `MessageFinished` 无 usage 时不再沿用上一条用量。
- **F01 信任弹窗**：消费 A 的 `ProjectTrustDecisionRequiredException`：显示项目绝对路径与受保护资源，“信任/不信任”落盘后重试一次；关闭弹窗不记录、不重试。措辞：信任不启用项目扩展、不代替逐次工具审批（以审批指示为准）、下次 Pi 进程生效。

### H2 视觉基座（F09）
- `Styles/FluentTokens.axaml`：移植 UniGetUI（MIT）Light/Dark Fluent v2 令牌，accent 跟随系统；`FufucodeTheme.axaml` 保留 App* 键名并改为派生。VM 不再含颜色（`StatusTone` 语义色调 + XAML 类映射），`.cs` 硬编码色审计零命中。
- `Styles/Icons.axaml`：Fluent UI System Icons（MIT，commit `a563cf9`）20 regular path，`PathIcon` 渲染，无新增 NuGet。
- 外壳：260px 导航栏（WinUI 选中指示条）、对话/扩展双页常驻 + 淡入上移过渡、页头状态 chip 取代平铺状态栏、浮动 composer、InfoBar 式错误/提示/中性卡、工具卡状态徽章、ContentDialog 式对话框。Mica 仅在 `ActualTransparencyLevel==Mica` 时合并覆盖；**标题栏扩展未做**（Avalonia 12.1.3 与系统标题按钮协作未确证）。
- 修复代码围栏两侧空行（段首尾 `\n\n` 渲染为空行，~48px→~13px，布局断言防回归）。

### H3 M5 扩展管理页
- `Integration/ExtensionCatalog.cs`：解析真实 `get_commands`（0.87.1 形状，容错），按 `sourceInfo.baseDir` 归组已加载扩展；订阅 `ExtensionError`（`ExtensionPath` 为入口文件，按“目录或其下文件”归组）；按 Pi `loader.js` 规则镜像扫描 `<project>/.pi/extensions` 与 `<AgentDirectory>/extensions`。
- 扩展页：已加载扩展（路径/命令数/最近错误/审批桥状态）、已发现扩展、命令/提示词/技能列表 + 搜索 + “填入”、项目信任卡（改选→落盘→可立即重启）、RPC 不支持能力（照录 [轨道B] 已知限制）、最近启动失败卡。mock 模式显示空状态，不造数据。
- 安全重启：`RealSessionHost.RestartCurrentAsync` 同 id 重开（SessionLease 限制只能先释放后恢复）；回合进行中/有待决扩展请求时禁用。
- composer 命令入口：`apps_list` 按钮 + Flyout（搜索、点击填入并聚焦）；不做“输入 / 自动弹出”（IME 风险）。填入规则：空草稿设为 `/name `，非空草稿前置。

### H4 扩展启停真实生效（消费 A 的 `UserExtensionPaths`）
- 启用列表按项目持久化（`desktop.db` preference `app.enabled-extensions:<projectId>`；手动添加 `app.added-extensions:`；最近加载错误 `app.extension-errors:`，加载成功即清除）。启动时缺失路径被跳过但保留在列表中，行显示“路径缺失”。
- 开关只改期望集合，页顶 InfoBar“有 N 项扩展变更，重启会话后生效 [立即重启][撤销]”；应用 = 释放旧进程 → 赋 `UserExtensionPaths` → 同 id `ResumeAsync`。信任未决先弹信任窗再重试。
- 失败回滚（A 交接要求）：`UserExtensionLoadException`（缺失/Pi 启动失败）或 `ProjectExtensionNotTrustedException` → 恢复旧列表重开原会话，持久列表不变，错误写到对应行；回滚也失败 → 无活动会话遮罩 + 重试。启动期扩展失败时遮罩提供“停用该扩展并重试”。
- 行状态：内置（审批桥，常开）/已加载/待重启：启用/待重启：停用/未启用/路径缺失/加载失败；项目扩展在项目未信任时行内提示。“添加扩展…”选文件夹，按 Pi 入口规则校验。“已加载”依据是当前进程的启动快照——Pi 0.87.1 任一显式扩展加载失败即退出，启动成功即代表已加载。
- 四类弹窗与加载失败的真实 Pi 自测改走产品路径（启用→应用重启），删除 H3 的 `approvalBridgePath` 测试注入。

## 验证（实际运行，本 worktree）

- `dotnet build Fufucode.sln -nologo`：0 错误 0 警告。
- `--selftest`：H1 46 项 → H2 61 项 → H3 84 项 → H4 96 项 ok，0 失败（`tmp/h4-selftest.txt`）。复审后新增：重启失败流程（遮罩+重试+恢复）、信任拒绝不假称保留会话。
- **终验（H4 提交后，`d6267cd`）**：`--selftest` 96 ok / 0 失败 / 0 SKIP（`tmp/final-selftest.txt`）；`--selftest --realpi` 137 ok / 0 失败 / 0 SKIP，Atria 全绿（`tmp/final-selftest-realpi.txt`）；Core 测试（A 的套件，只运行）150 PASS / 0 FAIL（`tmp/final-core-tests.txt`）。realpi 扩展段：bridge 目录与路径一致；安全重启同 id、历史不减、草稿保留；demo 经产品路径启用后审批桥仍 Verified、`demo.ui-tour` 注册，**四类弹窗（select/confirm/input/editor）经 `ExtensionUiDialogHost` 应答路径真实触发**，`ui-tour-result.json` 四值断言通过；停用后命令消失；加载期抛错扩展 `PiStartupFailure` 回滚；DoNotTrust 项目内扩展 `NotTrusted` 回滚；缺失路径被跳过。Atria 段：多轮对话、A01 批准/拒绝、停止链路、关闭重开恢复。中间轮次曾遇 Atria `Connection error.`/429 上游限流（只影响模型段，按 [协调] 约定记录，未改断言）。
- 截图：`tmp/h2-shots/`（主窗口顶/底、扩展空态、信任弹窗、confirm 弹窗，各 Light/Dark）、`tmp/h3-shots/`（扩展页 Light/Dark、命令 Flyout）。

## 未完成 / 未验证项

- Track B 的 `atria-widget-lab`/`broken-fixture` 尚未进 main；本轮用 demo 扩展与自测临时生成的故障/项目内扩展验收。
- 交互式桌面：真实 IME（Rime/微软拼音）、Mica 实际观感、窗口动画、弹窗人工点击未在交互式桌面验证（无头渲染 + preedit 注入覆盖逻辑）。
- 已发现扩展只镜像目录发现规则，未解析 Pi settings 中声明的扩展/包（`--no-extensions` 下本就不加载）。
- 截图中扩展页数据来自按 0.87.1 形状编写的测试夹具；真实数据一致性由 realpi 断言覆盖。

## 交轨道 A 的请求（M5 开工核实，2026-09-29；两项均已由 A `5df7279` 解决，D 已接线）

1. **[阻塞 M5 启停生效] 用户扩展加载入口**。现状：`PersistentSessionManager.OpenAsync` 固定 `--no-extensions` + 仅桥接 `--extension`（`PersistentSessionManager.cs:103-104`），App 无任何途径让用户选择的扩展进入 Pi 进程；`--no-extensions` 会同时屏蔽 settings 声明的扩展（`resource-loader.js:316-318`，只保留 CLI 显式路径），所以 App 侧也不能靠写 pi-agent settings 绕过（按硬边界不绕）。
   - 建议接口：`PersistentSessionManager` 增加 `public IReadOnlyList<string> UserExtensionPaths { get; set; }`（默认空；对之后的 `CreateAsync/ResumeAsync` 生效），逐个追加为桥接之后的 `--extension <绝对路径>`，保留 `--no-extensions`。路径不存在时在拉起进程前抛出带路径的类型化异常。
   - 需知：Pi 0.87.1 任一扩展加载失败 → 启动诊断 `Failed to load extension "<path>": ...` 写 stderr 并 `process.exit(1)`（`main.js:726-735`）。Core 现以 `PiRpcException("Pi process exited with code 1. stderr: ...")` 透出（`PiRpcClient.cs:260`），D 暂按该文本解析"最近加载错误"；若 Core 能给出结构化的加载失败列表（路径+错误）更稳。
2. **F05 分类映射未落地**：用户停止后 `message_end.stopReason="aborted"` 的 `errorMessage` 被写入 `_error`（`ChatSessionCoordinator.cs:443`），适配器按 `Unknown` 发出 `SessionError`（`PiChatSessionAdapter.cs:130-137`）——feedback F05 所述"A 已修"在当前 main 上不成立。建议：停止期间或 `stopReason=="aborted"` 时映射为 `SessionErrorKind.UserCancelled`（或不入错误流）。D 侧在做 UI 过渡映射（停止窗口内的 Unknown/UserCancelled 一律中性"已中断"），Core 修复后 D 的映射自然退化为无操作。

---

# 附：M3 审批接线 + M4 侧栏（MVP 收口，已验收）

任务书：`docs/archive/assignments/track-d-integration.md`（M2，已完成并验收）、`docs/archive/assignments/track-d-m3m4.md`（M3/M4，本次）。工作区：`.codex/worktrees/track-d-app/Fufucode`，分支 `track-d`。M3/M4 开工前已 `git merge main`（引入 Atria 端点约定 `aa67397`；M2 成果已经 `8ebee1c` 合入 main）。

## 本次完成（2026-09-29，M3+M4）

全部改动限于 `src/Fufucode.App/`：

1. **M3 审批接线**（任务一）
   - 新增 `Views/ExtensionUiDialogWindow.axaml(.cs)`：confirm（允许/拒绝）、select（选项列表）、input（单行）、editor（多行 prefill）四类弹窗；**非模态**——审批期间主窗口时间线继续滚动（A02）；X 关闭一律按取消/拒绝（fail-safe）；每个请求只应答一次；editor 无 Pi 端超时，确定/取消两路径恒可达（Core 另有本地 60s 截止兜底）。
   - 新增 `Integration/ExtensionUiDialogHost.cs`：待处理请求 → 弹窗映射；`ExtensionUiRequestClosed` 撤下失效弹窗（不假设服务端清理）；`AutoResponder` 测试钩子（无头验证 A01 允许/拒绝）；弹窗处理不等 `SendAsync` 先完成（[轨道B] 时序）。
   - `Integration/SessionUiWiring.cs` 重写：免答通知类全量映射——notify/setWidget→时间线轻提示，setStatus→状态栏（`StatusBarViewModel.SetExtensionStatus`），setTitle→窗口标题，set_editor_text→输入框（`InputViewModel.ApplyExternalText`：空草稿直接采用，非空存为待恢复并明确提示，计划 §8.4）；extension_error 透出诊断。M2 的"审批功能接入中"占位已移除。
   - 审批状态沿用状态栏 Verified/Unavailable/Unverified 显示（M2 已有）。
2. **M4 侧栏与会话管理**（任务二）
   - 新增 `Integration/RealSessionHost.cs`：统一管理会话生命周期（首次打开/新建/切换/重命名/按会话草稿）；**先开新会话再放旧会话**——切换失败旧现场不丢；进程与会话独占由 Core SessionLease 保证。
   - 侧栏真实模式：项目内会话列表（含"当前会话"标记与最近打开时间）、选中即切换、新建会话按钮、当前会话重命名（TextBox+保存，经 `set_session_name` 落盘）、项目路径显示；mock 模式行为不变。
   - 草稿按会话隔离：编辑防抖 600ms 落 desktop.db drafts 表；切换/关闭前强制落盘；装配时按会话恢复。
   - 切换/新建走启动遮罩（"正在切换会话…"），完成后整体重建视图模型并回放历史。
3. **自测扩展**：`--realpi` 新增 M4 段（5 项断言）与 Atria 真实模型段（`TestRealModelAtriaAsync`）；key 按 [协调] 约定读用户级 `FUFUCODE_ATRIA_API_KEY` 注入进程环境（子进程继承，不落盘不入库）；隔离数据目录写入与产品同形的 models.json（apiKey 仅 `$FUFUCODE_ATRIA_API_KEY` 插值引用）。

## 验证（实际运行，本 worktree，2026-09-29 复跑）

- `dotnet build Fufucode.sln`：0 错误（仅 progress-c 已记录的 `Bitmap.Save` 旧警告）。
- `--selftest`（mock 全段）：0 项失败，不回退。
- `--selftest --realpi`：**0 项失败**，全量证据：
  - 真实 Pi 段：连接/桥接 Verified/无凭据拒绝+草稿回填/P07 handled/真实停止链路/组合根两路径——全绿（同 M2 轮）。
  - **M4 段**：新建并切换（新进程绑定新会话）、新会话草稿为空、切回 A 草稿恢复、重命名落盘可读、B 草稿独立保留——5/5。
  - **Atria 真实模型段**（`atria/Atria-Dawn-Preview`，状态栏实测正确显示）：
    - M2 真实模型多轮对话两轮完成（第 2 轮依赖上下文，流式渲染经时间线）——**M2 最后一项验收闭环**；
    - A01：审批请求真实到达，**批准后写盘生效**（allow-probe.txt 内容实测 `fufucode-allow`）；**拒绝后无写盘副作用**（deny-probe.txt 不存在）；
    - 真实运行中停止 → Interrupted 终态，停止后 3s 时间线零新增（不自动继续）；
    - **含内容的关闭重开恢复**：15 条历史消息完整回放，含第 1 轮用户消息原文。
- 端点稳定性记录：首次跑第 2 轮 `outcome=Failed`、二次跑第 1 轮 `Failed`（免费端点瞬态，非代码缺陷——同一代码路径在三次运行中均有两轮全过与 A01 双通过的证据；测试已按 [协调] 约定区分"端点不可用"（SKIP+错误文本）与缺陷（FAIL），第三次复跑全绿）。

## 未完成 / 未验证项

- **交互式弹窗 UI**：四类弹窗的渲染与按钮交互未在真实桌面人工/自动化操作（无头环境以 AutoResponder 驱动同一应答链路）；editor/input/select 未获真实触发（桥接当前只用 confirm）。
- **停止中拒绝工具审批的组合**（停止测试期间 AutoResponder 固定拒绝）与审批中超时撤窗路径，未经真实触发。
- 交互式桌面的启动遮罩、侧栏切换、IME 等行为（计划 §11 第 5 层）未自动化验证。
- 打包布局运行时定位（M7，配合轨道 F 的 P1-4）未做。

## 交轨道 A 的记录（Core 侧，不直接改）

1. （沿 M2 轮）P1-3 证据已补：0.87.1 真实 `clear_queue` 形状成立，建议补记 verification.md。
2. 复跑观察：免费端点瞬态失败会以 `outcome=Failed` 收敛（无永久"处理中"），Core 终态判定表现正确；无需改动。

## 已知限制（设计内）

- 历史回放不恢复工具输出文本；工具卡片状态按持久化 stopReason 标注，不虚构执行结果（M2 轮已记）。
- 运行中提交一律 `FollowUp` 排队；插入/排队选择入口留待后续（审查 P2-17）。
- 草稿防抖落盘（600ms）；切换/关闭时强制落盘兜底。

## 当前可运行入口

- mock 演示：`dotnet run --project src/Fufucode.App -- --mock`
- 真实会话：`dotnet run --project src/Fufucode.App -- --project <项目目录> [--session <id>]`（Atria 配置已就绪于 `%LocalAppData%\Fufucode\pi-agent\models.json`；key 走 `FUFUCODE_ATRIA_API_KEY`）
- 自测：`--selftest`（mock）/ `--selftest --realpi`（真实 Pi + Atria 模型全量验收）

## 下一步

M0–M4 MVP 用户路径已全链路真实验证（启动→提交→流式+工具卡片→弹窗审批→停止→关闭→重开恢复）。待协调安排：M5 插件交互或 M6 设置与桌面体验；交互式弹窗的桌面人工验收建议由用户试用确认。

---

# 附：M2 集成轮记录（2026-09-29，已验收并入 main）

## 本次完成（2026-09-29）

组合根替换与真实链路接通，全部改动限于 `src/Fufucode.App/`：

1. **组合根替换**（任务书 1）
   - 新增 `Integration/AppOptions.cs`：默认真实 Pi；`--mock` 回演示会话；`--project`/`--session`/`--pi-node`/`--pi-cli` 覆盖。
   - 新增 `Integration/RealSessionBootstrap.cs`：从 `AppContext.BaseDirectory` 向上定位 `runtime/node/node.exe` 与 `runtime/pi/.../cli.js`（缺失时报带可操作信息的诊断，不静默降级）；经 `PersistentSessionManager` 新建或恢复会话——`--session <id>` 指定恢复，缺省恢复该项目最近会话（失败如实记录诊断并新建），无会话则新建。进程参数（`--session-dir`→应用数据目录、cwd→项目、`PI_CODING_AGENT_DIR`、桥接 `--extension`）全部由 Core 组装，App 不拼参数。
   - 新增 `Integration/HistoryMapper.cs`：Pi 历史消息 → 冻结契约终态消息（见"已知限制"）。
   - 新增 `Integration/SessionUiWiring.cs`：审批占位与扩展通知接线（任务书 5），窗口与自测共用。
   - `MainWindow`：真实模式先显示启动遮罩，后台完成子进程启动+历史恢复后才装配 DataContext（计划 §7.2"先重建状态再接收新工作"）；启动失败保留遮罩显示诊断，不退回 mock；关窗先 `DisposeAsync`（Core 有界关闭 Pi 进程树）再放行。
   - `Fufucode.App.csproj` 增加对 `Fufucode.Core` 的 ProjectReference（decisions.md 既定：集成时 App 引用 Core）。
2. **真实流式链路**（任务书 2）：`TimelineViewModel`/`InputViewModel`/`StatusBarViewModel` 原样消费冻结接口事件；`InputViewModel` 现按连接/活动/错误/终态事件刷新命令可用性（真实链路 CanSend 受审批门控）。遵守 decisions.md [轨道C] 线程约定（订阅侧 Dispatcher 封送 + 断言前排空 UI 队列）。
3. **停止与草稿**（任务书 3）：停止按钮→`StopAsync`（clear_queue+abort）；`QueueCleared` 恢复草稿沿用既有实现；发送/停止失败新增 `OperationFailed` 显式上报且发送失败回填草稿（顺带修了审查 P2-1）。
4. **恢复路径**（任务书 4）：`TimelineViewModel.LoadHistory` 把 `History` 当前分支按"开始+终态"事件灌入同一重建管线单次刷新；中断会话（`LastOutcome==Interrupted`）显示提示且不自动重发。
5. **审批占位**（任务书 5）：对话框类 `extension_ui_request` 显示"审批功能接入中…未应答将按超时自动拒绝"，不应答、不 stub 审批语义；免答 notify 类显示为"扩展通知"。状态栏新增审批桥接状态（仅 Verified 显示"已验证"，不可用透出诊断）。
6. **自测扩展**：`--selftest` 默认仍全 mock（无 Pi 环境全绿）；新增 `--realpi` 段（隔离临时数据目录/项目目录，不触碰用户 LocalApplicationData）。

## 验证（实际运行，本 worktree）

- `dotnet build Fufucode.sln`：成功，0 错误 0 警告（首次全量 1 个警告为 progress-c 已记录的过时 `Bitmap.Save` 重载）。
- `dotnet run --project src/Fufucode.App -- --selftest`：0 项失败（mock 全段，含 500 条长会话 0.95s、无头渲染 22,169 字节位图）。
- `dotnet run --project src/Fufucode.App -- --selftest --realpi`：0 项失败。真实 Pi（0.87.1，本 worktree `runtime/` 自 `C:\Projects\Fufucode\runtime` 复制）实测要点：
  - 子进程连接（get_state 有效）、审批桥接探针 Verified、状态栏显示真实状态（模型文本实测 `unknown/unknown`，隔离 pi-agent 无凭据）；
  - 无凭据 prompt 被真实 Pi 拒绝（"No API key found"），经 UI 链路显式报告且草稿回填，不留永久"处理中"；
  - 扩展命令 `/fufucode-bridge.status` 经 UI 链路完成、未永久等待运行结束（**P07 真实 Pi 证据**）；其 notify 实测到达并显示（`hasUI=true, timeout=120000ms`）；
  - **真实 Pi `clear_queue`+`abort` 停止链路完成**——审查 P1-3 关注的 `clear_queue` 响应形状（steering/followUp 数组）在 0.87.1 真实进程上成立；
  - 关闭重开：恢复连接成功，历史一致（本次会话 0 条消息，见未验证项）；组合根全路径（新建→缺省恢复最近→按 id 恢复）三项通过；
  - 附带观察：扩展命令往来未在 `get_entries` 落盘（恢复历史 0 条），如实记录。

## 未完成 / 未验证项

- **真实模型多轮对话：待验（缺凭据）**。任务书允许：应用专属 pi-agent 需配置 API key/OAuth，本环境无可用凭据（未设 `FUFUCODE_TEST_*`，无 ANTHROPIC/OPENAI key）。凭据就位后需补验：多轮流式渲染、终态、停止后不自动继续、模型失败不留永久"处理中"、含内容的历史恢复。
- **含内容的历史恢复**：本次实测会话 0 条消息（prompt 被拒、扩展命令不落盘），恢复路径机制已验但内容回放（`HistoryMapper` 映射正确性、思考块/工具卡片回放）未经真实数据验证。
- **对话框类审批请求占位**：notify 路径已实测；confirm/select/input 的"审批功能接入中"占位未经真实触发（需模型驱动工具调用）。
- **交互式桌面 UI**（计划 §11 第 5 层）：启动遮罩、滚动、IME 等真实窗口行为未自动化验证；本环境无交互式桌面自动化。
- 打包布局下运行时定位（M7，配合轨道 F 的 P1-4 修复）未做。

## 交轨道 A 的记录（Core 侧，不直接改）

1. **P1-3 证据已补**：0.87.1 真实 `clear_queue` 返回 `steering`/`followUp` 数组（本测试停止链路通过），建议在 `docs/verification.md` 补记。
2. `PiChatSessionAdapter` 的块/用量映射为 private，App 侧 `HistoryMapper` 存在镜像实现；若 Core 公开等价 helper，App 应换用以消除重复（候选，不紧急）。
3. P1-1/P1-2 修复在开工同步时（main `6ebb121`）尚未进 main；本集成不依赖其行为（扩展命令均为快速 handled，无对话框挂起场景）。

## 已知限制（设计内）

- 历史回放不恢复工具输出文本；工具卡片状态按所属消息持久化 stopReason 标注（aborted→Cancelled，error→Failed，其余→Completed），不虚构执行结果；`toolResult`/`custom` 条目不单独回放。
- 真实模式侧栏会话列表只读；新建/切换会话属 M4。
- 运行中提交一律 `FollowUp` 排队（Core 适配器行为），"插入或排队"选择入口按计划 §6.5 留待后续（审查 P2-17 同记录）。

## 当前可运行入口

- mock 演示：`dotnet run --project src/Fufucode.App -- --mock`
- 真实会话：`dotnet run --project src/Fufucode.App -- --project <项目目录> [--session <id>]`（需 `runtime/node` 与 `runtime/pi`；发送功能要求审批桥接 Verified + 应用专属 pi-agent 配好模型凭据）
- 自测：`--selftest`（mock）/ `--selftest --realpi`（含真实 Pi 段）

## 下一步

凭据就位后完成真实模型多轮对话验收（M2 完成条件最后一项）；随后按协调安排进入 M3 对接（桌面端审批处理，依赖轨道 A 的 M3 Core）。

---

## Wave 3 残余补跑（2026-10-01，装好的 1.0.0 合并包）

协调者派发的三项残余复核，全部完成：

1. **隔离实例发消息 + 审批桥加载 + 审批放行/拒绝各一次**：以独立 `FUFUCODE_DATA` 跑 `--selftest --realpi`（证据 `tmp/w4/leftover-realpi.txt`）——**210 项 ok / 2 项 FAIL**。桥加载 Verified、A01「批准后写盘生效（fufucode-allow）」「拒绝后无写盘副作用」均 pass。两项 FAIL 与 Wave3 记录完全相同：demo 扩展启用（安装布局不含 dev 树 demo 路径）+ `demo.ui-tour` 无结果文件（所选模型无 API key，`Pi rejected prompt: No API key found`）——夹具/环境限制，非产品缺陷，不转判。
2. **新终端 CLI 接线**：重建 Machine+User PATH 的新进程里 `fufucode` 命中 `{app}\cli\fufucode.cmd/.exe`，`sessions list --json` exit 0 返回 JSON（`tmp/w4/cli-check.ps1`）。
3. **开始菜单快捷方式**：`Fufucode.lnk` → `Fufucode.cmd`，`WorkingDirectory=C:\Users\21894\Documents`，与系统 Documents 目录一致（`tmp/w4/shortcut-check.ps1`）。

本轮未复跑 0.9.0→1.0.0 升级路径（任务只列三项残余；升级与完整性记录见 `tmp/w3/`）。前台纪律：本轮零前台化，信息全部后台 UIA；过程中目标窗被最小化时驱动的拒答正确（无越界点击）。

## Wave 4（F11）：对照 UniGetUI 实机界面的精确视觉对齐（未提交）

基准：`reference/UniGetUI` 源码 + 实机 UniGetUI 2026.3.0（后台 UIA 取证：`tmp/w4/ug-*.json`，截图 `ug-discover.png / ug-settings.png / ug-navdock.png`）。改动均在 `src/Fufucode.App/`，未提交。

### 改动

- **页过渡**：`FadeSlidePageTransition` 重写对齐 `EntrancePageTransition`——250ms / 28px 上滑淡入 / `SplineEasing(0,0,0,1)` / 旧页立即隐藏 / 取消安全还原。
- **减少动画**：新增 `Infrastructure/MotionPreference.cs`（Windows `SPI_GETCLIENTAREAANIMATION`，与 UniGetUI 同一信号源），页过渡与导航条均降级为瞬时切换。
- **共享导航选中条**：新增 `Infrastructure/NavSelectionPill.cs` + MainWindow 覆盖层——3x16 圆头 pill 在导航项间移动，前缘 `Bezier(0,0,0,1)`/后缘 `Bezier(0.5,0,0.2,1)`、300ms 边缘拉伸（UniGetUI 同算法，渲染变换驱动不触发布局）。
- **侧栏结构重排**：主导航（对话/扩展）居顶、会话列表居中、设置+项目切换入底栏（对齐 NavListBox/FooterNavListBox 分区）；导航项改为图标 24px + 标签 14px + 间距 12 + 内边距 8,8 + 项高 ~46 + 圆角 6 + Margin 0,2。
- **控件几何补齐**（`FufucodeTheme.axaml`）：ComboBox 下拉项（内缩 4 / 高 32 / 圆角 4 / 悬停选中按压三态）、MenuFlyout/ContextMenu 表面与内缩项、CheckBox/RadioButton/ToggleSwitch accent 三档+glyph、ProgressBar accent、ListBoxItem 全局圆角 4。
- **composer 下划线**：改常驻透明条 + 120ms opacity 过渡，悬停中性线、聚焦 accent 线（对齐 MegaQueryUnderline 方案，替代原瞬时 IsVisible 切换）。
- **页头**：扩展页加 40px 页图标 + 28px 标题（对齐 MainHeader 80px 图标列）；设置页标题升 28px；设置卡 MinHeight 60 / Padding 16,12（半径 8 原已具备）。

### 逐项对比（已过项有截图/代码证据）

| 项 | 结果 | 证据 |
|---|---|---|
| 页面切换过渡（时长/曲线/位移） | 过（参数级一致：250ms/28px/spline(0,0,0,1)） | 代码 diff |
| 导航选中条移动动画（前后缘双曲线） | 过（同算法实现；pill 随页移动见截图） | `ff-chat.png`/`ff-ext.png`/`ff-settings.png` 中 pill 位置随页变化 |
| 导航项几何（图标 24/标签 14/圆角 6/间距） | 过 | 同上截图 vs `ug-navdock.png` |
| 侧栏分区（主导航顶、设置底栏） | 过 | 同上 |
| 列表项悬停/选中/焦点分层 | 过（SubtleFill 三态 + :focus-visible 外框） | 代码 |
| 悬停/按压过渡时长 | 过（83ms BrushTransition，与参照一致） | 代码 |
| 命令 flyout 几何（内缩项/表面/边框） | 过 | `command-flyout-light.png` + MenuFlyout 样式 |
| composer 下划线焦点态 | 过（120ms 过渡） | 代码 |
| 图标体系（Fluent System Icons 全套、无 emoji） | 过 | `Styles/Icons.axaml` |
| 设置卡规格（半径 8/高 60/边距） | 过 | `ff-settings.png` vs `ug-settings.png` |
| 控件令牌（ComboBox/Toggle/CheckBox/Radio/ProgressBar） | 过 | 代码 + `ff-ext.png` 开关态 |
| 深浅主题对比度 | 过（双主题字典同源） | `h2-shots/main-light.png`、`main-dark.png` |

### 未过 / 差异项（如实记录）

- **自绘标题栏未做**：UniGetUI 标题栏为自绘（返回箭头/汉堡/居中搜索/自绘 caption 键）。Wave1 曾试 ExtendClientArea 出现系统标题重叠与黑带已回退；完整自绘 caption+拖曳区超出本轮额度，列后续项。
- **图标专列（icon-only rail）与汉堡折叠未做**：我们恒为带标签布局（对齐其展开 dock 态）；UniGetUI 默认态是 80px 图标列。
- **悬停态逐像素帧对比缺证**：后台 UIA 不产生真实指针悬停；悬停观感靠同一令牌+同参数过渡保证，未做高速截图帧级比对（限制说明）。
- **亮色主题实测桌面截图缺**：以无头渲染图替代（`tmp/h2-shots/*-light.png`）。
- **小书匠图标库未采用**：现有 Fluent System Icons 集与 UniGetUI 同族且已覆盖全部槽位；换第二套图标族违反「全套风格一致」约束。结论：不引入，记录在案。
- **空态插图**：沿用 G 轨道产出（位置/尺寸不变），UniGetUI 空态为单色线稿——风格差异按品牌不复制原则保留自有插画。

### 验证

- `dotnet build src/Fufucode.App`：0 警告 0 错误。
- `--selftest`（mock 全段，含 H2/H3 截图断言）：152 ok / 0 FAIL（`tmp/w4/selftest2.txt`）。
- 实机后台截图：Debug 实例三页 `tmp/w4/ff-chat.png / ff-ext.png / ff-settings.png`。

### 待协调者

- 改动未提交（按派单要求留 worktree）；并排对比材料在 `tmp/w4/`（`ug-*.png` vs `ff-*.png`）。

## Wave5-R1 五区前端第一波（2026-10-02）

### 遗留 6 项处置

| 文件 | 处置 | 依据 |
|---|---|---|
| `CommandPaletteViewModel.cs` | 删除 | 这是独立命令面板，不属于 R1 的五区首波；相关 VM 初始化/导航注册已清除。 |
| `ChangesPanelViewModel.cs` | 删除 | 这是完整变更面板功能，属于右栏后续波次；R1 只保留可收起骨架位。 |
| `NotificationService.cs` | 删除 | 当前无调用点，与五区骨架及会话对话呈现无关。 |
| `SidebarViewModel.cs` | 保留 | 继续承载项目、会话列表、重命名、切换及 mock 状态，匹配左栏任务/会话列表。 |
| `StatusBarViewModel.cs` | 保留 | ChatPage 仍依赖连接、模型、活动、权限及终态状态；作为会话头部紧凑状态摘要的数据源。 |
| `MainWindow.axaml.cs` | 修改/保留 | 延续窗口与页面生命周期，增加右侧骨架开合宽度同步；会话/设置/扩展导航保持原路径。 |

### 五区和对话实现

- Window 主网格：52 DIP 导航轨 + 260 DIP 项目/会话栏 + 中央工作区 + 默认 0 DIP 右侧工作区列；右栏目前只有“工作区面板 / 文件、差异与预览区域”骨架，通过聊天头部按钮开合，未接文件/Changes/Review 数据。
- 底部活动/终端挂点已预留并默认隐藏；本轮不接真实终端。
- 左栏保留全局导航、创建会话、会话列表、项目选择和设置；Rail 额外提供对话/扩展/设置入口。会话行继续显示标题和副标题。
- 对话头部呈现会话标题、项目名、活动摘要及状态芯片；时间线内容宽度改为 768 DIP 并保留居中边距。消息、助手块、思考块、工具卡片仍按现有类型分层呈现。
- Composer 保留发送、排队、steer、停止、扩展命令入口；停止后待恢复草稿显示恢复提示和明确恢复按钮。

### Atlas 逐组件核对（00/01/02/03）

| 组件 | 状态 | 说明 |
|---|---|---|
| 00 左轨 / 任务栏 / 中央区 / 右栏空间关系 | 部分通过 | 52/260 DIP 侧区和中央区已实现；右栏默认收起且仅骨架；Atlas rail 约 56px，本轮 52 DIP；未实现真正 split separator。 |
| 01 左栏入口、会话/任务行 | 部分通过 | 导航、项目、会话列表存在；当前仅基本标题/副标题，无未读/运行/失败 glyph、动态尾区菜单、组织/排序菜单。 |
| 02 Changes 文件卡 | 未实现（按 R1 范围） | 右栏只有骨架，没有 diff 文件列表、加减统计、折叠或查看 Changes 功能。 |
| 03 Composer 输入和主动作 | 部分通过 | 输入框、扩展入口、steer/排队/发送/停止存在；没有附件、语音、模型权限 pill、队列编辑/删除/Resume 或投递未确认界面。 |

### H-S04 / H-S05 覆盖

- H-S04 部分覆盖：会话标题、项目名、活动摘要/状态、用户/助手/工具/思考块层级及可滚动时间线已存在；未覆盖分支名、右栏 tabs、真实展开活动状态和完整头部窄宽布局。本轮无真实 Pi 运行截图，不能标记通过。
- H-S05 部分覆盖：普通输入、steer、follow-up 排队、停止及停止后的待恢复草稿入口有 UI；附件、队列暂停/恢复、编辑/删除、投递未确认状态和恢复失败状态未覆盖。未标记通过。

### 验证和证据

- `dotnet build --no-restore`：成功，0 warning / 0 error（整套 solution）。
- `dotnet run --project src/Fufucode.App --no-build -- --selftest`：0 项失败。
- 无头主窗口截图：`tmp/w5r1/main-window.png`（1440×900）；辅助页面截图复制为 `tmp/w5r1/chat-light.png`、`tmp/w5r1/extensions-light.png`。主窗口图确认五区主结构与空白会话 composer 的静态布局；它不是运行中的真实会话证据。
- Codex atlas 已逐张查看 00-full、01-sidebar、02-changes-card、03-composer。组件通过状态仅限上表列出的结构项；未完成/未覆盖项如实列出。
- Track-d 工作区缺少 `docs/archive/assignments/track-d-fivezone.md`；本次从 A 的 main worktree 读取任务书副本。当前 track-d HEAD 与本地 `main` 指针相同（cf57fe5），没有执行 merge。


## Wave5-R2 terminal 技术选型（实现前记录）

选择 **Windows ConPTY + 原生自渲染**。R2 要在应用内保留多个终端实例的 tab、支持输入/输出与 resize；HWND reparent 依赖外部终端的窗口实现和启动时序，隐藏/重开时状态难以稳定；WebView2+xterm.js 引入运行时和 NuGet/前端资产依赖，当前尚未获批。ConPTY 是 Windows 自带 API，可以避免新 NuGet，且每个 tab 对应一个明确的 terminal session。

风险：需维护 P/Invoke 启动/关闭及管道生命周期；ANSI/VT 渲染、字体/光标/颜色、终端宽高回报需要限定实现；此方案不等同完整 xterm，先实现 R2 可验证的基本命令行与文本呈现。不会将 Pi agent 的 tool 命令接入交互 shell；H-S09 本波只提供静态实例归属容器。

## Wave5-R2 交付与验收（2026-10-02）

### 实现与接线

- 右栏由 `WorkspacePanelViewModel/View` 重建：稳定 Files/Changes tab、tab 关闭/重开、New Tab 菜单；Review/Preview 为禁用骨架；各正文独立滚动。Files 按实际项目逐层浏览，每层最多 300 项，链接目录不展开，完整路径可复制。
- Changes 双口径接 Core `ChangeRangeQuery.WorkspaceAsync` / `SessionWritesAsync`，后者取当前 Coordinator snapshot 的 Messages/Tools；查询 generation/cancellation 阻止旧会话迟到结果污染。数据缺失与成功空列表不同，null 标记保持未知，未伪造 ±行数。真实路径已接线；终验仍需协调者用真实会话验证。
- 右栏命中分割柄 10 DIP、视觉线 2 DIP，键盘 16 DIP 步进；宽窗并排，1200 DIP 抽屉，800 DIP 自动收会话栏，1600 DIP 并排；面板关闭恢复触发焦点。宽高偏好写 DesktopMetadataStore，渲染按当前窗口限幅，窄窗不覆盖宽窗偏好。
- 底栏独立 ConPTY shell，每 tab 有 terminalSessionId/VT buffer/滚动状态，支持创建/关闭/隐藏、键盘或拖动高度、Ctrl+J 唤起。隐藏不结束 shell；关闭 tab 通过 JobObject 结束该进程树，关窗口释放全部实例。按项目保存窗口内 dock，切会话保留该项目终端，项目切换绑定不同容器。
- 原生 TextBox 支持中文 IME 输入；基本 VT/双宽字符/颜色/擦行/光标及 1500 行历史，输入区固定 Dark 主题保持占位文字对比，标题条沿 App 主题。
- 左栏运行/失败/未读 glyph 与固定尾区菜单已补；当前状态来自 Core 活动/终态/连接事件，历史中断来自 WasActive。未读仅窗口内 `TurnSettled` 状态，回到对话清除，不声称跨重启持久化。

### guideline §3/§4 逐组件清单

| 编号/组件 | 状态 | 证据与边界 |
|---|---|---|
| L05 宽窄布局 | 已覆盖本波 | 800/1200/1600 DIP 渲染与断言；窄窗保相同 tab 对象，正文不缩字号。 |
| L06 separator/关闭/焦点/偏好 | 部分 | 10 DIP 命中/2 DIP 视觉、键盘调整、限幅持久化、关闭焦点恢复已实现；实际鼠标拖动与辅助技术 RangeValue 尚未实机验。 |
| K09 Changes 范围/文件/路径 | 部分 | workspace/session 两查询、new/deleted/binary/unknown、复制路径、loading/error/empty；无 patch/hunk/unified/split/逐文件行数，不能假称 Diff 编辑器。 |
| K10 Files | 部分 | 真实目录浏览与完整路径、稳定 tab/选中/独立滚动；无文本编辑、dirty、外改监控。 |
| I07/Z05 撤销 | 未覆盖 | Core 无 patch/快照/还原/stage 契约，未提供可点写操作。 |
| K10/Z04 终端 | 部分 | 独立真实 ConPTY 生命周期、双 tab、隐藏保留/关闭/resize/中文基础命令通过；全屏 TUI/alternate screen/scroll region/mouse/bracketed paste/输出选区复制不在本片实现。 |
| §3.4 中央文件联动 | 未覆盖 | 本波允许后置；中央路径→右 tab/行定位尚无文件内容提供者。 |
| §4 H-S09 归属 | 部分 | 项目静态归属容器；agent tool 输出仍在中央，未把 Pi stdout 写入 shell；真实并行关系未终验。 |
| 左栏动态状态/尾区 | 部分 | 当前会话运行/失败/窗口内未读、行菜单；历史终态/持久化未读缺字段；组织/排序菜单后置。 |

### H-S06/S07/S08/S09

- H-S06 部分：Files/Changes 可用、Review/Preview 禁用骨架、关闭/New Tab 与路径复制已验；文件 editor/审查/预览/中央定位缺内容契约。
- H-S07 部分：三个宽度布局、身份保留、右栏独立滚动容器、separator 键盘已验；真实双滚动锚点/拖曳/缩放/高对比验收尚未做。
- H-S08 部分：双 tab、创建/关闭/隐藏、同尺寸重开保留输出、尺寸限幅和真实 PTY resize 已验；鼠标拖高和实机中文输入法组合过程尚未实测。
- H-S09 部分：静态项目归属提示已呈现，独立 shell 与 Pi 工具卡分开；agent 命令与交互 shell 同时运行的实机截图仍待终验。

### 检查与修复证据

- App/solution build 实际执行：0 warning / 0 error；完整 `--selftest --conpty` 最终 0 项失败，日志 `tmp/w5r2/selftest-final.log`。
- `--selftest --r2-only --conpty`：0 项失败，`tmp/w5r2/r2-final.log`；覆盖 A8 mock DTO、unknown、旧查询隔离、300 项上限、布局/偏好/左栏状态和终端生命周期。
- 真实 ConPTY 初次检查超时：cmd 继承父进程的重定向 std 句柄，输出绕过 PTY。`STARTF_USESTDHANDLES` 且 std 句柄留空后修复，方法与 Windows Terminal 的 ConptyConnection 对应；再次实测 cmd 命令输出、Unicode、resize 与 exit(0) 通过。
- 自测暴露并已修：ESC7/8 缩窗光标越界、快速切 tab 的迟到滚动回调污染、重复 Close 后已释放 CancellationTokenSource.Cancel；相关断言与窗口关闭检查均通过。
- 无头截图：`tmp/w5r2/right-open.png`、`bottom-open.png`、`layout-800.png`、`layout-1200.png`、`layout-1600.png`。明亮主题右栏与双终端已查看；均为明确 mock 展示，不能替代真实 Core Changes/Pi 终态终验。
- 横向缩小终端暂未做 reflow：当前 screen 行右侧可能截断；同尺寸隐藏重开保输出。该限制不假标无损缩放。

### 交 A 清单

1. 每文件 ±行数、patch/hunk、基线/范围快照、还原/stage；当前 A8 只读路径 DTO 保持原语义。
2. 历史会话运行/失败终态与持久化未读元数据；D 仅维护当前窗口状态。
3. 文件读取/编辑、dirty/外改失效与行定位提供者；审查/预览内容能力。
4. PTY 未来跨平台后端与更完整 VT 依赖需独立立项；本轮无新 NuGet。

## Wave5-R3 开工选择（2026-10-02）

R2 验收后已 `git merge main`（Already up to date）。采用**应用内独立常驻设置页**：不新增 OS 窗口和第二套会话宿主，进入时保持会话/右 tab/项目终端对象，暂时隐藏工作面板，返回恢复身份与焦点。即时保存项不伪造 dirty；仅网络/端点/密码草稿由实际未保存值守卫。

自绘标题栏采用 `WindowDecorations.BorderOnly` + 独立 32 DIP Caption 行，G 的 46×32 @2x 控制状态原比例呈现（不拉伸到 UniGetUI 46×44），内容区与命中区分离。避免再用上轮产生系统标题重叠的扩展 Full decorations 路线。命令/用量/通知将以同一能力注册提供标题栏入口与快捷键。

## Wave5-R3 交付与五区收口终审（2026-10-02）

### 恢复与证据口径

- 用户误删工作区后，原 `track-d-app/Fufucode` 的 Git 元数据恢复，但源码和未提交文件仍缺失。已补回 Git 基线缺失文件，并从本次会话的原始补丁恢复 R2/R3 App 改动；分支仍为 `track-d`，HEAD `d83d7cc`。没有 commit、合并其他分支或 push。
- 删除前的 R2 日志与截图已丢失。上节保留历史实测记录；本次以重新生成的 `tmp/w5r2/*.png`、`tmp/w5r3/build.log`、`tmp/w5r3/selftest-final.log` 为准，不把旧文件引用当现存证据。
- 只修改 App 与本进度文件；没有新增 NuGet。截图是实际 Avalonia 控件的无头渲染，主窗口明确标为 mock；用量、通知和扩展三态采用注明的 fixture，不能替代真实 Pi/生产数据终验。

### §5 独立设置逐组件

| 编号/组件 | 状态 | 实现、验证与边界 |
|---|---|---|
| K11 分类/搜索 | 已覆盖本波 | 外观、模型与思考、网络与端点、通知、关于五类；分类和控件行共同过滤，明确无匹配状态。未添加账号/云服务等假分类。 |
| K11 控件行/窄窗 | 已覆盖本波 | 左说明右当前值；窄窗改为上下，分类可移到顶部；420/1280 DIP 控件检查，800 DIP 主窗口截图。分类正文保存各自滚动位置。 |
| K11 扩展三空态 | 已覆盖本波 | 查询成功空目录=未安装；已有目录无匹配=无匹配；查询失败=失败。筛选不改源集合/待启用状态；G extensions 图用于前两态，error 图用于失败，按比例缩放；mock 未连接另有说明。 |
| I11 保存四态 | 已覆盖已有链路 | 主题落盘且即时应用；运行中模型/思考只保存未来默认值；代理/端点等待重启；存储失败保留原配置，已应用模型在可核实时实际回退。凭据已写入而配置失败会说明未回退凭据。真实文件持久化与注入 RPC 检查通过，不称真实供应商验收。 |
| I11 dirty/返回 | 已覆盖窗口内路径 | dirty 只认代理/端点/密码真实草稿，收起表单仍计入；继续编辑/丢弃由同一离页守卫处理，覆盖导航、会话/项目切换与关闭。密码留在 View，丢弃清空。设置页往返保留会话 VM、右 tab、终端实例、草稿与焦点。离页测试注入对话选择，原生确认窗口点击未实机验。 |
| 设置期间会话 | 部分 | 页面切换不重建/停止宿主；无头检查确认项目终端仍运行与身份保持。本轮没有真实 Pi 运行中进入设置的产品截图。 |

### §6 跨区与全局逐组件

| 编号/组件 | 状态 | 实现、验证与边界 |
|---|---|---|
| K08 命令面板 | 已覆盖当前动作集合 | 搜索、分组、实际快捷键、禁用原因；上下选择、Enter、Esc、焦点返回。标题栏按钮/面板/快捷键使用 AppActionRegistry 同一能力门。动作仅包含真实导航/Files/Changes/终端/用量/通知；审查与预览禁用并解释原因。与 composer 斜杠命令独立。 |
| K13/I08 用量 | 部分：UI与契约投影已覆盖 | live `Coordinator.GetUsage()` + 只读历史报告；会话累计/本轮/全部记录标签、分支口径、统计时间、partialTail、不完整小计。null 显示“暂无完整数据”，实测 0 才显示 0；旧会话迟到查询隔离。A8 fixture 检查覆盖；真实会话三范围终验未做，无费用/额度/context 伪统计。 |
| K14/I09 通知 | 部分：页内已覆盖 | 完成/失败/待审批三类，焦点策略，不提醒/失焦/总是与审批开关实际落盘；sessionId+generation+本地 run 去重，完成仅认 TurnSettled，Idle/message_end/Unknown/Interrupted 不发成功。关闭审批撤下需操作标记，点击归属会话路由；连接故障与任务失败不双发。OS 投递、声音、跨重启通知去重未交付。 |
| K15 Caption | 部分：无头状态已覆盖 | BorderOnly + 32 DIP 独立标题行；G 46×32 原比例明暗资产、红底 close-hover，maximize/restore 随窗口状态切换；动作入口与拖动命中层分开，模板不叠第二块 hover 底板。原生鼠标拖动、Win11 Snap、最大化多屏边界、原生禁用态未实机验。 |
| I13 快捷键/焦点 | 已覆盖本波绑定 | Ctrl+K、Ctrl+Shift+P 命令面板；Ctrl+P Files；Ctrl+J dock。前三个在终端聚焦时保留给 shell；能力不足不执行。真实控件 Enter/Esc 路由与焦点返回已检查；快捷键重绑定未提供。 |
| 浮层尺寸/层级 | 已覆盖本波 | 命令/用量/通知共用遮罩和恢复焦点；宽度/高度按窗口限幅，内容独立滚动，Tab 在浮层循环。用量标题不展示内部 scope 标识。 |

### R1/R2 部分项复查

| 原部分项 | 本轮复查结果 |
|---|---|
| R1 五区空间/真实 separator | R2 已有右栏/底栏真实 GridSplitter 与键盘调整；原生拖动/缩放仍未实机验。 |
| R1 左栏状态/尾区 | 运行/失败/窗口内未读已补；32 DIP 行尾菜单位、hover/focus/menuOpen 状态已实现。待操作 glyph、组织/排序/搜索、持久化未读仍缺。 |
| R1 完整会话头 | 本轮收口为标题/项目/单一活动摘要，常态连接/模型/审批/扩展进会话详情。长中文标题保持入口可见、截断；补上原缺失 PanelRight geometry。分支名仍无展示契约。 |
| R1 活动块/Composer | 原用户/助手/思考/工具卡和滚动/IME/steer/follow-up/停止恢复自测保留；附件/语音、队列暂停恢复编辑删除、投递未确认专用 UI 仍缺。 |
| R2 Changes 文件卡 | workspace/session 双口径、new/deleted/binary/unknown、loading/error/empty、完整路径复制已覆盖；真实 Core 查询留协调者终验；patch/hunk/±行数/还原/stage 未交付。 |
| R2 Files/中央联动 | 目录浏览、300 项上限、tab 身份/关闭/重开已覆盖；编辑器、dirty/外改、中央文件→行定位缺内容服务。 |
| R2 宽窄/滚动/焦点 | 800/1200/1600 DIP 身份保持与抽屉、splitter 键盘、关闭焦点、自独立滚动容器已检查；真实双滚动锚点/鼠标拖动/150%-200%缩放/高对比未验。 |
| R2 终端/归属 | 双实例、隐藏重开、关闭释放、滚动隔离、真实 ConPTY cmd 中文输出/resize/exit 已重验；同尺寸隐藏重开保输出。横向缩小无 reflow；完整 TUI/选区复制、真实中文输入法组合、agent 命令与 shell 同时运行截图仍缺。 |

### 全局令牌 U01–U09 / V01

| 条目 | 映射/取舍 | 状态与边界 |
|---|---|---|
| U01 表面 | FluentTokens 明暗中性表面 → App 背景/surface/hover/selected/border；Mica 未启用时实色回退 | 明暗截图已查看；原生 Mica/高对比未验。 |
| U02 排版 | Windows Segoe UI Variable/Segoe UI/中文系统回退；UI/body14、secondary12、section20/page28；代码 Cascadia Mono/Consolas、正文 LineHeight20 | 新增页面沿用；现有 Inter 包/回退保留，未增加第三方字体文件，严格无第三方字体基线调整另议。 |
| U03 触达/状态 | 大部分动作32–36；Caption46×32；会话尾区32；focus/hover/selected/disabled 独立 | Rail38 DIP 是 R1 沿用的触达例外；原生辅助技术/高对比仍未验。 |
| U04 间距/容器 | 4/8/12/16/20/24/32；control radius4、overlay8、composer原8；正文不逐段包卡 | 本波沿用。caption 不拉伸为参考46×44；语义卡与浮层仅单层边框。 |
| U05 语义 | App success/error/warning；Changes 标记保留文字，不仅颜色；unknown不用绿色0 | 明暗与 fixture 状态已看；长正文高对比未验。 |
| U06 浮层/焦点 | 普通卡无阴影；FluentFlyoutShadow 仅浮层，Esc/焦点恢复/禁用原因可读；焦点外2内1 | 本波控件路由已检查；完整键盘/读屏顺序与原生拖动留终验。 |
| U07 动效 | 页面250ms/28DIP/Spline；新全局浮层选用即时开合，常规控件保留 Fluent 默认反馈 | MotionPreference 每次读取 Windows 动画偏好，页面/导航 reduced motion 即时切换；未修改用户系统设置，也未做实际开关演练。参考 opacity100ms/search120ms 未逐控件核实，不称精确同值；既有导航 pill300ms为产品扩展。 |
| U08 组件例值 | 沿用 control4、search5、border1、焦点2/1；菜单/下拉各自padding | 页面/卡片间距按 U04，未把菜单例值推广成全局页边距。 |
| U09 Toast 阴影 | 新增 FluentToastShadow：Light `0 4 16 0 #33000000`，Dark `0 4 18 0 #80000000` | 仅主动通知提示使用；flyout/pill 原有阴影为产品扩展，不给全部卡片套阴影。 |
| V01 双轴取舍 | 沿用五区信息层级；最终明暗/语义色/圆角/焦点用 Fluent，G 原创品牌与状态资产 | 没有搬 Codex 配色/文案/商标，也没有把 UniGetUI 美术资产复制进 App。 |

### H-S01–S09 三档终审（附设置 S10/S11）

三档表示整个场景的覆盖，不因单个静态截图把整场景标为通过。

| 场景 | 档位 | 已有证据；剩余原因/建议归属 |
|---|---|---|
| H-S01 左栏/一般选项 | 部分 | rail/展开栏/设置扩展路由已存在；真实项目 footer 目标沿旧链路。一般选项总菜单/账号目的地未做（D，按产品能力），本波无真实项目菜单截图。 |
| H-S02 组织/排序/搜索 | 未覆盖 | 任务书允许后置；没有支持所需组织/排序/搜索的产品元数据约定（A定义，D后续接线），没有可点假选项。 |
| H-S03 运行/待操作/失败/未读/hover | 部分 | 当前会话状态投影、固定尾区和菜单；窗口内未读已验。待操作 glyph 与历史终态/持久化未读未覆盖（A+D）。 |
| H-S04 头部/展开活动 | 部分 | 紧凑头、长标题、详情、原活动块/展开/滚动链路；缺分支名及真实 Pi 完整运行截图（A提供分支字段，D/B实机）。 |
| H-S05 Composer 高级状态 | 部分 | 基础输入/IME/steer/排队/停止恢复已验；附件/语音、队列暂停/编辑/删除、投递未确认/恢复失败专用呈现未覆盖（A契约+D切片）。 |
| H-S06 右栏各种 tab/联动 | 部分 | Files/Changes/New Tab/关闭/重开；Review/Preview 禁用说明。内容编辑/审查/预览/中央行定位缺服务（A+D）；真实 Changes 终验待协调者。 |
| H-S07 断点/双滚动 | 部分 | 800/1200/1600、独立正文、稳定 tab、键盘分割、偏好限幅；鼠标拖动/真实阅读锚点/缩放高对比未验（D/B实机）。 |
| H-S08 终端生命周期 | 部分 | 双 tab/create/close/hide/reopen、尺寸偏好、真实 cmd ConPTY 已验；实际拖高/IME/TUI/选区复制未覆盖（D后续终端切片与实机）。 |
| H-S09 agent命令+交互shell | 部分 | 项目归属容器与独立 PTY、Pi stdout只进RPC、tool仍中央；真实同时运行截图未验（D/B实机）。 |
| H-S10 设置目的地往返 | 部分 | 应用内常驻设置页、分类/搜索、tab/dock/草稿/焦点身份无头已验；原生标题/边框/Snap及真实 Pi 持续运行未验（D/B实机）。 |
| H-S11 设置窄窗/dirty | 部分 | 真分类/过滤、420/1280页与800主窗、真实草稿、保存四态已验；原生确认对话/中文输入法实际组合仍未实机验（D/B）。 |

### 每区组件总覆盖表

| 区域 | 组件/编号 | 总覆盖与剩余 |
|---|---|---|
| 五区骨架 | L01–L08 | Caption/Rail/Sidebar/中央/右工作tab/底dock/独立设置均落窗；左右/底独立挂点。L03会话头本轮收口；侧栏宽度手调、真实拖动/缩放/高对比未全验。 |
| 左栏 | K01/K02、L01/L07、I10/Z01 | 导航、项目/会话切换、重命名、当前状态、尾菜单；分叉/归档/置顶/组织/排序/搜索未交付，不能把复制ID当这些能力。 |
| 中央 | K03/K04/K06/K07/K12、I01–I05/I12、Z02/Z03 | 用户/助手/思考/工具与既有审批弹窗、基础发送/steer/排队/停止恢复/错误中断；K05/I06/I15中央变更摘要→成果完整流、完整高级Composer仍缺，审批尚未统一嵌入工具上下文。 |
| 右栏 | K09/K10文件、L05/L06、I07/Z05 | Files目录+Changes范围列表和tab容器；真实diff内容/行数/unified/split、编辑dirty/外改、还原/stage、Review/Preview/中央定位未交付。 |
| 底栏 | K10终端、Z04、I12 | Windows ConPTY基础终端、多实例与项目容器已实现；完整VT/TUI/复制/跨平台后端、真实agent并行关系未交付/未验。 |
| 独立设置 | K11/I11、I14 | 真实分类/搜索/控制行、保存四态、dirty与恢复、通知偏好、扩展三态；原生窗口/真实会话运行边界另验。 |
| 全局 | K08/K13/K14/K15、I08/I09/I13 | 统一动作、用量三口径、页内通知、caption；OS通知/声音、快捷键重绑定、caption原生行为未验。 |

### 最终构建、自测与截图证据

- 恢复并完成接线后的最终 `dotnet build --no-restore`（solution）：成功，**0 warning / 0 error**；日志 `tmp/w5r3/build.log`。
- 最终 `dotnet run --project src/Fufucode.App --no-build -- --selftest --conpty`：退出码 0，**284 ok / 0 FAIL**，无 UI 未处理异常或未观察任务异常；日志 `tmp/w5r3/selftest-final.log`。含 R2、设置、扩展三态、跨区全局、主窗口接线与真实 ConPTY 检查；没有启用 `--realpi`，不声称本轮真实 Pi/供应商通过。
- 真实 ConPTY 检查实际通过：cmd 命令输出、Unicode、resize、正常 exit(0)。无头终端截图为 mock 展示，与该独立进程检查区分。
- R2 已重新生成 5 张 PNG：`right-open.png`、`bottom-open.png`、`layout-800.png`、`layout-1200.png`、`layout-1600.png`，均在 `tmp/w5r2/`；右栏与底栏图已实际查看。
- R3 已生成 17 张 PNG（`tmp/w5r3/`）：设置明暗/800 DIP/网络草稿/通知偏好；命令面板明暗；用量明暗；caption close-hover 明暗；通知中心；长标题1200；五区工作面；扩展未安装/无匹配/失败。已查看关键明暗与状态图，关闭 hover 红底、长标题不盖按钮、设置窄行重排、用量 unknown 和失败专用 G 图可见。截图有效不等于用户视觉认可。
- `git diff --check` 无空白错误；Git 提示 LF→CRLF 属工作区行尾规则，不是构建警告。改动仍未提交；最终证据与本节三档清单一起交协调者。

### 后续阻塞与交接清单

- **交 A（服务/契约缺口）**：每文件行数与patch/hunk/快照范围/还原/stage；文件读取编辑/dirty/外改/行定位与审查预览内容；分支展示、历史运行/失败与持久化未读元数据；分叉/归档/置顶/组织/搜索事务；附件/队列编辑暂停恢复与未知投递相关状态。D没有写 Core 或虚构这些能力。
- **D 后续平台服务**：OS通知/声音/跨重启去重；完整终端VT与选区复制、跨平台后端；快捷键重绑定与原生caption/Snap。无新依赖未经审核接入。
- **协调者/B 真实终验**：真实会话 Changes/usage、Pi运行中设置往返、agent命令与交互shell同时运行、窗口拖动/分割、IME、100%/150%/200%缩放/高对比。这些尚未验证，不能用mock全绿替代。
- R2→R3→五区终审已在本进度文件连续收口；工作区保留全部未提交改动，供协调者代验代提。

## D6 前置：E1 右栏 Preview tab 点亮（1a+1b 完成并实机验证；1c 因文件边界未做）

**目标达成情况**：派单的目标是"右栏 Preview tab 可见、E1 的 BrowserView 能真正点亮（此前 E-seam 因无 Preview tab 而永久折叠）"——**已由 1a+1b 达成并实机取证**。1c（选中文件推给预览面）未做，原因是它必然落在派单边界之外的文件，见末段。

### 已改动（仅 `WorkspacePanelViewModel.cs` + `WorkspacePanelView.axaml`，未碰任何 Browser* 文件）

- `WorkspacePanelKind` 增 `Preview`；tab `Title` 改 switch 三态。
- `RecreateTabs` 追加 Preview tab，**PanelId 用 E 侧常量 `BrowserPreviewSeam.PreviewPanelIdSuffix` 拼接而非字面量 `"|preview"`**——seam 的可见性判定完全依赖这个字符串，两处必须同源，写死字面量会在 E 侧改约定时静默失效。
- Preview tab 以 `isOpen: false` 创建（tab 条按 `IsOpen` 控可见），避免凭空多出一个常驻标签；由 `+` 飞出菜单的 `OpenPreviewCommand` 打开。为此给 `WorkspacePanelTabViewModel` 构造加了默认 `isOpen = true` 的可选参数，Files/Changes 行为不变。
- 新增 `IsPreviewSelected`（并挂进 `SelectedTab` 的 `NotifyPropertyChangedFor`）与 `OpenPreviewCommand`。
- View：`Preview · 预览` 按钮去掉 `IsEnabled="False"` 改绑 `OpenPreviewCommand`；删除「预览内容提供者待接入」提示行；更新已过期的 E-seam 注释（原文写"当前尚无 Preview tab，故保持折叠"）。

### 验证（实际运行）

- `dotnet build Fufucode.sln` → **0 警告 0 错误**；`dotnet run -- --selftest` → **0 项失败**（新增第三个 tab 未破坏 R2 的 `Tabs.Single(t => t.Kind == Changes)` 等既有用例）。
- **实机 UIA 取证**（`cua-driver`，`launch_app` 隐藏启动 `active:false`，全程后台投递未抢前台）：打开工作面板 → 点 `+` → 点 `Preview · 预览` 后，UIA 树出现 `浏览器地址栏`/`浏览器导航`/`浏览器刷新`/`浏览器打开文件` 四个元素，tab 条变为 Files/Changes/Preview；截图 `tmp/w5r4/e1-preview-lit.png` 显示地址栏 `about:blank`、状态"自由导航 · 加载完成"、WebView 区域已渲染。**即 E1 的 BrowserView 确实在 D 的右栏里点亮，不是仅编译通过。**
- 收尾：用应用自身「关闭」按钮退出，`list_windows(pid)` 返回 0 个窗口。

### 1c 未做的原因（需协调者裁决，非技术障碍）

派单要求"沿用 `IClipboardService` 的构造注入模式，在 `WorkspacePanelViewModel` 注入 `BrowserViewModel` 实例，并在 SelectedFile 变更/双击时调用 `PreviewFile`"。实测对象图与此不符：

- `BrowserViewModel` 实例由 **`BrowserView` 控件在自己的构造函数里 `new` 并持有**（`private readonly BrowserViewModel _viewModel = new(); DataContext = _viewModel;`，注释明确"自带 BrowserViewModel，不继承外层 DataContext"），对外只暴露 `public BrowserViewModel ViewModel` 与 `public void PreviewFile(string? path)`。
- 因此 D 侧要拿到**同一个**实例，只能从视图层取 `browserView.ViewModel`。可选落点三处：① `WorkspacePanelView.axaml.cs`（D 所有的 28 行 code-behind，已有 `BrowseFile_Click`/`SelectTab_Click` 同类接线）；② 组合根 `MainWindow.Workspace.cs`；③ 改 `BrowserView`（E 轨文件，派单明令禁止）。
- ①都不在本次"只改两个文件"的边界内，纯 XAML 无法调用方法，故未擅自越界，也未写入无人调用的 `AttachPreviewSurface` 之类占位 API（那会是死代码 + 半成品）。

**建议**：批准把 `src/Fufucode.App/Views/WorkspacePanelView.axaml.cs` 纳入边界，最小实现约 3 行——给 `<views:BrowserView>` 加 `x:Name`，在该文件里订阅面板的选中变更/双击并调用 `previewBrowser.PreviewFile(file.FullPath)`；VM 侧无需改动。当前 E1 预览面自带「打开」「文件…」入口，故未接也仍可独立使用，不阻塞 E 轨验收。

## D6 主任务：窄宽重叠修复 + PreviewFile 接线（2026-10-03/04）

执行者：用户派发的外部 AI（线程无关，任务书 docs/archive/assignments/track-d-six.md）；执行者落码后中断，驱动器补验证+修编译+补本记录。

### 交付（worktree 实测）

- **D-R4-1 修复**：`ChatPage.axaml(.cs)`/`MainWindow.Workspace.cs` 落避让带度量——1440 并排无边距、1024 窄宽抽屉模式下面板 ≥360、抽屉顶不压会话头动作区、底不压 composer、避让带内 ≥200 DIP 可用高度、遮罩同步避让且页头/输入区可点。新增常驻自测 `SelfTest/Wave5D6SelfTest.cs`（17 项断言全部进 selftest 常驻）。
- **PreviewFile 接线**（按已批边界，仅 `WorkspacePanelView.axaml.cs`）：FilesList SelectionChanged → `ArmPreview`（选中非目录即把路径推给 `BrowserView.TryPreviewFile`，按 x:Name=PreviewBrowser 契约定位，右栏收起安全返回）；`BrowseFile_Click` 对已选中文件再点/双击第二下 = `OpenPreviewCommand` 打开 Preview tab——同一语义单入口，不另挂 DoubleTapped。不抢 tab 身份（guideline §3.4）。
- 截图三态：`tmp/w5d6/01-wide-1440.png`、`02-narrow-1024-drawer.png`、`03-narrow-1024-drawer-terminal.png`。

### 驱动器修复与验证

- 执行者代码两处缺 using（`Avalonia`→Visual、`System`→StringComparison）编译失败，驱动器补齐（仅 using 行，无逻辑改动）。
- 驱动器实测：`dotnet build Fufucode.sln` 0 警告 0 错误；`--selftest` 0 失败，其中 `W5-D6` 断言 17/17 过（窄宽三态+抽屉约束+遮罩可点+最紧态）。
- 未验（如实）：真实桌面缩放 100%/150%/200% 下的 D-R4-1 行为、真实 WebView2 预览渲染（selftest 为无头）——归用户装包体验或 B 轨终验。

### 交接状态

- D 侧管线（R1-R4+D6）至此**全部完成**；「交 A 清单」「平台服务」「真实终验」三张遗留清单见上文 R3 节，仍有效。
- 本节由协调者代写（执行者中断未记录），改动内容以 worktree diff 为准。
