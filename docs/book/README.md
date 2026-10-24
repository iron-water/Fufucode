# FUMO 与《深入理解 AI Agent》的章节对照

这组文章借用李博杰《深入理解 AI Agent：设计原理与工程实践》的十章结构，逐一对照本机 FUMO 内核的实际实现。每篇独立成文，可单独阅读；文中引用书中的概念与判断，但叙述与结论基于本仓库代码与实测数据。

## 为什么按这本书的框架来对照

这本书的核心公式是 **Agent = LLM + 上下文 + 工具**，并把它展开为 **Agent = Model + Harness**，其中 **Harness = 上下文管理 + 工具接口 + 约束 + 验证 + 纠正**。FUMO 是一个纯 C# 的 Harness 实现，这个拆法与它的代码边界几乎一一对应，适合作为对照坐标系。

## 章节映射

| 书 | 对应文章 | FUMO 的实际状态 |
|---|---|---|
| 1. AI Agent 入门 | [01-agent-introduction.md](01-agent-introduction.md) | 四层结构完整实现 |
| 2. 上下文工程 | [02-context-engineering.md](02-context-engineering.md) | 缓存友好、压缩保真已做，腐化未量化 |
| 3. 用户记忆和知识库 | [03-memory-knowledge.md](03-memory-knowledge.md) | 刻意的空缺，有替代方案 |
| 4. 工具 | [04-tools.md](04-tools.md) | 8 个内置 + MCP 网关 + 插件能力门禁 |
| 5. Coding Agent 与通用 Agent | [05-coding-agent.md](05-coding-agent.md) | 精确替换式编辑，分组检索 |
| 6. 交互：观察与动作空间 | [06-interaction.md](06-interaction.md) | 屏幕/DOM/语音输出有，语音输入无 |
| 7. Agent 的评估 | [07-evaluation.md](07-evaluation.md) | 三层可观测性 + 三类探针 |
| 8. 模型后训练 | [08-post-training.md](08-post-training.md) | 不做，但借鉴了训练视角 |
| 9. Agent 的持续进化 | [09-continual-evolution.md](09-continual-evolution.md) | 无自动进化，积累了三类证据 |
| 10. 多 Agent 协作 | [10-multi-agent.md](10-multi-agent.md) | Subagent 与 AutoDev Thread 两级 |

## 阅读顺序建议

如果只想了解整体架构，读第 1 篇即可——它给出了 FUMO 的四层边界划分。

如果关心长任务为什么能跑完，读第 2 篇（上下文）与第 7 篇（评估）。这两篇解释了压缩保真与失败归因。

如果关心工具设计，读第 4 篇（工具生态）与第 5 篇（Coding Agent）。这两篇解释了为什么选精确替换而不是补丁、为什么检索要分组。

如果关心多 Agent，读第 10 篇。它区分了子代理（短、独立上下文）与开发线程（长、持久工作区），并记录了档位设计被实测证伪后的归档过程。

## 三篇记录了空缺而非实现

第 3 篇（记忆与知识库）、第 8 篇（后训练）、第 9 篇（持续进化）描述的是 FUMO 没有做的事。写它们的原因不是凑数，而是：

- 空缺需要说清楚是"定位选择"还是"能力缺失"；
- 替代方案需要被记录下来，否则后来者会重复讨论；
- 未实现的部分往往决定了未来架构的边界。

## 数据来源

文中引用的实测数据来自：

- `tests/Fufucode.App.Backend.Tests` 的 `--live-agent-loop`、`--live-session`、`--long-horizon-only` 探针（真实端点）；
- `tmp/bench/run.py` 的外部评测夹具（隐藏验收 + 既有测试 + analyze + 耗时 + billable token 加权）；
- `perf/perf-*.jsonl` 的性能 span 记录（模型请求、工具、压缩、落盘）；
- `docs/decisions.md` 与 `docs/records/` 中逐轮记录的改动与验证结论。

具体数值会随模型、网关、任务变化，文章记录的是当时的观测结果与由此得出的判断，不是恒定指标。引用的档位对照数据来自已归档的 `--live-subagent-bench`，保留在 `docs/records/fumo-writetex-merge-2026-10-23.md`。
