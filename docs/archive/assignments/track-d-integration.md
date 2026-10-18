# 轨道 C 下一任务：M2 集成——把外壳接到真实 Core

> 三个轨道的成果已在 main 汇聚（基线 `f88caab`）：A 的 Core（含 `PiChatSessionAdapter` 已实现你冻结的 `IChatSession`）、B 的审批扩展、你的外壳。本任务让 Fufucode 第一次真正对话。依据计划 §10 M2 完成条件。

## 基线与工作区

- 从当前 main 新建工作分支（或在 track-c 上先合并 main）；不得基于旧的 track-c 顶点开工。
- 你只写 `src/Fufucode.App/` 和 `docs/archive/progress/progress-c.md`。若集成中发现 Core/Pi 需要改动：记入 `docs/archive/progress/progress-c.md` 交轨道 A，不直接改。

## 任务

1. **组合根替换**：`MockChatSession` → 经 `PersistentSessionManager`/`ChatSessionCoordinator` 构建的真实会话（`PiChatSessionAdapter`）。进程参数按计划 §7.2：`--session-dir` 指向应用数据目录，工作目录取所选项目。保留 `--mock` 开关使 `--selftest` 在无 Pi 环境保持全绿。
2. **真实流式链路**：文本/思考/工具块经现有时间线渲染；注意你自己在 `docs/decisions.md` [轨道C] 写的线程同步约定（Idle 后排空 UI 队列）对真实实现同样适用。
3. **停止与草稿**：停止按钮走 `StopAsync`；`QueueCleared` 内容按约定恢复为草稿，不覆盖已有输入。
4. **恢复路径**：启动时用 `PersistentSessionManager.ResumeAsync` 接历史（`History` 树），新消息走 `Chat`（A 在 `docs/decisions.md` [轨道A] 已定义交付方式）。
5. **审批不在本任务范围**：审批请求的桌面端处理是轨道 A 的 M3 Core（`docs/archive/assignments/track-a-m3.md`，尚未完成）。集成时若收到 `extension_ui_request`，如实显示"审批功能接入中"即可，不要自行 stub 审批语义。
6. **验证**：真实 Pi 无模型查询经真实 UI 显示；真实模型多轮对话（**需应用专属 Pi 凭据，未配置则如实记录待验**）；停止后不自动继续；模型失败不留下永久"处理中"；关闭重开会话恢复。

## 完成标准

即计划 M2 完成条件：从打包前的开发目录启动，完成一次真实对话，流式渲染正确、终态正确、停止语义正确；`--selftest` 与真实链路双绿；证据与未验证项写入 `docs/archive/progress/progress-c.md`。

> **2026-09-29 变更**：执行者为新设轨道 D（原 C 收回；A 的临时 App 授权同步撤销）。任务内容不变；进度写 `docs/archive/progress/progress-d.md`（自建）。P1-1/P1-2/P1-3 属 Core 侧，由轨道 A 修复，不在本任务范围；你集成时若依赖其行为，开工前先同步 main。
