# 轨道 A 下一任务：M2 Core（会话协调与类型化协议）

> M0/M1 已验收。下一任务依据计划 §6.3–6.6、§7.1 和 §10 M2。先读 `docs/decisions.md` 中 [协调] 条目：会话接口由轨道 C 在 `Fufucode.App/Abstractions` 先行定义，你在 Core 做真实实现与适配。

## 范围（你只写这些）

- `src/Fufucode.Core/`（新建项目，加入 `*.sln`）
- `src/Fufucode.Pi/`（可在其上补类型化 DTO 层）
- `tests/`（Core 与 DTO 的测试，继续用 FakePi）
- `docs/progress.md`、`docs/verification.md`

**不写 Avalonia 界面**——UI 由轨道 C 并行开发；M2 集成时才对接。

## 任务

1. 类型化协议 DTO：把 `PiRpcClient` 的裸 `JsonElement` 提升为类型化事件与消息模型（计划 §6.6：文本/思考/工具调用分块、delta 追加、最终替换；usage 为累计值不重复相加）。未知字段向后兼容。
2. 会话协调器：连接/活动/交互状态分离（§7.1），事件先订阅后 prompt（§6.4），停止 = `clear_queue` → `abort` + 草稿恢复（§6.5），`agent_settled` 语义与超时分离（§6.3–6.4）。
3. 实现并适配 C 冻结的 `IChatSession` 接口（以 `docs/decisions.md` 与 C 的 Abstractions 为准）；形状不合理的部分不要单方面改——记入 `docs/decisions.md` [轨道A] 并同步 C。
4. 全部用 FakePi 做真实管道测试；真实 Pi 无模型冒烟保持通过。

## 参考资料（选读，按相关性取用）

- `docs/research/UNIGETUI_DESIGN_STUDY.md` 的 §2.4–2.6（操作事件总线、进程封装 `_UnSafe` 模式）、§6.1.1–6.1.3（进程输出泵、操作卡片状态机、崩溃 pending 文件）、§6.1.7（**DTO 用 source-generated `JsonSerializerContext`，零反射**——与当前 DTO 层直接相关）、§6.1.8（装配容错与世代号防过期回填）。状态标签：设计参考，非需求规范；采纳与否记入 `docs/progress.md`。

## 完成标准

- Core 测试覆盖：消息重建分块不混淆、最终消息不重复、usage 不重复累计、停止后队列清空、超时不自动重发副作用命令。
- 真实 Pi 一条真实模型多轮对话（有凭据时）走通 Core 全链路，作为 M2 Core 的端到端证据；无凭据则如实记录待验。
- 进度与验证记录更新；不把未运行的验证标为通过。
