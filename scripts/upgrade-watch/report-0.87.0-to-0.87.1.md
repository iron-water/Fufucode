# Pi 升级雷达报告

- 基线：`npm 0.87.0`
- 目标：`npm 0.87.1`
- 生成：2026-09-29T08:05:04.065Z
- 范围：rpc 命令/字段、事件类型、extension_ui 子协议、session 版本、docs

## 破坏性（升级前必须处理）

（无）

## 需评估（行为可能变化，人工确认）

- **events-docs** — event 'agent_settled' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'auto_retry_end' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'auto_retry_start' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'bash_execution_update' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'compaction_end' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'compaction_start' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'extension_error' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'summarization_retry_attempt_start' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'summarization_retry_finished' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'summarization_retry_scheduled' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'text' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'tool_execution_end' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'tool_execution_start' added to json.md
  - 证据：`docs/json.md`
- **events-docs** — event 'tool_execution_update' added to json.md
  - 证据：`docs/json.md`
- **docs** — rpcDoc changed
  - 证据：`/docs/rpc.md`
- **docs** — jsonDoc changed
  - 证据：`/docs/json.md`
- **docs** — sessionDoc changed
  - 证据：`/docs/session-format.md`

## 无影响

（本次扫描面上未发现新增差异之外的条目）

> 本报告只对比，不自动升级、不改 lockfile（计划 §3 红线）。
