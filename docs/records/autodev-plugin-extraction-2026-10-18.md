# 自动开发整包拆入插件（2026-10-18）

## 范围

把自动开发的执行内核、工作台页面、持久化草稿/请求日志、后台宿主与 CLI 命令整体迁入
`src/Fufucode.Plugins.AutoDev`。App/Core 只保留宿主契约与接缝，不再静态引用可卸载实现。

### 迁入插件

| 目录 | 内容 |
| --- | --- |
| `Engine/` | `AutomationStore` 及 Control/Conv/Harnesses/Hosting 全部实现（原 `Core/Automation`） |
| `Ui/Automation/` | 页面、视图模型、Persistence 草稿模块（原 `App/Features/Automation`） |
| `Cli/` | `control` / `host` 命令实现（原 `Fucucode.Cli` 内） |
| `AutoDevExecutionOwner` | 宿主审查/工具请求的唯一执行所有者 |
| `AutoDevCommandHost` | 宿主 CLI 的命令入口（清单 `commandHostType`） |

### 宿主侧接缝

- `App/Abstractions/Automation/Harnesses/HarnessContracts.cs`：运行环境、请求/结果、适配器身份与
  `HarnessAdapters` 登记点。具体协议适配器随插件发布，插件加载时登记、卸载时注销。
- `App/Abstractions/Automation/ExecutionBaseLibrary.cs`：`IExecutionOwner` 与按所有者登记的执行入口。
  本地审查和 CLI 只从这里请求执行；未安装时返回真实安装原因，不内置第二份执行器。
- `App/Abstractions/Automation/IAutomationPageController.cs`：宿主对插件页面的最小控制面，
  以及 `IAutomationCommandHost`。
- `Core/Plugins/IPluginPackage.cs`：页面贡献增加 `OnProjectChanged`，插件上下文增加 `DataRoot`。
- `PluginPackageManager`：收集页面贡献并广播 `PagesChanged`，停用/卸载时移除。
- `App/Features/Plugins/AutomationPageHost.cs`：宿主保留页签与停靠身份，安装后挂载插件视图，
  未安装时显示真实安装指引。
- `MainWindow.Workspace/BrowserSettings`：从插件管理器取页面贡献并挂载；不再 `new` 页面/视图模型。
- `LocalReviewHarnessExecutor`：改为向 `ExecutionBaseLibrary` 请求一次执行，按插件返回的真实终态分类。
- CLI `AutomationPluginCommands`：按清单 `commandHostType` 加载已安装且启用的插件；只加载命令入口，
  不触碰页面/Avalonia 类型。

## 未改动的策略

插件市场的 `DefaultInstalled/DefaultEnabled` 仍为 `false`：默认安装/捆绑是独立产品决定，
本次只做架构拆包。未安装时页面显示安装指引，不伪造工作台。

## 验证

- `dotnet build Fufucode.sln -c Debug`：36 个项目通过。
- `tests/Fufucode.Control.Tests`：538 项通过（CLI 子进程使用隔离数据根与真实插件载荷）。
- `tests/Fufucode.Conv.Tests`：73 项通过；`Fufucode.Harness.Tests`：51 项通过；`Fufucode.Automation.Tests`：全部通过。
- `tests/Fufucode.LocalReview.Tests`：86 项通过；反例 `--ce` 全部通过；UI `--ui` 79 项通过。
- `tests/Fufucode.App.Automation.Persistence.Tests`：192 项通过；`Fufucode.App.Automation.V4.Tests`
  除既有的模型默认值断言外通过。
- `tests/Fufucode.App.Automation.SelfTests`：插件页面已挂载；其余为迁移前即存在的 3 项
  焦点/文本绑定断言失败（主检出同样失败）。
- 独立探针（不引用插件程序集）：把插件 DLL + manifest 安装到隔离数据根，宿主经可回收
  `PackageLoadContext` 从物理载荷加载，页面贡献与 XAML 成功挂载（`Controller` 非空）。

## 基线说明

提交的 `main` 中 `WorkbenchStyles.axaml` 已引用 `LiveMarkdown.Avalonia`，但包引用当时仍在并发工作区
未提交，导致 XAML 编译静默失败、运行期“无预编译 XAML”。本分支补齐了该包引用（仅构建必需）。
