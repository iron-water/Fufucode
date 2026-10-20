# FUMO Subagent 插件

Subagent 不是默认内核能力。它由可选插件 `workspace.subagents` 提供；默认插件市场展示该插件，默认安装包不包含其程序集。
用户安装插件后档位固定为 `faster`。`stronger` 只能由用户在设置中手动切换；系统不自动提示、升级或降级。

产品只提供两个档位：`faster` 与 `stronger`。读写权限、worktree、审批和 Thread 升级属于任务执行策略，不是档位。

# FUMO 子代理（Subagents）

日期：2026-10-19。适用于默认后端 FUMO。

## 定位

子代理是**进程内、短生命周期**的隔离上下文：父会话只收到结论或候选补丁。
它不承担持久交付、工作树租约或跨代理协调——这些属于 AutoDev Thread。

两种模式：

| 模式 | 工作区 | 能力 | 适用 |
|---|---|---|---|
| `read`（默认） | 共享只读 | 读文件、列目录、正则搜索、通配查找；显式声明的能力工具 | 探索、定位、审查、快速问答 |
| `patch` | 临时 git worktree | 读 + 写/编辑 + `run_command` | 单文件/小范围改动、验证命令、候选补丁 |

`read` 是默认模式，默认代理 `explore`：只读工具、无 worktree、可并行扇出。模式层**不设**轮次、超时、推理强度或输出长度限制；这些只由代理定义决定。
`patch` 默认代理 `general`：在隔离 worktree 内执行，同样不设模式级预算。

read 的速度来自实现层，而不是限制：
- 不创建/清理 worktree；
- 只读工具可在一轮内并行执行；
- 多个 read 子代理可并行扇出；
- 共享文件读取缓存，重复读取不再落盘；
- 可通过 `context` 预取已知线索，减少发现轮次；
- 可通过代理定义的 `model` 指定更快的模型。

`timeoutSeconds` 只有显式写入代理定义时才生效，默认不设子代理级超时；父会话 Stop 始终可取消。

## 内置与自定义

内置：

| 名称 | 模式 | 工具 |
|---|---|---|
| `explore` | read | `read_file`、`list_dir`、`grep_search`、`glob_files` |
| `general` | patch | `read_file`、`write_file`、`edit_file`、`list_dir`、`grep_search`、`glob_files`、`run_command` |

目录（项目优先）：

```
<项目>/.fumo/agents/*.md
<项目>/.agents/agents/*.md
<项目>/.claude/agents/*.md
~/.fumo/agents/*.md
~/.agents/agents/*.md
~/.claude/agents/*.md
```

格式：

```markdown
---
name: reviewer
description: 只读代码审查
mode: read                               # read | patch，默认 read
tools: read_file, grep_search             # 内置工具名，或显式声明的能力工具
maxTurns: 6
timeoutSeconds: 180                       # 可选：安全上限，10–600
model: group/auto-deepseek-v4-1-flash     # 可选：read 想更快时优先换快模型
effort: high                              # 可选：推理强度；未配置时沿用模型默认
outputSchema: {"type":"object","required":["verdict","issues"]}
fork: true                                # 可选：继承父会话最近历史
---

你是只读审查子代理……
```

字段说明：

- `mode: read` 会强制过滤掉所有写工具与 `run_command`，即使 `tools` 里声明了也不会生效。
- `mode: patch` 只允许内置修改工具；能力工具与嵌套子代理在 patch 内关闭，避免绕过 worktree。
- 能力工具（`skill`、`mcp`、插件工具）只有显式列出才在 read 模式可用；它们可能作用于工作区之外，调用方需自行确认风险。
- `outputSchema` 为单行 JSON；最终答复会被校验，缺失 `required` 字段即失败。
- `fork` 会带上父会话最近 30 条、最多 100k 字符的历史。
- read 模式可嵌套：默认最多 1 层（root=0），patch 模式不嵌套。

## 工具用法

```json
{ "agent": "explore", "prompt": "定位配置加载入口" }
{ "mode": "read", "prompt": "汇总 MCP 初始化路径" }
{ "mode": "patch", "prompt": "把超时从 5 秒改成 10 秒并运行测试" }
{ "agent": "reviewer", "prompt": "审查这段改动" }
```

- 省略 `agent` 时：`read` 使用 `explore`，`patch` 使用 `general`。
- 省略 `prompt` 时列出可用代理及模式。
- 显式 `mode` 与代理定义冲突时明确拒绝，不静默改写。
- `subagent` 调用可并行：read 交互只读；patch 写入各自独立 worktree，worktree 创建内部串行化。

## patch 补丁契约

patch 子代理返回：

- `patch`：`git diff --cached` 生成的 unified diff（≤ 32 KB 时内联；更大的补丁只给路径，避免上下文截断）；
- `patchPath`：补丁临时文件（始终写入，可直接 `git apply <path>`）；
- `changedFiles`：改动文件列表；
- `baseHead`：补丁基线提交；
- 工具轨迹与验证命令结果（`run_command` 的退出状态）。

补丁**不会**写入父工作区。父会话审阅后可以：

1. 直接应用补丁；
2. 将任务升级为 AutoDev Thread（持久 worktree、租约、交付门禁）；
3. 丢弃补丁。

## 行为与边界

- 子代理拥有独立历史；`fork: true` 才继承父历史。
- 子代理工具调用同样经过执行模式与人工确认门禁（patch 的写工具在子代理引擎内触发审批）。
- 父会话 Stop 会取消当前回合；前台子代理随父回合取消，没有后台任务残留。
- 子代理用量（输入/输出 token）随结果返回，但不并入父会话用量统计。
- 不做后台队列、命名邮箱、共享任务板或持久工作树；这些属于 Thread。
- `maxTurns` 用尽时会返回“已完成 + 限制提示”，调用方应结合内容判断是否完整。
