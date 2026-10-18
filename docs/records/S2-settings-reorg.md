# S2-SETTINGS 设置面板三级架构重构与侧栏条目治理（D2 + D3）

- 任务 ID：`S2-SETTINGS`（难度 L3）
- 状态：**模块交付**（代码 + 独立测试通过）；外壳接线未做，按任务书归主控 M 统一收编
- 起点 HEAD：`043fd2c37a3c7f817781009f4b881f9513efeb1d`
- 代码提交：`d17073f3de60cd29c30af089d3db6b52a71d3b07`
- 交付 HEAD：本记录所在提交（SHA 见交接卡 `deliveryHead`）
- 工作树：`C:/Projects/Fucode/worktrees/s2-settings`，分支 `agent/s2-settings`
- 允许路径：`src/Fufucode.App/{Views/SettingsPage*,Views/WorkbenchSectionsView*,ViewModels/SettingsPageViewModel*,Features/SettingsExperience/**}`、`tests/Fufucode.App.Settings.Tests/**`、`docs/records/S2-settings-reorg.md`

## 1. 改动

### 1.1 新增纯逻辑模块 `src/Fufucode.App/Features/SettingsExperience/`

| 文件 | 职责 |
|---|---|
| `SettingsNavCatalog.cs` | 侧栏唯一真源：`SettingsNavTier`（Basic/Features/Other）+ 15 条 `SettingsNavDefinition`（id/标题/层级/搜索词/图标/配色/`HasContent`），一级顺序 `基础类 → 大功能 → 其他` |
| `SettingsNavPreference.cs` | 升降级偏好 `SettingsNavPreference`（`Promoted` / `Demoted`）+ `Toggle` / `EffectiveTier` / `Normalize` / `CanMove`；`SettingsNavPreferenceStore` 原子落盘 `<dataRoot>/settings-nav.json`，损坏文件不覆盖 |
| `SettingsNavLayout.cs` | 目录 + 偏好 → 分组（升级项并入「大功能」，降级项并入「其他」，空组隐藏）；`Matches` 提供跨层级搜索 |
| `SettingsRelatedSettings.cs` | 相关设置关系图（双向），`For(pageId)` 解析卡片条目 |
| `SettingsPreviewProtocol.cs` | 「文件预览」页签内嵌预览能力协议（接受 text/markdown/image/diff，`Wired:false`） |

三级归属：

- 基础类：外观、关于、网络与端点、键盘快捷键、模型与思考、储存
- 大功能：浏览器、终端、文件管理、自动开发
- 其他（默认收纳二级小功能）：通知、配置文件、用量、工作台模块、会话与消息

### 1.2 `src/Fufucode.App/ViewModels/SettingsPageViewModel.cs`

- `SettingsCategoryViewModel` 增加 `Tier`（生效层级）/`OriginTier`（目录层级）/`IsGroupStart`/`GroupTitle`，并派生 `CanMoveTier`、`TierActionLabel`、`TierActionTooltip`、`TierActionToFeatures`。
- `Categories` 改为 `ObservableCollection`，由目录 + 偏好重建（不再内联硬编码）。
- `UpdateCategoryFilter` 改走 `SettingsNavLayout.Build`：同一关键词同时过滤三个一级分类，组首项带一级分类标题；`SelectedCategory` 按 Id 保留。
- `Attach` 时调用 `AttachNavPreference(context.DataRoot)` 载入偏好；新增 `ShowCategoryPlaceholder`。

### 1.3 新增 `src/Fufucode.App/ViewModels/SettingsPageViewModel.Nav.cs`

`AttachNavPreference`、`SelectCategory(id)`、`ToggleNavTierCommand`、`OpenRelatedCommand`、`RelatedLinks`/`HasRelatedSettings`、`NavigationRebuilt` 事件。

### 1.4 视图

- `Views/SettingsPage.axaml`：侧栏条目模板加一级分类标题（仅组首项显示）与升降级按钮（基础类隐藏）；内容区新增「该设置页尚未接线」占位卡与底部「相关设置」卡片；新增 3 条局部样式。
- `Views/SettingsPage.axaml.cs`：侧栏选中改为**代码后置驱动**（`SelectionChanged` → `SelectCategory`；`NavigationRebuilt`/`SelectedCategory` 变化 → 按 Id 回填选中）。原因：搜索与升降级会重建条目实例，原来的 `SelectedItem` 双向绑定会把选中写回 `null`。两个 ListBox 仍保留原 `x:Name`，`UpdateResponsiveLayout` 行为不变。
- `Views/WorkbenchSectionsView.cs`：前端功能排布页补一行「文件预览协议」说明。

**未触碰** `MainWindow.Workspace.cs` 及任何外壳接线（按任务书归主控 M）。

## 2. 验证

| 项目 | 命令 | 结果 |
|---|---|---|
| App 构建 | `dotnet build src/Fufucode.App/Fufucode.App.csproj` | **0 警告 0 错误**（含 Avalonia 编译绑定校验） |
| 测试构建 | `dotnet build tests/Fufucode.App.Settings.Tests/Fufucode.App.Settings.Tests.csproj` | **0 警告 0 错误** |
| 独立测试 | `dotnet run --project tests/Fufucode.App.Settings.Tests/Fufucode.App.Settings.Tests.csproj` | **48/48 断言通过** |

测试覆盖（`tests/Fufucode.App.Settings.Tests/Program.cs`）：

1. 三级架构分类与层级解析（三个一级分类的条目归属、标题、默认分类、浏览器契约不回归、`HasContent`）。
2. 升降级偏好持久化：升级/降级写入、生效层级、基础类不可移动、规范化丢弃跨层级/重复/未知项、`settings-nav.json` 落盘与重新加载恢复、VM 命令写盘后新实例恢复同一层级。
3. 相关设置跳转流转：关系双向、目标可解析、卡片跟随当前分类、跳转分类首页、下钻二级页、搜索态跳转先清空关键词。
4. 跨层级搜索：分别命中基础类/大功能/其他、空组隐藏、无命中空状态、组标题保留、清空搜索恢复完整三级侧栏。

## 3. 未完成项与接线需求（交主控 M）

1. **终端 / 文件管理 / 自动开发 / 用量** 四个分类已在目录登记但**内容页未接线**：当前显示「该设置页尚未接线」占位卡。M 接线后把 `SettingsNavCatalog` 对应条目的 `HasContent` 改为 `true` 即可隐藏占位。
2. **侧栏升降级偏好文件**为 `<dataRoot>/settings-nav.json`（新文件，不改 `settings.json`/`ui.json` schema）。若 M 希望并入既有配置真源，需要 `AppSettings`/`WorkbenchAppearance` 放开字段（不在本任务允许路径内）。
3. **文件预览协议**仅声明能力与状态（`Wired:false`），真实预览面接线待 M 收编。
4. **未运行** App 全量 1480 断言自测与原生挂机测试（按任务书 v3 战略延后）。`SettingsDestinationSelfTest`、`SkinIntegrationSelfTest` 等既有自测对 `Categories`/`FilteredCategories` 的既有断言已人工核对未破坏，但未实机执行。
5. 侧栏升降级按钮的**真实指针点击 / UIA 取证**未做（需交互式桌面）。

## 4. 本任务交付卡

> `head` = 代码与测试交付提交；最终提交（本卡所在提交，仅追加本卡、无源码改动）见交接卡 `deliveryHead`。

```json
{
  "schemaVersion": 1,
  "type": "delivery.card",
  "taskId": "S2-SETTINGS",
  "runId": "S2-SETTINGS-R1",
  "deliveryRevision": 1,
  "workerThreadId": "01a13b97-cd98-7342-981c-456a6ba337ae",
  "managerThreadId": "01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07",
  "baseSha": "043fd2c37a3c7f817781009f4b881f9513efeb1d",
  "head": "3bc113724769f7d26dfda5e9aaf673fb7dd9f787",
  "eventId": "S2-SETTINGS/01a13b97-cd98-7342-981c-456a6ba337ae/S2-SETTINGS-R1/1/3bc113724769f7d26dfda5e9aaf673fb7dd9f787",
  "status": "module_ready",
  "summary": "设置左侧栏三级架构（基础类/大功能/其他）与交互式升降级（偏好落盘 settings-nav.json）、相关设置卡片跳转、跨层级搜索、文件预览能力协议均已实现并有独立测试；终端/文件管理/自动开发/用量四个分类已登记但内容页待主控 M 接线，外壳未改。",
  "commits": [
    "d17073f3de60cd29c30af089d3db6b52a71d3b07",
    "3bc113724769f7d26dfda5e9aaf673fb7dd9f787"
  ],
  "changedFiles": [
    "src/Fufucode.App/Features/SettingsExperience/SettingsNavCatalog.cs",
    "src/Fufucode.App/Features/SettingsExperience/SettingsNavLayout.cs",
    "src/Fufucode.App/Features/SettingsExperience/SettingsNavPreference.cs",
    "src/Fufucode.App/Features/SettingsExperience/SettingsPreviewProtocol.cs",
    "src/Fufucode.App/Features/SettingsExperience/SettingsRelatedSettings.cs",
    "src/Fufucode.App/ViewModels/SettingsPageViewModel.cs",
    "src/Fufucode.App/ViewModels/SettingsPageViewModel.Nav.cs",
    "src/Fufucode.App/Views/SettingsPage.axaml",
    "src/Fufucode.App/Views/SettingsPage.axaml.cs",
    "src/Fufucode.App/Views/WorkbenchSectionsView.cs",
    "tests/Fufucode.App.Settings.Tests/Fufucode.App.Settings.Tests.csproj",
    "tests/Fufucode.App.Settings.Tests/Program.cs",
    "docs/records/S2-settings-reorg.md"
  ],
  "verification": {
    "passed": [
      "dotnet build src/Fufucode.App/Fufucode.App.csproj → 0 警告 0 错误",
      "dotnet build tests/Fufucode.App.Settings.Tests/Fufucode.App.Settings.Tests.csproj → 0 警告 0 错误",
      "dotnet run --project tests/Fufucode.App.Settings.Tests → 全部通过：48 项断言"
    ],
    "failed": [],
    "notRun": [
      "App 全量 --selftest（1480 断言）与原生挂机测试：按任务书 v3 战略延后，未运行",
      "既有 SettingsDestinationSelfTest / SkinIntegrationSelfTest / Wave5R3SelfTest / WorkbenchDemoSelfTest 对 Categories/FilteredCategories 的断言：已人工核对未破坏，未实机复跑",
      "侧栏升降级按钮的真实指针点击与 UIA 取证：无交互式桌面，未运行"
    ]
  },
  "evidence": [
    "C:/Projects/Fucode/worktrees/s2-settings/docs/records/S2-settings-reorg.md（改动、验证、未完成项与接线需求）",
    "tests/Fufucode.App.Settings.Tests/Program.cs（48 项断言的独立入口）",
    "git log d17073f3de60cd29c30af089d3db6b52a71d3b07 / 3bc113724769f7d26dfda5e9aaf673fb7dd9f787"
  ],
  "integrationNeeds": [
    "主控 M 在 MainWindow.Workspace.cs 侧栏接线中消费新的三级分类（基础类/大功能/其他）与升降级偏好文件 <dataRoot>/settings-nav.json",
    "M 接线终端/文件管理/自动开发/用量四个分类的内容页后，把 SettingsNavCatalog 对应条目的 HasContent 改为 true 以隐藏占位卡",
    "M 完成文件预览面接线后把 SettingsPreviewProtocol.Current.Wired 置为 true",
    "若要把侧栏偏好并入既有配置真源（settings.json/ui.json），需放开 Core/Infrastructure 字段（不在本包允许路径内）"
  ],
  "blockers": [],
  "writerStopped": true,
  "worktree": "C:/Projects/Fucode/worktrees/s2-settings",
  "branch": "agent/s2-settings"
}
```
