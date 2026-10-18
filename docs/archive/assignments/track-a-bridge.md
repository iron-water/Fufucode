# 轨道 A 任务：桥接集成收口——Core 对接真实 fufucode-bridge

> 你的 M3 Core 与 B 的审批扩展、M5 交互面清单都已在 main。你验收记录里"桥接扩展无就绪握手前审批状态保持 Unverified"的口子现在可以清了。依据：`docs/decisions.md` [轨道B] 的冻结契约（Pi 0.87.1 实测）、你自己的 [轨道A] `IExtensionUiSession` 设计、计划 §8.2/§11 A01–A03。

## 任务

1. **就绪握手**：启动后用 `get_commands` 探测 `fufucode-bridge.status`（B 冻结的探针），审批状态 `Unverified → Verified`；缺失或进程启动即退（B 实测：坏扩展 fatal exit）→ `Unavailable` 并透出诊断。这是 A03 的最终形态。
2. **对接真实扩展重跑 A01**：不再用 `tests/fixtures/` 的临时 fixture——用 main 上 `extensions/fufucode-bridge/` 对真实 Pi 跑允许/拒绝/超时三路径，拒绝后文件系统证据。验证你的参数快照令牌与 B 扩展的实际行为一致。
3. **按 B 清单落实路由差异**：`editor` 无 `timeout` 字段（host 必答否则永久挂起）；对话框挂起时不得等 `prompt` 响应（B 实测死锁场景）；未知 method 记录并忽略、不应答；连接丢失一律按拒绝。逐条对照 `docs/decisions.md` [轨道B] 核对你的 `IExtensionUiSession` 实现，不一致处修 Core。
4. **向 C 交底**：审批弹窗在 App 侧的消费方式（`IExtensionUiSession` 事件 → 弹窗 → 应答）整理成 `docs/decisions.md` `[轨道A]` 追加条目，供 C 集成审批 UI。

## 完成标准

- 就绪握手三态（Verified/Unavailable/Unverified 的迁移路径）有测试；真实桥接扩展下 A01 三路径 + A02 非阻塞全绿。
- B 清单的每条实测行为在 Core 中有对应实现或明确不适用理由。
- 进度与验证记录如实更新。
