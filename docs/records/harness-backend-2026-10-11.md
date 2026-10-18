# 通用 Harness 执行后端交付

日期：2026-10-11。起点：主仓库 `C:/Projects/Fucode/Fufucode`，main `20841c6`。

## 完成

- Core 新增公共执行契约、四种 batch 协议适配、U1 SQLite 执行日志及事件收集。
- 新增 CLI 宿主供本机独立运行，应用后台可直接调用同一 Core 库。运行时路径、版本、环境均由配置提供。
- 持久化投递先于进程启动；重放去重、输入冲突拒绝、线程/工作树单运行、原模型/运行时/保存会话绑定、不明投递阻断重发。
- 接受审核和集成没有被 transport 成功替代。现有用户改动保留，未启停生产门房，未接管已有会话。

## 实测

| 检查 | 结果与证据 |
|---|---|
| `dotnet run --project tests/Fufucode.Harness.Tests/Fufucode.Harness.Tests.csproj` | 51 项通过；四协议真实子进程夹具、打回恢复、接受不集成、持久重放、坏流、无终态、取消、错误身份、崩溃 claim 和工作树写入冲突 |
| U1 `Fufucode.Automation.Tests` | 全部通过；复用原有交付、review、集成、依赖、lease 行为 |
| HarnessHost 构建 | 0 错误、0 警告 |
| Python smoke 脚本 | 编译检查通过 |
| 真实 Pi 1.1.0 | 两轮调用、同一保存会话记忆恢复、旧请求重放通过 |
| 真实 OMP 18.9.0 | 指定 magpie provider 后，两轮调用、同一保存会话记忆恢复、旧请求重放通过 |
| 真实 Codex 0.162.1 | exec/resume 两轮调用、同一 thread 记忆恢复、旧请求重放通过 |
| 真实 DSH 0.2.0-rc.2 | headless 两轮调用、同一 session-id 记忆恢复、旧请求重放通过 |

所有真实调用均显式 `devin/swe-2`，内容仅记忆标记与短回答，不执行项目开发、不派子代理。共八轮成功调用，另有一次 OMP 原 provider 的证书失败。fixture 的 rework/accept 是固定人工决定，不冒充真实模型审核；真实模型验证的是传输、持续会话和去重。

原始本地证据：

- `C:/Users/mio/AppData/Local/Temp/fufucode-harness-smoke-0abwor7d/results.json`：Pi/Codex/DSH 成功及 OMP 首次失败；各子目录含 manifest、stdout、stderr、SQLite 与原生事件。
- `C:/Users/mio/AppData/Local/Temp/fufucode-harness-smoke-z1bqzpof/results.json`：OMP 修正后成功。
- `C:/Users/mio/AppData/Local/Temp/fufucode-harness-tests-05e8efeece944b85b3d33906959e5441`：最终 51 项夹具证据。

成功会话身份：

| Harness | 两轮一致的身份 |
|---|---|
| Pi | `01a12bc3-0638-72c5-933c-1542502abd91` |
| OMP | `01a12bc7-7f3c-7070-87d1-0bd4d1fbe2ee` |
| Codex | `01a12bc3-664e-7ab3-a63c-823ca42e11c2` |
| DSH | `session-19128861-6845-4270-98b2-932d0df54cb9` |

## 问题与边界

OMP 会话文件以 title 记录开头，不能只读第一行当 session header；已修复并加入夹具。OMP 原 devin provider 证书失败没有通过关闭 TLS 校验绕过，改为明确选择已有 magpie provider；故障保留。DSH 既有 profile 的其他插件会打印非目标 provider 登录/网络警告，本次没有登录或修改这些插件。

这是四 harness 的有界执行公共后端，不是全部无人值守 P0：尚未消费 HTML 上层执行意图、未迁移旧生产门房、未接原生聊天 UI、未实现可交互审批与优雅取消。CLI 宿主不是自动安装的常驻服务，不声称关闭窗口或重启后自动运行。

同一项目只能有一个权威数据库。数据库内唯一运行约束不能替代跨宿主的资源租约，也不锁住未知用户进程。Unknown 自动解除、真实工作树 HEAD 交付接收门、后台服务恢复以及 Pi/OMP RPC、Codex app-server、DSH 交互适配属于后续接线。本文不将四种 batch 能力宣称为所有 harness 功能完全等价。

下一步操作卡：按 [后端契约](../tasks/harness-execution-backend.md) 将上层批准意图接到唯一后台宿主，先验证终端关闭/重启、受控对账和交付门，再接原生界面；保留当前四种独立安装配置路径作为同一适配层的部署选择。
