# 五大内核的扩展体系调研：Pi / OMP / Claude Code / Codex / DSH

> 调研基于本机已安装产物与源码/逆向代码（只读）：Pi 1.3.0、OMP 18.11.x、Claude Code 2.1.302、Codex 0.163.0、DSH 0.2.0-rc.2。
>  说明：OMP 的 CLI 是 18.11.0、随附源码是 18.11.3；Codex/DSH 的部分说明文档版本略旧，已尽量以当前配置和代码为准。

------

## 一、一句话结论

| 内核            | 扩展体系的本质                                               | 最鲜明的特征                                                 |
| --------------- | ------------------------------------------------------------ | ------------------------------------------------------------ |
| **Pi**          | **单一进程内 TypeScript 扩展 API** + npm/git 包分发          | 契约最小、工具暴露层级最讲究、**项目信任门禁最强**           |
| **OMP**         | **四层扩展模型**：Pi 兼容扩展 + 插件包 + 扩展根目录 + 旧式 Hook | 运行时能力最全：子代理、LSP、任务隔离、工作树、Advisor、Collab |
| **Claude Code** | **声明式插件包**（12+ 组件类型）+ 市场 + 少量 JS 扩展模块    | 组件种类最丰富：命令/代理/技能/Hook/MCP/LSP/输出风格/工作流/监控 |
| **Codex**       | **配置与声明式插件**（技能/MCP/Hook/App）                    | 模型目录本身就是扩展面；配置分层最严格；没有进程内插件 API   |
| **DSH**         | **Cordis 微内核**：npm 包 + patch 组合 + 服务依赖注入        | 扩展深度最高：服务接缝、作用域隔离、Agent 预设、工作流引擎、热更新 |

如果按“**扩展能力从薄到厚**”排序：
 **Pi → Codex → Claude Code → OMP → DSH**
 如果按“**安全边界从强到弱**”排序：
 **Pi ≈ Claude Code → Codex → DSH → OMP**

------

## 二、五种“扩展世界观”的直接对比

| 维度           | Pi                           | OMP                                            | Claude Code                                 | Codex                                           | DSH                                    |
| -------------- | ---------------------------- | ---------------------------------------------- | ------------------------------------------- | ----------------------------------------------- | -------------------------------------- |
| **扩展单元**   | TS/JS 模块（默认导出工厂）   | ①Pi 兼容模块 ②插件包 ③扩展根目录 ④Hook 模块    | `.claude-plugin/plugin.json` 插件包         | `.codex-plugin/plugin.json` 插件包              | npm 包 + Cordis 插件 + patch 行        |
| **运行方式**   | 进程内，用户权限             | 进程内（多层加载）                             | 命令/MCP/技能/代理声明式；Hook 可为 JS 模块 | 无进程内插件 API；MCP 子进程 + 命令 Hook + 技能 | 进程内服务/事件/Fiber                  |
| **分发**       | `pi install`（npm/git/本地） | `omp plugin` + 市场                            | `claude plugin` + 市场                      | `codex plugin` + 市场                           | `dsh plugin`（pnpm）+ 市场             |
| **项目信任**   | **有，加载前拦截**           | **无，始终视为已信任**                         | 有，工作区信任 + 插件安装确认               | 有（项目 trust_level）                          | 无逐目录信任；有沙箱/审批              |
| **热重载**     | `/reload` 重载扩展代码       | `/reload-plugins` 重载资源；扩展代码未确认重载 | `/reload-plugins`；修改需重启               | 无明显热重载                                    | 配置/主题/客户端 HMR；宿主模块常需重启 |
| **扩展面数量** | 1 套 API，41 个事件          | 50 个事件 + 4 层机制                           | 12+ 组件类型，~34 个 Hook 事件              | 4 类组件（技能/MCP/Hook/App）                   | 287 个官方包，服务级扩展               |

------

## 三、常用扩展类型逐项拆解

### 1. MCP（五家全部支持，但定位不同）

|              | Pi                                                           | OMP                                    | Claude                                                 | Codex                                               | DSH                          |
| ------------ | ------------------------------------------------------------ | -------------------------------------- | ------------------------------------------------------ | --------------------------------------------------- | ---------------------------- |
| 形态         | 内置扩展 `builtin:mcp`                                       | 原生客户端                             | 原生客户端                                             | 原生客户端 + 插件 `.mcp.json`                       | 原生 `mcp-client`            |
| 传输         | stdio / Streamable HTTP（拒绝旧 SSE）                        | stdio / HTTP / **SSE**                 | stdio / SSE / HTTP / WS / IDE / SDK 等                 | stdio / Streamable HTTP                             | stdio / Streamable HTTP      |
| 工具命名     | `mcp__server__tool`                                          | `mcp__server_tool`（单下划线）         | `mcp__server__tool`                                    | 由服务器/过滤配置决定                               | `mcp__server__tool`          |
| 工具暴露控制 | **direct / codemode / deferred / hidden / model-only**，支持 `tool_search` | essential / discoverable，`xd://` 挂载 | Hook 匹配 + 权限规则                                   | `enabled_tools` / `disabled_tools` / 每工具审批模式 | 服务器级启用/禁用            |
| 额外能力     | 资源工具、OAuth、扩展可注册服务器                            | MCP prompts 变成斜杠命令、资源订阅     | MCP prompts→命令、资源、**MCP Apps(UI)**、MCP 托管技能 | 企业/MDM 配置、OAuth、App 连接器                    | 资源，不支持 MCP prompt 模板 |

**要点**：Pi 最关心“怎么把海量 MCP 工具省着给模型看”；Claude 最关心“MCP 能不能带 UI 和权限”；DSH/Codex 更像标准客户端。

------

### 2. 子代理 / 多代理 / 团队

|                | Pi                                     | OMP                                                | Claude                                                       | Codex                             | DSH                                        |
| -------------- | -------------------------------------- | -------------------------------------------------- | ------------------------------------------------------------ | --------------------------------- | ------------------------------------------ |
| 核心支持       | **无**（只有示例扩展，另起 `pi` 进程） | **有** `task` 工具                                 | **有** Agent/Task 工具                                       | **有** `spawn_agent`              | **有** 子代理注册表                        |
| 定义方式       | Markdown 示例约定                      | Markdown frontmatter（tools/model/spawns/output…） | Markdown frontmatter（model/tools/effort/permissionMode/hooks/mcpServers/skills/memory/isolation…） | `[agents.role]` / `agents/*.toml` | 提供者：spawn / fork / codex / claude-code |
| 隔离           | 独立进程                               | 工作树/CoW 隔离                                    | **worktree / remote**                                        | fork_turns + 线程                 | 子会话隔离                                 |
| 结构化输出     | 示例约定                               | 有 output schema                                   | 有                                                           | 有                                | 有                                         |
| 团队/看板/邮箱 | 无                                     | Agent Hub / Collab（实时共享会话）                 | **Agent Teams**（邮箱、任务、成员）                          | 无                                | **Agent Team**（邮箱、任务看板、CAS 版本） |

**要点**：

- Pi 把子代理留给扩展作者；
- OMP 把子代理做成完整运行时（批量、异步、并发上限、派生策略）；
- Claude 的 fork 子代理会继承完整父对话，并复用提示缓存；
- Codex 区分“会话内子代理”和“独立新线程”；
- DSH 甚至可以把 Codex / Claude Code 本身当作子代理后端。

------

### 3. 技能（Skills）

五家已经**高度趋同**：基本都是 `SKILL.md` + `scripts/ references/ assets/`，按需加载（渐进披露）。

差异在“来源与治理”：

|            | 特色                                                         |
| ---------- | ------------------------------------------------------------ |
| **Pi**     | Agent Skills 规范；用户/项目/`.agents`/包；`/skill:name` 强制加载；`disable-model-invocation` |
| **OMP**    | 跨内核目录（Claude/Codex/Cursor 等）+ **Skillshare 公共技能市场**（发布/版本/撤回）+ 自动学习技能 |
| **Claude** | 个人/项目/插件/`--add-dir`/MCP 托管；frontmatter 支持 `allowed-tools`、`context: fork`、`paths` 条件加载、Hook |
| **Codex**  | `$CODEX_HOME/skills` + 系统技能 + 插件技能；`skill-installer` 从官方仓库安装 |
| **DSH**    | 技能注册表 + 文件系统提供者 + 插件技能；文件热监视；`user-invocable` / 模型不可调用 |

------

### 4. 命令与提示模板

|               | Pi                     | OMP             | Claude                           | Codex               | DSH                     |
| ------------- | ---------------------- | --------------- | -------------------------------- | ------------------- | ----------------------- |
| Markdown 命令 | `prompts/*.md`         | `commands/*.md` | `commands/*.md` 或 `skills/`     | 无独立 prompts 目录 | `dsh-commands` 插件命令 |
| 可执行命令    | 扩展 `registerCommand` | **TS 命令模块** | 内置 local/local-jsx/prompt 三类 | 无                  | 插件注册                |
| MCP prompts   | 作为工具/技能          | 作为斜杠命令    | 作为斜杠命令                     | 未发现              | 不支持                  |
| 参数占位      | `$1,$@,${1:-default}`  | 同类            | `$ARGUMENTS/$1/$name`            | —                   | 插件自定义              |
| 内联 shell    | —                      | —               | `!`command``                     | —                   | —                       |

------

### 5. Hooks / 生命周期

|          | Pi                                                           | OMP                                    | Claude                                                       | Codex                                                        | DSH                                     |
| -------- | ------------------------------------------------------------ | -------------------------------------- | ------------------------------------------------------------ | ------------------------------------------------------------ | --------------------------------------- |
| 事件数   | 41                                                           | **50**                                 | ~34                                                          | 5–8（Pre/PostToolUse、SessionStart/End、UserPromptSubmit、Stop、Subagent*） | Cordis 原生事件                         |
| 实现形态 | 只有扩展事件处理器                                           | 扩展事件 + 独立 HookAPI 模块           | **8 种**：command/prompt/agent/http/mcp_tool/script/callback/function | command / mcp_tool                                           | 原生 + **兼容 Claude/Codex hooks.json** |
| 阻断语义 | 扩展可改/拦工具调用                                          | 同上                                   | exit 2 阻断、`permissionDecision`、`additionalContext`       | command hook + `mcp_tool`                                    | 桥接仅支持 command hook                 |
| 特色事件 | `project_trust`、`provider_stream_event`、`agent_before_settle` | 审批、计划评审、TTSR、子代理派生、重试 | PreCompact、PermissionRequest、FileChanged、Worktree*        | Interrupt、SubagentStart/Stop                                | 由插件服务自行定义                      |

------

### 6. 插件 / 包 / 市场

|            | Pi                       | OMP                           | Claude                                                       | Codex                         | DSH                                      |
| ---------- | ------------------------ | ----------------------------- | ------------------------------------------------------------ | ----------------------------- | ---------------------------------------- |
| 清单       | `package.json#pi`        | `package.json#omp/#pi`        | `.claude-plugin/plugin.json`                                 | `.codex-plugin/plugin.json`   | `package.json` + `dsh.bundle.patch`      |
| 可声明组件 | 扩展/技能/提示/主题      | 工具/钩子/扩展/命令/特性/设置 | 命令/代理/技能/Hook/MCP/**LSP**/输出风格/主题/工作流/例程/监控/通道/设置/依赖 | 技能/MCP/Hook/App + UI 元数据 | 任意 Cordis 服务/工具/UI 插件            |
| 市场       | npm/git 包（无插件市场） | **有**，兼容 Claude 市场      | **有**，官方市场 222 个插件                                  | **有**，本地/远程市场         | **有**，pnpm + 插件管理器                |
| 安装事务   | 简单增删                 | 有锁、特性选择、项目覆盖      | 安装命令需哈希确认                                           | 市场策略/认证                 | **快照回滚、版本对等检查、构建脚本审批** |
| 启用/禁用  | 设置文件                 | 插件/特性/项目                | 分 scope（user/project/local）                               | `[plugins.*] enabled`         | patch 行 `disabled`                      |

**Claude 的组件目录最“像应用商店”**：已安装目录里支持 LSP（csharp-lsp、gopls、rust-analyzer…）、输出风格、代码审查、提交命令、工作流等。
 **Codex 的插件最“声明式”**：插件不能注册新工具，只能带技能、MCP、Hook、App。
 **DSH 的插件最“系统级”**：可以注册服务、事件、工具、UI 面板，甚至改变 Agent 预设。

------

### 7. 规则 / 提示词 / 记忆 / 模型 / UI / 后台任务

| 能力     | Pi                                                | OMP                                                   | Claude                                         | Codex                                                | DSH                                    |
| -------- | ------------------------------------------------- | ----------------------------------------------------- | ---------------------------------------------- | ---------------------------------------------------- | -------------------------------------- |
| 规则     | `SYSTEM.md`、`AGENTS.md`                          | **TTSR 流规则**（正则/AST/裁判模型）                  | CLAUDE.md 层级 + `.claude/rules`（按路径条件） | AGENTS.md 层级 + `.rules` 执行策略                   | AGENTS.md/CLAUDE.md + 插件化系统提示段 |
| 记忆     | 会话 + 上下文文件                                 | 会话/规则/技能                                        | **自动记忆 + 项目记忆**                        | **MEMORY.md / vault / rollout 摘要**                 | 会话投影；长期记忆靠社区插件           |
| 模型扩展 | `models.json` + `registerProvider` + **虚拟模型** | 模型**角色**（smol/slow/plan/vision…）+ 配置档        | 模型选择器（非插件）                           | **模型目录本身就是扩展面**（提示词/工具/多代理能力） | `llm` 服务 + 多提供商路由              |
| UI 扩展  | 主题 + 消息/条目/工具渲染器                       | 主题 + 组件形状 + 思考渲染                            | 输出风格 + 状态栏 + MCP Apps                   | 插件 `interface` 元数据 + MCP Apps                   | **53 个客户端 UI 包 + 槽位系统**       |
| 后台任务 | 无                                                | **jobs + `omp ps`**                                   | 定时任务/工作流/后台子代理                     | **automations（rrule 心跳）**                        | **jobs + schedule**                    |
| 压缩     | 摘要 + 扩展钩子                                   | **5 种策略**（remote/snapcompact/handoff/shake/soft） | 结构化摘要 + 边界保留                          | 阈值压缩                                             | 压缩插件（pruner/basic/image-offload） |

------

## 四、每家最独特的一招

### Pi：工具暴露经济学

`codemode`（QuickJS 沙箱里用代码批量调工具）、`tool_search`（按需加载延迟工具）、`deferred/hidden/model-only` 暴露层级——解决“工具太多，模型上下文放不下”的问题。

### OMP：分层扩展 + 运行时全家桶

四层扩展并存；插件支持“特性选择”和设置 schema；运行时自带子代理、LSP、工作树、后台任务、Advisor、Collab、技能市场。它是“Pi 的工程化超集”。

### Claude：组件化插件 + 权限工程

插件可带命令、代理、技能、Hook、MCP、LSP、输出风格、工作流、监控；Hook 支持 8 种实现；子代理支持 fork、worktree、remote、后台、团队；插件安装命令要哈希确认。

### Codex：模型目录即扩展

`model-catalog.json` 不仅定义模型，还定义**每个模型的提示词模板、工具模式、推理档位、是否支持多代理**；配置分层可被 MDM/企业策略覆盖。扩展能力最“声明式”。

### DSH：微内核 + 组合式装配

Cordis 的 `Context/Service/Fiber` 让插件注册服务、事件、工具、UI；`patch.yml` 把包组合成“运行镜像”；Agent 预设是子树；作用域隔离避免同级插件互相污染。它最像“Agent 操作系统”。

------

## 五、共同趋势与真正的分歧

### 已经趋同的 4 件事

1. **技能格式统一到 `SKILL.md`**（Agent Skills 规范）。
2. **MCP 成为跨内核标准**，区别只在暴露控制和传输支持。
3. **插件清单 + 市场 + 启用/禁用**成为通用形态。
4. **Hook 事件命名跨内核靠拢**，DSH 甚至直接提供 Claude/Codex Hook 兼容桥。

### 真正的分歧只有 4 个

| 分歧                       | 表现                                                         |
| -------------------------- | ------------------------------------------------------------ |
| **代码扩展 vs 声明式扩展** | Pi/OMP/DSH 允许进程内代码；Codex 基本只允许声明；Claude 介于两者之间 |
| **项目信任**               | Pi/Claude/Codex 有门禁；OMP 明确没有；DSH 靠沙箱/审批而非目录信任 |
| **隔离级别**               | 多数是“同进程、同权限”；只有子代理/任务有工作树隔离；真正的 OS 沙箱仍少 |
| **热更新**                 | Pi 最彻底（重载扩展代码）；DSH 配置/模板级热更新；其余多数要重启 |

------

## 六、对 FUMO 的轻量启示（如果要继续扩展）

FUMO 目前只有“宿主插件工具 + 人格 + 静态命令”，可以按成本从低到高补：

1. **先做统一插件清单**（低成本）：一个 `plugin.json`，声明 `tools / skills / commands / mcp / hooks`，复用现有 `PluginToolRegistry` 与 `IPluginToolCatalog`。
2. **接 MCP 客户端**（中成本）：stdio + Streamable HTTP，工具命名 `mcp__server__tool`，按需加载。这是投入产出比最高的扩展面。
3. **技能目录扫描**（低成本）：`SKILL.md` 渐进披露 + `/skill:name`，与五家格式兼容。
4. **Hook 事件接缝**（中成本）：先做 `PreToolUse/PostToolUse/SessionStart/Stop/UserPromptSubmit`，支持命令型 Hook；不要一开始支持任意进程内代码。
5. **子代理**（高成本）：先做“只读探索子代理 + 独立 JSON 会话”，不要立刻上团队/邮箱/看板。
6. **安全底线**：项目级插件/技能必须经过信任确认；执行型扩展要么进程隔离，要么明确标注“同权限运行”。

**总结**：五家的扩展体系看似五花八门，实际都收敛为 **“技能 + MCP + Hook + 插件清单 + 子代理”** 五个面；差别主要在**代码执行深度、信任模型和运行时隔离**。Pi 给出最小可用形态，OMP/DSH 把它扩展成平台，Claude 做成组件化市场，Codex 则把扩展压进配置与模型目录。