# 意见模式移除、命名统一与核心功能重构（2026-10-07）

承接 `docs/records/visual-unification-2026-10-07-b.md`。三项用户要求：移除意见模式（测试用功能）、统一侧边栏命名、重构核心功能。

## 一、移除意见模式（前端全部相关代码）

删除的文件：

- `Views/MainWindow.Opinions.cs`（意见模式开关、全窗口点击捕获、右键菜单注入、意见编辑弹窗、导入导出）
- `Infrastructure/UiOpinionStore.cs`（`ui-opinions.json` 读写）

清理的引用：

| 位置 | 处理 |
|---|---|
| `MainWindow.Actions.cs` | 删除 `opinions.mode` / `opinions.export` / `opinions.import` 三个动作注册 |
| `WorkbenchActionDefaults.cs` | 删除三个动作的默认位置与图标 |
| `MainWindow.Customization.cs` | 移除 `InitializeOpinions()` 调用、意见模式按钮高亮、意见计数标题、溢出保底项 |
| `MainWindow.Global.cs` | Esc 返回不再判断 `_opinionMode` |
| `MainWindow.Fork.cs`、`SettingsNavigation.cs`、`ChatPage.Composer.cs`、`MainWindow.Actions.cs`、`WorkbenchSectionsView.cs` | 移除 `RegisterOpinionSurface` 调用 |

自测调整：`WorkbenchCustomizationSelfTest` 原先依赖意见模式验证"左键不执行原操作""右键追加意见项""消息头像/正文各有独立意见目标"。这些能力随功能一并移除，相关断言替换为：

- 断言不再注册任何 `opinions.*` 动作；
- 断言普通点击恢复执行自身动作（点设置按钮会进入设置页）；
- 断言右键菜单不再出现 `ui-opinion` 项；
- 断言消息头像与正文保留稳定 AutomationId。

保留的两条断言含 "opinion" 字样，但断言的是"不存在"，不是放宽。

## 二、命名统一

侧边栏打开的菜单称**核心功能**，其余称**辅助功能**。

- 界面提示已是「核心功能」（侧边栏入口）与「辅助功能」（顶栏溢出菜单），本次保持一致。
- 设置页「功能位置」下拉里的位置标题同步改为「核心功能（侧边栏）」与「辅助功能（顶栏菜单）」，两处用词不再分裂。

## 三、核心功能重构

### 移除引用草稿
- 删除 `ask` 页面（`Add("ask", ...)`、`MainWindow.Global.cs` 的动作注册、`_sideReference` / `_sideQuestion` / `_sideDrafts` 字段与草稿保存逻辑）。
- 消息操作里原来的「加入引用草稿」按钮改为「在侧边聊天中提问」，内容仍经显式操作带到侧边聊天，不会自动注入主会话。
- `overview` / `ask` / `outline` 从 `KnownPages` 移除；`ask` 与 `outline` 不再占侧栏位置。

### 文件预览作为侧边栏底层功能，默认能用
- `preview` 本来就是 `RegisterCore`（非可选模块），不依赖浏览器模块开关，默认在右侧可用；本次未改变其注册方式，只是移除与之重复的旧入口，并把它在动作目录里的说明改为"预览所选文件内容"。
- 自测新增断言：文件预览是无模块依赖的核心页，可直接打开到右停靠区。

### 会话预览改版为会话管理
- 原 `overview`（概览）改名为**会话管理**，仍是左停靠区默认页。
- 页面新增每条会话的管理操作：重命名、置顶/取消置顶、归档（悬停或聚焦时显示的尾部按钮，沿用会话尾操作样式）。
- 新增 `Views/MainWindow.SessionManagement.cs` 实现 `ISessionManagement`：重命名、置顶、归档都作用于**列表里选中的那条会话**，不是只作用于当前会话。
  - 置顶/归档直接写 `SessionPresentation` 元数据；
  - 重命名因 Pi 的 `set_session_name` 只作用于当前会话，实现上先切换到目标会话再改名（真实模式下）。
- `SessionItemViewModel` 增加 `IsPinned` 与三个 `ICommand`，由 `SidebarViewModel.AttachSessionManagement` 注入。未注入时页面只显示列表，不出现无效按钮。
- `view.navigation` 动作改为显示/隐藏会话管理。

## 验证

| 项 | 结果 |
|---|---|
| 构建 | 0 错误 0 警告 |
| `--selftest --r05-only` | 60 项通过 0 失败 |
| `--selftest --tabs-only` | 47 项通过 0 失败 |
| `--selftest --settings-destination-only` | 20 项通过 0 失败 |

## 边界

- 归档、置顶为本地元数据，不改动 Pi 会话文件；重命名经 `set_session_name` 落盘。
- 会话管理的操作目标＝列表选中项；若选中的是当前会话，行为与原来的会话级动作一致。
- 未做：与 UniGetUI 的视觉逐像素对照、实机输入法与合成器效果。截图证据已产出但未经逐像素核对。
