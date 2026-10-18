# 轨道 A 下一任务：M3 Core（审批请求桌面端处理）

> M4 Core 已验收。轨道 B 的桥接扩展尚未交付，但桌面端的审批处理不依赖它——协议形状由官方 `rpc-extension-ui.md` 冻结。本任务把 M3 桌面侧做完整，B 一落地即可对接。依据计划 §7.1（交互状态）、§8.2、§8.4 与 §11 的 A01–A03、C02。

## 范围（你只写这些）

- `src/Fufucode.Core/`、`src/Fufucode.Pi/`
- `tests/`（含用于真实 Pi 验证的临时 fixture 扩展——放 `tests/fixtures/`，**不得**写入 `extensions/`，那是轨道 B 的领地）
- `docs/progress.md`、`docs/verification.md`

## 任务

1. **extension_ui_request 路由**：对话框类（`select`/`confirm`/`input`/`editor`）进入待处理交互状态集合（§7.1），等待桌面决定后以 `extension_ui_response`（原 `id` + `value`/`confirmed`/`cancelled`）应答；fire-and-forget 类（`notify`/`setStatus`/`setWidget`/`setTitle`/`set_editor_text`）分发给展示层但不阻塞。
2. **超时与清理**：请求超时后本地清理待请求集合并按拒绝上报（§8.4：不假设服务端超时会自动清理本地 UI）；连接代次隔离——旧进程/旧会话的审批 ID 不得用于新连接（C02）。
3. **管道纪律**：审批等待期间 stdout 持续消费、其他事件不被阻塞（A02）；一次批准只对应具体请求与参数（§8.2）。
4. **审批不可用诊断**：桥接扩展缺失/加载失败时状态可解释（`extension_error` 事件透出），不误报"审批已生效"（A03）。
5. **验证**：FakePi 模拟对话框请求的全生命周期；真实 Pi 用 `tests/fixtures/` 下的临时确认扩展走通允许/拒绝/超时三路径，验证拒绝后文件确实未改变（A01 的桌面侧证据；扩展侧证据仍归轨道 B）。

## 协调点

- 待处理审批如何映射到 `IChatSession`（弹窗是 UI 的事，Core 只暴露状态与应答入口）：接口需扩展时按惯例写 `docs/decisions.md` `[轨道A]` 并同步 C。
- 与 B 的对接面：Core 只依赖官方 `extension_ui_request/response` 形状，不依赖 B 的实现细节。

## 完成标准

- A01（桌面侧）/A02/C02/A03 对应测试通过；`agent_settled` 与审批状态并存时行为正确。
- 审批等待不阻塞任何事件路由（有专项测试）。
- 进度与验证记录如实更新。
