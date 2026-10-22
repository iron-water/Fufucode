# FUFU Pet — 开发任务记录

任务书：FUFU-PET-001（用户任务书附件，2026-10-21）。主控会话：https://app.devin.ai/sessions/18c9e140e1664dcfa09dd5e8e8239e8d

> 注：docs/ 旧树已被有意抹除（2026-10-21 历史重写），本目录只承载 FUFU 自己的任务记录，
> 不恢复任何旧文档；仍有效的协议以 `AGENTS.md` 正文 + 本目录记录为准，
> 历史协议文本备份在私有库 `iron-water/Great-notes/fufucode-docs-backup/`。

## 模块边界（FUFU-00 核查结论）

| 单元 | 位置 | 职责 | 现状 |
|---|---|---|---|
| FUFU Desktop | `src/Fufucode.Fufu/`（新） | 独立 exe：透明悬浮窗、角色渲染、托盘、行为状态机、Bot 宿主、IPC 服务端 | FUFU-01 起步 |
| Fufucode Bridge | `src/Fufucode.Plugins.FufuBridge/`（FUFU-04 新建） | 可选插件：Named Pipe 客户端、会话订阅/状态查询/受控任务转发 | 未建 |
| FUMO Runtime | 复用 `src/Fufucode.Core`（FUFU-03 引用） | `NativeSessionFactory` → `IChatSession`；FUFU 用独立 `AppDataLayout` 根 | 已存在 |
| Live2D 验证 | `labs/fufu-live2d/` | 渲染路径技术验证（执行者 B） | 进行中 |

已有 `extensions/fufucode-bridge/` 是 Pi 工具审批桥（TypeScript 扩展），与 FUFU 的 Bridge 插件无关，不动。

## 关键接口结论

- `NativeSessionFactory.Create(AppSettingsStore, sessionId, workspaceRoot, ...)` 返回 `IChatSession`
  （`SendAsync`/`StopAsync`/`Event` 流）。FUFU 传自己的 `AppDataLayout(%LOCALAPPDATA%\FufuPet)`
  → 独立 settings.json 端点配置、独立 sessions/ 目录，不与 Fufucode 争会话文件。
- FUFU 宠物行为设置不进 `AppSettings`（固定 schema），独立 `fufu-settings.json`。
- 托盘：`Avalonia TrayIcon + NativeMenu`（参照 `Fufucode.App/Integration/TrayIconService`）。
- 单实例：`Local\FufuPet.SingleInstance.v1` 命名 Mutex + 激活事件（参照 `SingleInstanceGuard`）。
- 透明穿透：窗口本体即角色包围盒 + Win32 `WM_NCHITTEST` 命中判定（`ICharacterRenderer.HitTest`）。

## IPC 契约（FUFU-04 实现版）

双向命名管道 `\\.\pipe\fufu.fufucode.v1`（插件写 → FUFU 读）与
`\\.\pipe\fufu.fufucode.v1.back`（FUFU 写 → 插件读），JSON Lines。
单句柄挂起读会阻塞写（本机实测），故读写各走一条单向管道。
Bridge 插件为客户端、FUFU 为服务端；握手 `hello`/`helloAck`（协议版本协商），
服务端 `GetNamedPipeClientProcessId` 校验客户端进程名须为 `Fufucode*`，否则断开。

消息（`Fufucode.App.Abstractions.FufuBridgeProtocol`）：
插件→FUFU `hello / snapshot / event / approval / approvalClosed / ping`；
FUFU→插件 `helloAck / submit / select / approve / pong / error`。

## 运行与打包（FUFU-06）

```powershell
pwsh fucode/publish-fufu.ps1            # 产出 out/fufu-06/{fufu, service.fufu-bridge, *.zip}
```

- `fufu/fufu.exe`：自包含 win-x64，双击独立运行（不拉起 Fufucode，无 .NET 依赖）。
- `service.fufu-bridge/`：插件载荷 = `manifest.json` + `Fufucode.Plugins.FufuBridge.dll`
  （宿主从 Default ALC 解析 Core/Abstractions，载荷不带宿主程序集副本）。
- **安装**：Fufucode 插件市场 → `service.fufu-bridge`（FUFU 桌宠桥接）→ 安装；
  或把载荷目录复制到 `%LOCALAPPDATA%\Fufucode\plugins\service.fufu-bridge\` 再重启 Fufucode。
  市场条目 `DefaultInstalled=false` —— FUFU 不默认安装（任务书硬性要求）。
- **更新**：重新 publish 后在市场点「更新」或覆盖 plugins/ 下载荷；`plugins.json` 记录状态。
- **卸载**：市场卸载仅删载荷；FUFU 数据 `%LOCALAPPDATA%\FufuPet\`
  （`fufu-settings.json`、`task-log.jsonl`、sessions/）与 `%LOCALAPPDATA%\Fufucode\fufu-bridge.log`
  全部保留，不随卸载删除。
- **进程隔离**：FUFU 独立进程；关闭 Fufucode → FUFU 仍运行（实测 `Responding=True`），
  插件断开后按 0.5s→8s 指数退避自动重连（实测 3 次成功）。
- **权限**：pipe 是本机（`\\.\pipe`）同用户通道；服务端按客户端 PID→进程名校验，
  非 Fufucode 进程（powershell 实测）被拒绝；提交/切换/审批必须经 FUFU 面板用户操作发出。

## 任务分工

- 主控（本会话）：FUFU-00/01/03/04/05/06，接口与集成。
- 执行者 B（会话 13bc7660…）：FUFU-02 Live2D 验证，`labs/fufu-live2d/`，分支 `feature/fufu-02-live2d-probe`。

## 风险清单

1. Live2D SDK 许可：二进制/模型不入公开仓库；验证用官方示例模型仅本地引用。
2. FUFU-03 端点配置：FUFU 无 Fufucode 的设置 UI，需自带最简端点设置或支持导入。
3. `publish-local.ps1` 在 Windows PowerShell 5.1 下中文编码解析失败——用 pwsh 或手动等效命令。
4. FUFU 不默认安装、不抢焦点、自由移动可关——验收按任务书 §十一。
