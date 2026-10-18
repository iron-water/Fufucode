# Pi 原生能力接入核查

日期：2026-10-08（用户时区）。发布基线：`@earendil-works/pi-coding-agent@1.1.0`；官方 tag `v1.1.0`，提交 `abe508e1b89912adde45528136c3221eb69acdd7`。npm latest 与 GitHub latest release 已分别查询。本文讨论当前发布版，不把 main 或路线图当作已发布能力。

## 结论

继续使用 Pi 官方 RPC。模型调用、工具循环、codemode 执行、MCP 连接与认证、工具发现、会话/分支/压缩均优先由 Pi 执行。Fufucode 负责桌面入口、展示、应用审批和进程管理。

**Pi Durable 不能直接接到当前 JSONL RPC；普通 Pi 的 subagent 是官方扩展示例，Durable 的 subagent 则属于另一套实验 harness。** 不应为了追新立即自研重复引擎，也不应将实验 API 当成可直接升级的 RPC。

## 能力对应表

| 功能 | Pi 1.1.0 的真实入口 | Fufucode 应做什么 | 核查状态 |
|---|---|---|---|
| 模型/provider、流式响应、重试、thinking | RPC `get_available_models`、`set_model`、`get_available_thinking_levels`；Pi provider runtime | 复用；只维护配置、选择和错误展示 | 既有架构已复用；本轮模型清单与切换实测 |
| 工具执行与队列/取消 | RPC `prompt`、`steer`、`follow_up`、`clear_queue`、`abort`；生命周期事件 | 复用；停止仍先清队列再 abort | 本轮实测 |
| Codemode | `-e builtin:codemode`，激活 `--tools +codemode` 或 `defaultTools:["+codemode"]` | 无需自写 JS executor；展示脚本和嵌套工具；设置开关 | 本轮真实 QuickJS、工具调用和审批实测 |
| Codemode 小状态持久化 | `store/load`；Pi 的 `codemode-store` custom entry，分支可见性由 Pi 管理 | 不建第二份权威状态存储；不要用它代替桌面数据库 | 本轮确认写入会话；重启/分支 store 恢复未单独实测 |
| MCP stdio/streamable HTTP | `-e builtin:mcp`；应用专属 agent dir 的 `mcp.json`，受信任项目的 `.pi/mcp.json` | 复用客户端、连接、工具列表和调用；设置页读写 Pi 原生格式 | 本轮本地 stdio 服务调用实测；HTTP 未实测 |
| MCP OAuth/CLI 管理 | `pi mcp add/remove/list/login/logout`，会话 `/mcp login/logout/reconnect` | 调用受管 Node/CLI，复用认证实现；保留用户交互；不自写 OAuth | 官方文档/源码确认；真实登录未实测 |
| MCP 管理界面 | TUI `/mcp` 自定义界面；RPC `/mcp` 返回状态通知 | Avalonia 自写列表/开关/错误展示；不能嵌入 TUI；结构化状态需窄桥接或 CLI 解析约定 | 源码确认 `ctx.mode === "tui"` 分支；不是原生 MCP RPC 管理 API |
| 工具搜索/延迟暴露 | `builtin:tool-search` 的 `tool_search`；codemode 的 `searchTools/describeTool/describeNamespace/ALL_TOOLS` | 复用检索/暴露机制；无需自写工具检索器 | 文档/源码；本轮 MCP 默认 codemode 暴露调用实测；搜索排名未实测 |
| 分类/图像模型 | Codemode `models.classify()`、`models.generateImages()`；Pi 原生 model runtime 和凭据 | 复用模型访问；桌面处理图片、附件与产物显示 | 官方文档；真实服务调用未实测 |
| 会话、历史、fork、tree、压缩、统计/export | Pi 官方 RPC 会话命令及事件 | 复用；SQLite 只管项目索引、草稿、布局等 | 双向 fixture 读写实测；完整生产数据迁移未实测 |
| 插件、skills、prompt templates | Pi resource loader、扩展 API、包/资源配置；`get_commands` | 复用运行与加载；桌面保留启停/管理和加载诊断 | 本轮扩展来源、坏扩展失败、bridge 显式加载实测 |
| 扩展交互 UI | `extension_ui_request/response`：select/confirm/input/editor 和通知/状态文本 | 复用 RPC；Avalonia 映射控件；TUI custom/renderer/editor 不通用 | 当前版本文档确认，审批交互实测 |
| 审批/计划/自动/完全访问模式 | Pi `tool_call` hook 支持阻止，但不提供本项目模式策略与 Windows 沙箱 | 保留 fufucode-bridge；不要把项目信任当沙箱 | 本轮直接及 codemode 嵌套 write 的放行/拒绝实测 |
| Subagent（普通 Pi） | `examples/extensions/subagent`，独立 Pi 子进程、孤立上下文、单个/并行/链式任务 | 可先复用官方扩展逻辑；需受管 node/cli、子进程清理、配置继承、审批和任务视图适配 | 官方示例源码；未启用或运行子代理；不属于四个默认内置扩展 |
| Pi Durable / task graph / Durable subagents | 独立 `@earendil-works/pi-durable` 的 Harness/Conversation/Storage API | 等独立实验验证后做 Node 桥或另一个 backend；不能把其库 API 当现有 RPC 命令 | 官方源码/文档；未安装、迁移或运行 Durable |
| 项目/worktree、桌面 tab/dock、计划任务、通知、安装/升级 | 属于本项目的产品/Windows 集成 | 继续由 Fufucode 实现；Pi 工具/扩展可以参与，但没有等价完整桌面功能 | 项目职责判断，不是 Pi 缺陷 |

## 现在为何没有自动启用新能力

`src/Fufucode.Core/PersistentSessionManager.cs` 的受管会话带 `--no-extensions`，再显式载入审批桥和用户选定的扩展。该参数同时禁用默认内置扩展；单改 Pi 版本或只设置 `defaultTools` 都不能使未加载的 codemode 工具出现。

推荐沿用受控加载，新增产品功能选项后显式加入：

```text
--no-extensions
-e <fufucode-bridge>
-e builtin:codemode
-e builtin:tool-search
-e builtin:mcp
--tools +codemode
```

这是已在隔离实验验证的组合，不是本轮已启用的产品默认值。`tool_search` 若要直接向模型暴露仍需激活；MCP 默认 `codemode` 暴露可自动激活 codemode。不要改成只允许固定工具的硬白名单，以免破坏用户扩展。MCP JSON 中的 `${NAME}`、认证文件等不复制到研究报告。

本轮 `-Features` 探针实际使用上面的参数和本机合成模型：codemode 并行调用 write 与本机 MCP echo，验证实际磁盘结果、MCP 服务器收到调用、内层 write 弹出 bridge 审批、codemode 工具成功及 `codemode-store` entry 写入。允许与拒绝两组均 PASS。不是只看工具声明，也不是云供应商或远程 OAuth 验收。

## Durable 与 subagent 边界

`packages/durable/README.md` 明确 API 实验性、跨 release 可无通知变化。Harness 将会话、工具、模型执行和状态提交到 Storage；支持子 conversation、取消传播和 task graph。恢复不能解释为任意外部副作用可 exactly-once 重放。

`packages/coding-agent/src/experimental/durable/README.md` 提供单独的源代码启动入口，使用 `session.sqlite`；示例明确尚不包含现有 sessions picker、fork/tree、extensions、prompt templates、images、login。这和 Fufucode 当前 Pi JSONL 会话及 bridge 扩展不是同一后端。coding-agent npm 包的 `files` 排除了 `dist/experimental`，升级 npm 包不会得到该实验 CLI。

普通 Pi 的官方 subagent 示例可作较轻的短期接入来源，但不是免适配的内置服务。必须核查它启动的 `pi` 是否落到全局 PATH，以及子进程是否继承 Fufucode 的审批/agent dir；父进程审批一次 subagent 工具不等于所有子工具受到本项目策略控制。当前不声称未来版本会提供某个 RPC 命令。

## 持续追新顺序

1. **先接 codemode + MCP + tool-search**：复用原生执行；增加设置、受控 builtin 加载、MCP 配置/认证入口和嵌套工具展示。用本轮行为探针作准入依据。
2. **接新版事件增量**：`agent_settled.aborted` 和工具 `durationMs` 可改善结果判定和耗时显示；落锁 raw RPC 实际观察到正常任务 `aborted:false`、取消任务 `aborted:true`。当前客户端保留未知字段，已有停止判定继续工作。本轮未新增这两个字段的专用 UI 映射。
3. **Subagent 先保留适配边界**：若短期需要，基于官方扩展做受管进程/审批适配；不要先另写 agent loop。等待稳定原生接口时可替换实现。
4. **Durable 单独试验**：只在隔离副本验证存储、crash recovery、工具副作用、取消/子任务及功能差异，再决定是否增加后端；不直接迁移用户会话。
5. **按需人工升级**：检查 npm/release、锁精确版本、有限旁装探针、落锁验证、构建与交付记录；不在应用启动时自动 npm latest，不擅设定时任务。

## 官方依据与证据

- [1.1.0 release](https://github.com/earendil-works/pi/releases/tag/v1.1.0)
- [CLI/工具/显式 builtin 加载](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/cli.md)
- [Codemode](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/codemode.md)、[MCP](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/mcp.md)、[RPC](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/rpc-commands.md)、[RPC UI 限制](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/rpc-extension-ui.md)
- [内置扩展注册表](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/src/extensions/index.ts)、[Codemode 内层执行](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/src/extensions/codemode/execute.ts)
- [Durable](https://github.com/earendil-works/pi/blob/v1.1.0/packages/durable/README.md)、[实验 Durable CLI](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/src/experimental/durable/README.md)、[官方 subagent 示例](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/examples/extensions/subagent/README.md)
- 本机证据：`tmp/pi-upgrade-2026-10-08/features/probe-report.json`（11 PASS），各场景 RPC/modelRequests/MCP 调用记录。以上 Durable 资料由认证 `gh api` 获取到 `tmp/pi-upgrade-2026-10-08/upstream/`；其他文档与源码核查来自实际 1.1.0 安装包。
