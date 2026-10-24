# Companion Core + 两个连接插件（2026-10-24）

状态：实现完成，构建与既有测试通过；未做真实设备/桌宠联调。
工作路径：`C:\Projects\Fucode\Fufucode`，分支 `main`，未提交。

## 范围

按机主决定：

- `service.remote-control` → “Jiii 连接”
- `service.fufu-bridge` → “Fufu 连接”
- 两者实现 `ICompanionConnector`
- 协同核心放宿主服务

插件 ID 保持不变，避免插件状态迁移和双传输。

## 新增

| 文件 | 内容 |
|---|---|
| `src/Fufucode.App/Abstractions/CompanionContracts.cs` | `CanonicalFact/Event/Intent/Receipt`、`ICompanionCore`、`ICompanionConnector`、`ICompanionIntentSink`、连接器状态与能力 |
| `src/Fufucode.Core/Plugins/CompanionCore.cs` | 核心实现：注册、事件去重与扇出、意图幂等、有界审计、每连接器计数 |
| `src/Fufucode.Plugins.RemoteControl/JiiiConnector.cs` | Jiii 连接器：远控增量 → Canonical 事实/事件；入站只接受 notice |
| `src/Fufucode.Plugins.FufuBridge/FufuConnector.cs` | Fufu 连接器：发布 presence 事实；入站明确拒绝（管道 v1 无通用事件帧） |

契约放在 `Fufucode.App.Abstractions`（纯 BCL 库），两个插件共享同一类型，不受各自 ALC 影响。

## 修改

| 文件 | 改动 |
|---|---|
| `PluginPackageManager.cs` | 新增 `static CompanionCore CompanionCore { get; } = new()`，宿主常驻，不依赖插件启用 |
| `RemoteControlHost.cs` | 新增 `AdapterChanged` 事件；`Attach` 后通知连接器订阅真实增量 |
| `RemoteControlPluginPackage.cs` | 版本 1.1.0；初始化时注册 `JiiiConnector`，关闭时注销并解绑 |
| `FufuBridgePackage.cs` | 版本 1.1.0；注册 `FufuConnector`，2 秒定时刷新 presence |
| `PluginMarketplace.cs` | 两个服务插件显示名改为“Jiii 连接”“Fufu 连接”，版本 1.1.0 |
| `manifest.json`（两个插件） | 同步名称与版本 |
| `PluginAccess.cs` | 注释更新，新增 `FufuBridgePluginId` |
| `MainViewModel.cs` / `SettingsPageViewModel.Features.cs` | 用户可见文案改为“Jiii 连接” |
| `SettingsPage.axaml` / `ChatPage.axaml` | 分组标题、行标题、按钮提示改为 Jiii 连接语义 |

## 设计要点

- 核心是唯一路由/去重/审计点，不保存业务真源。
- 事件按 `EventId` 去重，重放不重复扇出。
- 意图按 `IdempotencyKey` 去重；`unknown` 也记录，避免自动重放写操作。
- 扇出按连接器独立投递，一个连接器失败不影响其它连接器。
- 越界行为显式失败：
  - Fufu 连接器收到入站事件直接抛 `NotSupportedException`，不假装送达；
  - Jiii 连接器只接受 notice 类事件，其它类型明确拒绝；
  - 目标连接器不存在或未实现 intent sink 时返回 `rejected` + 错误码。
- 时间线条目增量不跨端转发，避免会话正文外泄。

## 实际验证

- `dotnet build src/Fufucode.Core`：0 错误 0 警告。
- `dotnet build src/Fufucode.Plugins.RemoteControl`：0 错误 0 警告。
- `dotnet build src/Fufucode.Plugins.FufuBridge`：0 错误 0 警告。
- `dotnet build src/Fufucode.App`：0 错误；1 条既有警告 `ChatPage.Composer.cs:152 CS8602`，非本次改动。
- `dotnet build Fufucode.sln -m:1`：52 个项目，0 错误 0 警告。
  - 并行构建曾在 `Fufucode.Plugins.Terminal` 的 XAML 编译出现 `OutOfMemoryException`，单线程重建通过；属本机内存限制，非源码错误。
- `dotnet run --project tests/Fufucode.App.RemoteControl.Tests`：139 项断言全部通过。
- `dotnet run --project tests/Fufucode.Fufu.Tests`：全部通过。
- `dotnet run --project tests/Fufucode.App.Plugins.Tests`：21 项通过。

## 未完成 / 边界

- 没有为 `CompanionCore` 新增单元测试（注册、去重、幂等、扇出失败隔离）。
- 没有真实 FUFU 进程联调；FufuConnector 目前只发布 presence，不接收业务事件。
- 没有 Jiii 客户端；Jiii 连接器只把现有远控协议映射成 Canonical，尚未实现 Canonical → Jiii 的入站 Intent 路径。
- 审批统一真源尚未合并：FUFU 走 `IExtensionUiSession`，远控走 `SessionApprovalGate`，两条路仍并存。
- 没有 UI 展示连接器状态与审计；`CompanionCore.Status` / `RecentAudit` 目前无界面入口。
- 插件升级到 1.1.0：已安装旧版本的用户是否需要重新安装载荷，未验证。
- 未提交 Git。

## 已知取舍

- `JiiiConnector` 通过 `PluginPackageManager.CompanionCore` 静态属性取核心，而不是 `GetService<ICompanionCore>()`。原因是两个插件都通过静态属性登记服务，保持一致性；后续可改为注入。
- `FufuConnector` 用 2 秒轮询刷新 presence，而不是订阅连接事件；`FufuPipeServer` 没有暴露 `ConnectionChanged` 给插件层。
