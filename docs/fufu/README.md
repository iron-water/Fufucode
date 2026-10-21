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

## IPC 契约（FUFU-04 预定义）

Named Pipe `\\.\pipe\fufu.fufucode.v1`，JSON Lines。Bridge 插件为客户端、FUFU 为服务端。
握手（协议版本+身份）→ 会话列表/快照订阅 → 事件流（任务开始/执行/完成/失败/审批需求）
→ 受控命令（focus/submitTask/cancel），带授权令牌与能力白名单。详见实现时 `docs/fufu/bridge-protocol.md`。

## 任务分工

- 主控（本会话）：FUFU-00/01/03/04/05/06，接口与集成。
- 执行者 B（会话 13bc7660…）：FUFU-02 Live2D 验证，`labs/fufu-live2d/`，分支 `feature/fufu-02-live2d-probe`。

## 风险清单

1. Live2D SDK 许可：二进制/模型不入公开仓库；验证用官方示例模型仅本地引用。
2. FUFU-03 端点配置：FUFU 无 Fufucode 的设置 UI，需自带最简端点设置或支持导入。
3. `publish-local.ps1` 在 Windows PowerShell 5.1 下中文编码解析失败——用 pwsh 或手动等效命令。
4. FUFU 不默认安装、不抢焦点、自由移动可关——验收按任务书 §十一。
