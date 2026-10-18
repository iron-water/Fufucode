# DESK-1 交付记录：桌面端 WorkBuddy 皮肤重构

日期：2026-10-15
任务：DESK-1（Avalonia 桌面端吸收 WorkBuddy 官方视觉规范）
工作树：C:/Projects/Fucode/worktrees/desk1-workbuddy-ui
分支：agent/desk1-ui（起点 bce1551；main 当时为 4970eaf，仅多一条派单记录）
状态：submitted（模块已落地，等主控整合）

---

## 一、用户原始诉求与对应落点

| 用户原话 | 落点 |
|---|---|
| 还有好多 windows fluent 的残余，顶栏、底栏、图标、分划线 | WorkBuddyStyles.axaml 把 chromeBar / dockHost / dockHeaderRule / dockSplitter 的分划线与描边全部归零；图标整体换成官方原生 SVG 几何 |
| 侧边栏里 tab 的 X 太大，滑动不方便，不对齐 | DockRegionView 关闭按钮 24→16、行位 28→22 列宽、图标 10；页签水平步进 48→72；dockTab 内边距与圆角统一 |
| 到处堆砌文字说明都是差的设计，藏一藏 | 皮肤不新增任何内联长文案；设置页长说明沿用既有 settingsHelp / ToolTip 折叠，SkinPresentation 的预算不变 |
| 主页面按住顶栏没法拖动 | MainWindow.Chrome.cs：命中分类器重写 + TopLevel.GetTopLevel(this) as Window 后 BeginMoveDrag |
| 浮动页签拖动卡顿 | DockTabButton.OnPointerMoved 起拖阈值 8→5，并加 3px 位移合并，压掉拖拽中的无谓重布局 |
| 最底部极扁小栏默认关闭不显示 | 记录默认已是 ShowStatusBar=false，并加断言锁死；旧 ui.json 缺该键时走默认关闭 |

---

## 二、实际改动

### 新增

- `src/Fufucode.App/Styles/Skins/WorkBuddy.axaml`
  TDesign 品牌蓝 `#0052D9`、沉浸暗底 `#18181B` / `#1C1C1E`、卡片 `#242426` / `#2C2C2E`、
  卡片描边 `rgba(255,255,255,0.08)`（亮色 `rgba(0,0,0,0.06)`）、危险色 `#FF3B30` / `#FF453A`；
  圆角 8 输入 / 12 卡片 / 16 浮层 / 胶囊全圆。值全部来自 `docs/design/workbuddy-mobile-design-spec.md`。
- `src/Fufucode.App/Styles/Skins/WorkBuddyStyles.axaml`
  只作用于 SkinLease 挂载子树的外壳与控件样式：chrome / dock 去分划线、按钮与页签圆角族、
  16dp 轻量页签关闭按钮、列表与导航的蓝色选中态、卡片大圆角 + 细描边、焦点环。
- `src/Fufucode.App/Features/Skins/WorkBuddyIconSet.cs`
  把 `assets/reference/workbuddy/icons/` 的官方原生圆润 SVG 折成单条 Avalonia 路径（20/24 网格，圆头圆角 1.5），
  覆盖 Menu / Dismiss / ChevronX / Add / NewChat / History / Person / Search / Settings / Folder / Copy /
  Refresh / Send / RecordStop / ShieldCheckmark / Info / Warning / PanelLeft / PanelRight 等 56 个键。
- `src/Fufucode.App/SelfTest/WorkBuddySkinSelfTest.cs` 与 `--workbuddy-only` 入口：DESK-1 定向验收。

### 修改

- `SkinCatalog.cs`：新增 `SkinId.WorkBuddy`（存储键 `workbuddy`，别名 `wb` / `work-buddy`），插到 Fluent 之后。
- `SkinLease.cs`：WorkBuddy 皮肤改用 `WorkBuddyIconSet`。
- `MainWindow.Chrome.cs`：重写顶栏拖动命中分类器与 `BeginMoveDrag` 取窗方式。
- `DockRegionView.cs`：页签关闭按钮 16、列宽 22、起拖阈值 5、位移合并 3px、水平步进 72。
- `HeadlessUiRunner.cs`：预览支持 `--skin` / `--theme`，并把种子写进 `SelfTestDataRoot`，使真实渲染截图能对准指定皮肤。
- `SkinIntegrationSelfTest.cs`：同步被本次有意改变的期望值（关闭按钮 16、皮肤选择器索引）。

---

## 三、执行过的验证

| 命令 | 结果 |
|---|---|
| `dotnet build Fufucode.sln -v q` | 0 警告 0 错误（18.4s） |
| `dotnet build src/Fufucode.App/Fufucode.App.csproj -v q` | 0 警告 0 错误 |
| `dotnet run --project src/Fufucode.App -- --selftest --workbuddy-only` | WorkBuddy failures: 0 |
| `dotnet run --project src/Fufucode.App -- --selftest`（全量） | == 自测结束：0 项失败 == |
| `--headless-ui screenshot ... --skin workbuddy --theme dark` | 1440×900 Skia 真实渲染 PNG |
| 同上 `--theme light`、`--page settings` | 亮色与设置页同样落图 |

### 真实渲染证据（Skia 光栅，非静态文字）

| 产物 | SHA256 |
|---|---|
| `tmp/desk1/workbuddy-chat-dark.png` | `E267C1E413E4EA62F26E5908F2B6C2ED679916AB9D932BF780B83DDD1DA52EE4` |
| `tmp/desk1/workbuddy-chat-light.png` | `7598953AF62D54F745F2ED082C4F81C93EB650CAB892CD816AAE2D8F2980230D` |
| `tmp/desk1/workbuddy-settings-dark.png` | `3DFFEB4A829987DBDC4A430BCBA8E1F6C037D390E4EC37EED3A6CB9D32A7C6CA` |
| `tmp/desk1/tabs-zoom.png`（页签 2× 放大） | `9C4501C946D0D26DA7E73FEC4F4E8DD1619E1E2EA0AB5531C6B6F299DC955BA8` |

渲染观察到的实际差异：暗色页面为 `#18181B`、页签底边与「发送」按钮为品牌蓝、卡片为大圆角细描边、
壳层无 1px 分划线；亮色页面为 `#F2F2F7` 且同样无分划线；设置页皮肤选择器显示 `WorkBuddy` 且长说明已折叠。

---

## 四、过程中发现并修掉的一个真实缺陷

`WorkBuddyIconSet` 里 `Icon.Chat` 的官方路径含 Avalonia 解析器拒绝的数字写法，
使 `WorkBuddyIconSet.Create` 在 `SkinLease.Apply` 抛 `InvalidDataException`。
因为图标集在资源字典挂载之前构建，异常会让整个皮肤租约回滚，
表现为「选了 WorkBuddy 却仍是 Fluent」。已改写为等价显式坐标并加逐条几何解析断言。

配套提醒：任何皮肤图标集只要有一条坏路径，都会静默退化成 Fluent。本次测试已把这条守住。

---

## 五、未执行 / 未验证

- 未做真实鼠标拖动顶栏的原生输入验收（需交互式桌面会话；本机只有无头路径，BeginMoveDrag 真实手感未跑）。
- 未做触摸板两指横滑页签的原生验收（只验证滚轮路径与代码里的步进）。
- 未做浮动页签拖动的原生帧率测量（只做了位移合并的代码级优化与回归，无 FPS 数据）。
- 未在真实 %LOCALAPPDATA%/Fufucode/ui.json 上复现用户「状态栏显示」现场；该文件当前无 showStatusBar 键，因此走默认关闭，用户现场很可能来自更早的构建。
- 未做 workbuddy 皮肤下全部功能页的逐页回归（浏览器 / 终端 / 插件 / 资料库等）；只跑了 chat 与 settings 两页渲染。
- assets/reference/ 在工作树中不存在（被 .gitignore 的 reference/ 规则排除）；图标几何在编写时读了主区的同一份未跟踪资产，结论一致，但工作树本身无法重现该目录。

---

## 六、公共接线需求

1. 皮肤选择器文案与顺序已由 SkinCatalog.All 驱动，主控把 WorkBuddy 排在 Fluent 之后即可；本次即采用该位置，既有「索引 1/2」断言已随之更新。
2. WorkBuddyIconSet 未注册进 SkinIconSet 的默认表，改由 SkinLease 按 SkinId 分派；若主控要统一入口，可把分派收敛进 SkinIconSet.Create。
3. 默认皮肤仍是 fluent；要让 WorkBuddy 成为出厂默认，需主控改 SkinCatalog.DefaultStorageKey 与 WorkBuddyAppearanceStore 的初始值。本次没有动默认值，避免越权改变产品行为。

---

## 七、提交与停写

- 提交：本次单一提交（见最终答复）。
- 工作树未提交项：仅 tmp/desk1/*.png 截图证据（tmp/ 被忽略，不入库）。
- 本人临时进程：无残留（测试进程均已退出）。
- writerStopped: true
