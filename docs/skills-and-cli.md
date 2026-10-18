# FUMO 技能与 CLI 使用说明

日期：2026-10-18。适用于默认后端 FUMO（纯 C# 原生内核）。

## 技能（Skills）

技能是描述“某类任务怎么做”的 Markdown 文件。FUMO 只在系统提示词里放一行名字与描述，
模型真正需要时再调用 `skill` 工具加载完整正文，避免一开始就占用大量上下文。
技能正文通常描述如何组合已安装的 CLI（`gh`、`git`、`ffmpeg`…）或直接给出 API 用法。

### 目录

按优先级从高到低，同名技能先出现者生效：

```
<项目>/.fumo/skills/<名称>/SKILL.md
<项目>/.agents/skills/<名称>/SKILL.md      # 跨内核通用位置
<项目>/.claude/skills/<名称>/SKILL.md      # 兼容 Claude Code
~/.fumo/skills/<名称>/SKILL.md
~/.agents/skills/<名称>/SKILL.md
~/.claude/skills/<名称>/SKILL.md
```

也支持 `<名称>.md` 作为 `SKILL.md` 的替代文件名。

### 格式

```markdown
---
name: pdf
description: 读取 PDF 的文本与表格；需要处理 PDF 时使用。
# 可选：禁止模型自动加载，只能由用户 /skill:pdf 调用
disable-model-invocation: false
---

1. 用 `pdftotext -layout input.pdf -` 提取文本。
2. 表格场景使用 `python -m pdfplumber`。
3. 输出超过 2000 行时先保存到文件，再用 grep_search 定位。
```

- `name`、`description` 会进入技能目录；正文只在加载时进入上下文。
- 技能目录可以附带 `scripts/`、`references/`、`assets/`；`skill` 工具会返回基目录与文件清单，
  模型据此用 `run_command` 执行脚本或读取资料。

### 两种调用方式

1. **模型自主加载**：模型调用 `skill` 工具，参数 `{ "name": "pdf" }`。
   省略参数时返回当前可用技能列表。
2. **用户显式调用**：输入 `/skill:pdf 只提取第 3 页`。
   技能正文会注入本轮消息，命令后的文字作为“用户参数”一并传入。

`disable-model-invocation: true` 的技能不会出现在系统提示和 `skill` 工具列表中，
但用户仍然可以用 `/skill:名称` 调用。

## CLI 工具（run_command）

FUMO 的核心执行工具就是 CLI：`run_command` 在当前工作区内启动 Shell，
把标准输出、标准错误和退出码返回给模型。

参数：

| 参数 | 说明 |
|---|---|
| `command` | 必填，Shell 命令（CLI 名称、参数、管道都可以） |
| `cwd` | 可选，相对工作区的工作目录；仍受路径沙箱约束 |
| `timeout_seconds` | 可选，1–600 秒，默认 120 |

行为：

- 超时或用户中断时杀死整棵进程树，避免残留子进程。
- 标准输出 / 标准错误分别限制捕获量，超出后继续排空但丢弃，并在结果中注明。
- 结果过长时保留首尾、折叠中间。
- Windows 下使用 UTF-8 代码页输出，减少中文乱码。
- 写操作与命令执行仍经过执行模式与人工确认门禁（manual/auto/full/plan）。

## 建议的扩展方式

1. 先写技能，把现有 CLI 的用法、参数、注意事项固化下来。
2. 需要远程认证、资源读取或跨客户端复用时，再考虑做 MCP 接入。
3. 不要在技能里写“请直接修改系统文件”之类的越权指令；命令仍受权限模式约束。
