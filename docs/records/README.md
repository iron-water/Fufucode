# 记录目录说明

日期：2026-10-21。

`records/` 保存交付记录、长期日志、审查、故障登记和验收证据。它不是设计规格，也不自动成为当前实现承诺。

## 分类

| 类型 | 例子 | 状态 |
|---|---|---|
| 长期日志 | `logs/failure-registry.md`、`logs/feedback.md`、`logs/verify-log.md`、`logs/hourly-dispatch-ledger.md` | 持续追加，按日期/轮次读 |
| 交付记录 | `MKT1-plugins-delivery.md`、`S2-settings-reorg.md`、`R1-after-focus-*` | 单次任务，保留结论和证据，不复制为当前规格 |
| 审查记录 | `code-review-*.md`、`*review*.md` | 历史审查，结论以最新记录为准 |
| 整理/收口 | `organization/docs-organization-2026-10-05.md`、`organization/worktree-retirement-2026-10-05.md` | 历史操作记录 |
| 证据 | `evidence/**`、`*/evidence/**` | 截图、日志、原始输出；不是产品验收本身 |

## 命名与写作

- 新记录优先 `任务ID-主题-YYYY-MM-DD.md` 或 `YYYY-MM-DD-主题.md`。
- 每份记录至少写：日期、状态、改动、实际验证、未验证项、证据位置。
- 交付记录不复制完整源码；证据放 `evidence/`，记录只引用路径。
- 历史记录不改写；需要更正时追加“更正”段。

## 入口

- [INDEX.md](INDEX.md)：交付、日志、证据的入口索引。
- [evidence/](evidence/)：现有截图与输出。
- 旧根路径迁移表：[document-path-map.json](document-path-map.json)（2026-10-05 一次性迁移，不是当前索引）。
