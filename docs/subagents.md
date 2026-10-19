# FUMO 子代理（Subagents）

日期：2026-10-18。适用于默认后端 FUMO。

## 定位

子代理用于把“搜索、探索、局部审查”等会产生大量中间输出的工作隔离出去：
父会话只收到最终结论，不把子代理的工具日志塞进自己的上下文。

与 Claude/OMP 的做法一致：**独立的 Agent 循环 + 受限工具 + 只回传结论**。当前版本为进程内、同步、一次性执行。

## 内置子代理

| 名称 | 工具 | 用途 |
|---|---|---|
| `explore` | `read_file`、`list_dir`、`grep_search`、`glob_files` | 只读探索与事实定位 |
| `general` | 全部内置工具（除 `subagent`、`update_plan`） | 边界明确的一般任务 |

## 自定义定义

目录（项目优先于用户）：

```
<项目>/.fumo/agents/*.md
<项目>/.agents/agents/*.md
<项目>/.claude/agents/*.md       # 兼容 Claude Code
~/.fumo/agents/*.md
~/.agents/agents/*.md
~/.claude/agents/*.md
```

格式：

```markdown
---
name: reviewer
description: 只读代码审查，指出缺陷与风险。
tools: read_file, grep_search
maxTurns: 10
# model: 可选，覆盖该子代理使用的模型 id
---

你是只读审查子代理。先阅读相关文件，再输出按严重度排序的问题清单与证据。
```

- `tools` 只接受**内置工具名**；未列出的工具在子代理内会被拒绝。
- `maxTurns` 取 1–50，默认 15。
- `name` 相同：项目定义覆盖内置/用户定义。

## 模型侧用法

系统提示会列出可用子代理。模型调用：

```json
{ "agent": "explore", "prompt": "定位配置加载入口，返回文件与行号" }
```

- 省略 `agent` 使用 `general`；
- 省略 `prompt` 时返回可用子代理列表；
- `model` 可选，用于为本次子代理指定模型 id；
- 返回内容带 `[子代理 名称]（工具调用 N 次）` 标记。

## 行为与边界

- 子代理拥有独立历史，不复用父会话上下文（避免上下文污染）；
- 子代理工具调用同样经过执行模式与人工确认门禁；
- 子代理不能调用 `subagent`，第一版不支持递归/嵌套；
- 单次执行上限 5 分钟，超时由父会话取消；
- 同步执行：父会话在子代理运行期间等待（后台/并发保留给后续版本）；
- 子代理不使用插件/MCP 工具，只使用内置工具（保持隔离与轻量）。
