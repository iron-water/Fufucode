# 轨道 D 下一任务：M3 审批接线 + M4 侧栏（MVP 收口）

> 你的 M2 集成已验收（独立复核：真实关闭重开恢复、组合根三路径、真实停止链路全绿）。main 上轨道 A 的 M3 Core（`IExtensionUiSession` 审批路由、就绪握手）**已就绪**——你 progress-d.md 里"依赖轨道 A 的 M3 Core"的说法已过时，开工前 `git merge main` 同步。依据：计划 §8.2/§8.4、§10 M3/M4、`docs/decisions.md` [轨道A]（审批消费方式）与 [轨道B]（冻结交互面清单）。

## 范围（只允许写这些）

- `src/Fufucode.App/`、`docs/archive/progress/progress-d.md`、`docs/verification.md` 中本轨验证段
- Core/Pi 需要改动时：记 progress-d.md 交轨道 A，不直接改

## 任务一：M3 审批接线（把"审批功能接入中"占位换成真弹窗）

1. 订阅 `IExtensionUiSession` 的待处理请求：对话框类（confirm/select/input/editor）映射到 Avalonia 弹窗——title/message/options 渲染，用户决定后经应答入口返回（`confirmed`/`value`/`cancelled`）。
2. 按 [轨道B] 清单落实差异：**editor 无 timeout 字段（host 不应答会永久挂起——必须有应答路径）**；对话框挂起时不等 `prompt` 响应；请求超时后本地关闭失效弹窗（不假设服务端清理）。
3. 通知类（notify/setStatus/setWidget/setTitle）映射到状态栏/轻提示；`set_editor_text` 保护用户未发送草稿（计划 §8.4）。
4. 审批状态（Verified/Unavailable/Unverified）在 UI 可见；Unavailable 时明确提示审批未生效，不开放"受控写入"暗示。
5. 验收（对应 §11 A01–A03 桌面侧）：真实模型驱动工具调用 → 弹窗 → 允许才写盘；拒绝/关闭弹窗按拒绝；审批期间时间线继续滚动。**用 Atria 测试端点**（见 docs/decisions.md [协调]：配置已就绪于应用专属 pi-agent，key 走环境变量）。

## 任务二：M4 侧栏与会话管理（MVP 最后一块）

1. 侧栏：项目内会话列表（读 `DesktopMetadataStore`，只读历史可先做）、新建会话、切换会话（switch 路径经 Core；注意进程与会话独占约束）。
2. 会话命名、当前项目显示；中断会话的 Interrupted 标注沿用你已有的持久化标注。
3. 草稿按会话隔离保存/恢复（desktop.db 已有表）。
4. 长列表沿用现有虚拟化与 `x:DataType` 纪律。

## 完成标准

- 真实模型完整走通计划首版用户路径：**启动 → 选项目 → 提交任务 → 流式消息与工具卡片 → 弹窗审批 → 停止 → 关闭 → 重开恢复**（M0–M4 的 MVP 验收；凭据缺失项如实留待验）。
- `--selftest` 与 `--selftest --realpi` 全绿不回退；证据与未验证项写入 progress-d.md。
