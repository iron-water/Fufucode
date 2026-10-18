# 轨道 B 新任务：M5 演示扩展与插件交互面清单

> 审批桥接扩展已验收合并（7/7）。本任务依据计划 §10 M5 任务 3 与 §8.4，把"插件能用什么"变成可验证的事实，供 A 的 Core 路由和 C 的管理页消费。开工前：在自己的分支上 `git merge main` 同步最新协作约定。

## 范围（只允许写这些）

- `extensions/demo-extension/` —— 演示扩展（TypeScript）
- `scripts/demo-verify/` —— 验证驱动脚本
- `docs/archive/progress/progress-b.md`、`docs/decisions.md`（追加 `[轨道B]` 条目）

## 任务

1. **演示扩展**：注册一个自定义工具（如受审批链路覆盖的受控写入工具）、一个扩展命令（`/demo-...`）、并完整使用 `ctx.ui` 面：`select`、`confirm`、`input`、`editor`（对话框类）与 `notify`、`setStatus`、`setWidget`、`setTitle`（即发即弃类）。覆盖计划 M5 任务 3 的全部要求。
2. **验证驱动**：沿用 `scripts/bridge-verify` 的模式（真实 Pi + stub 模型 + 文件系统证据），场景至少覆盖：工具执行、命令执行、四类对话框往返（应答/取消/超时）、通知类到达、对话框超时后 Pi 不卡死。
3. **M5 交互面清单（核心交付物）**：从实测得出并冻结——哪些 `extension_ui_request.method` 必须应答、哪些免答、`widgetLines`/`set_editor_text` 的显示约束、超时字段行为、未知 method 的实测表现。写入 `docs/decisions.md` `[轨道B]`；这份清单是 A（Core 路由）和 C（管理页/弹窗映射）的对接契约。
4. **已知限制如实记录**：TUI 自定义控件不支持项、RPC 模式下的表现差异。

## 完成标准

- 演示扩展在真实 Pi 0.87.1 上全交互面跑通，证据落盘（同 A01 风格：看文件系统/协议记录，不只看消息）。
- M5 交互面清单冻结并写入 decisions.md；后续 A/C 改动与清单冲突时必须在此追加协调。
- 进度与未验证项如实更新。
