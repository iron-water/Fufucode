> **当前后端架构（2026-10-17）**：默认后端为 **FUMO 原生内核（纯 C# 实现）**；Pi/OMP/Codex/DSH/Claude 为可选后端插件，默认不随包发布。
> 下文中关于 Pi 运行时的描述是历史基线，不代表默认安装依赖。

# 运行基线

## 1.1.0 当前运行基线

更新日期：2026-10-08（用户时区）。当前 `main` 的 `runtime/pi/package.json`、lock 和实际安装均锁定 `@earendil-works/pi-coding-agent@1.1.0`，Node 保持 24.15.0。npm latest 与 GitHub latest release 同为 1.1.0；官方 tag `v1.1.0` 提交 `abe508e1b89912adde45528136c3221eb69acdd7`。下方 0.87.1/1.0.0 分支说明为历史记录，不代表现在的 main。

旧运行环境完整保留于 `tmp/pi-upgrade-2026-10-08/pi-1.0.0-backup/`。升级没有迁移生产会话，也未替换运行中的用户进程；新启动的受管 Pi 使用新版。

落锁入口：

```powershell
./scripts/pi-probe/run-probe.ps1 -PiRoot runtime/pi -ComparePiRoot tmp/pi-upgrade-2026-10-08/pi-1.0.0-backup -BaselineReport tmp/pi-upgrade-2026-10-08/candidate-rebased/probe-report.json -Output tmp/pi-upgrade-2026-10-08/locked-final -SelfTest -Features
```

结果：**11 PASS / 0 FAIL / 0 SKIP**，套件自测 **14 PASS / 0 FAIL**。01–10 检验启动/模型、entries 游标、流式 settled、队列取消、模型切换、真实 bridge 放行/拒绝、扩展故障、内置来源与双向 JSONL 副本读写；11 检验显式内置 codemode/MCP + bridge 的真实 QuickJS、stdio MCP、嵌套 write 审批及 store entry。模型端点为本机合成 SSE，不是云供应商。原始证据在对应目录的 RPC、modelRequests、MCP 调用与 SDK loader 文件。

最初两份探针在 10 项失败：脱敏 fixture 的 header.cwd 为不存在的 `C:\fixture\project`。Pi 1.1.0 恢复该 cwd 并拒绝启动；修正**副本头部**为隔离测试目录后 10 项通过。所有 entries 和 checked-in fixture 保持不变，完整比较原 entry 的断言未放宽；失败证据保留在 `baseline/`、`candidate/`。`locked/` 是安装尚未结束时误提前运行的旧版报告（同版本比较失败），不计入升级通过依据；最终只以 `locked-final/` 为准。

四个默认内置 factory 仍是 llama.cpp、codemode、tool-search、mcp；后三者 replaceable。普通 named inline 仍 `<inline:name>`，builtin 为 `builtin:<name>`。产品依旧 `--no-extensions` 并显式加载 bridge/选定扩展，因此本轮升级**没有默认启用** codemode/MCP；隔离探针确认显式 `builtin:*` 可以接入且嵌套审批生效。

1.1.0 新增 `agent_settled.aborted` 与工具 `durationMs`；现有客户端保留 raw 字段并继续按既有停止/消息结果判定，本轮未新增专用 UI 映射。没有虚构能力协商或 MCP/Durable/subagent RPC 命令。

边界：双向 fixture 兼容不等于全部生产会话升级/回退演练；未验证远程 MCP、真实 OAuth、Durable、子代理或图像/分类服务。具体接入矩阵见 `docs/research/pi-native-capabilities-2026-10-08.md`，本轮交付见 `docs/records/pi-upgrade-2026-10-08.md`。

## 历史运行基线

分支状态（2026-10-02）：`main@cf57fe5` 仍锁定 0.87.1；`track-a-pi10` 的 runtime/package/lock/实际安装为 1.0.0，尚未合并、重包或执行生产数据升级/回退演练。下表保留 main 的发布基线；候选分支实测见末尾「1.0.0 探针」。

验证日期：2026-09-28，Windows x64。

| 项目 | 已验证值 |
|---|---|
| .NET SDK | 10.0.401 |
| Avalonia | 12.1.3，`net10.0` |
| Node | 24.15.0，`runtime/node/node.exe` |
| Pi npm 包 | `@earendil-works/pi-coding-agent@0.87.1` |
| 安装来源 | npm registry，项目内 `runtime/pi/node_modules` |
| 锁文件 | `runtime/pi/package-lock.json` |
| Pi CLI | `runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js` |
| 官方文档版本 | GitHub tag `v0.87.1`，提交 `f07218c4d4bbc12bef056a7058c3dd49dfe41abe` |

能力表：

| 命令/事件 | 0.87.1 状态 |
|---|---|
| `get_state` | 真实 RPC 已验证，成功返回 `isStreaming` 等字段 |
| `get_available_models` | 真实 RPC 已验证，成功返回 `models` 字段；未调用模型 |
| `response` 按 ID 关联 | 真实 Pi 查询及 FakePi 乱序响应已验证 |
| `prompt`、`agent_settled` | M2 Core 已用当时的 Pi 配置完成真实模型两轮对话；M4 应用专属配置仍缺 API key，持久化对话待验 |
| `handshake`、`get_capabilities` | 未使用，官方 RPC 文档没有将其列为能力协商命令 |

M4 Core 实测补充（2026-09-28）：

- 0.87.1 的 `get_entries` 在真实 RPC 成功；支持 `since` 游标（FakePi 管道验证增量语义）。它覆盖压缩前及遗弃分支历史，不能以 `get_messages` 代替。
- `--session-dir` 与 `PI_CODING_AGENT_DIR` 可将会话与配置引向应用专属目录。新会话在首个持久化写入前，`get_state.sessionFile` 已有路径但文件尚不存在；`--name` 和 `set_session_name` 不使空会话落盘。同 ID 可再次启动，但未写盘时路径中的时间戳可能变化。
- 应用专属 `pi-agent` 当前无 API key：真实 prompt 被拒绝。真实模型写盘与恢复尚未验证；没有导入或复制全局 Pi 凭据。

资料：[v0.87.1 RPC 文档](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/rpc.md)、[命令文档](https://github.com/earendil-works/pi/blob/v0.87.1/packages/coding-agent/docs/rpc-commands.md)。

F01 项目信任 RPC 补验（2026-09-29）：`scripts/verify-project-trust.mjs` 在 `tmp/project-trust-probe/` 创建隔离项目与应用专属 Pi 配置，项目内放一个扩展命令和一个技能，用本仓库固定的 Node 24.15.0 / Pi 0.87.1 对 `get_commands` 取证：默认 `ask` 与显式 `--no-approve` 均不出现项目命令/技能；`--approve` 两者均出现；`--approve --no-extensions` 只出现项目技能。四种组合均通过；未发送模型 prompt，也未读取用户项目。此结果只证明这些资源的加载行为，不表示项目信任能限制工具的文件访问。

## 0.99.2 探针

实测日期：2026-10-01，Windows x64，Node 24.15.0。产品仍锁定 **0.87.1**；以下是候选差异证据，不是升级决定。`@earendil-works/pi-coding-agent@0.99.2` 精确旁装至 `tmp/pi-0.99.2/`（独立 package/lock，npm --ignore-scripts），没有更新 `runtime/pi`。

### 可复现调用与证据

在 A worktree 根目录执行，均退出 **0**：

```powershell
./scripts/pi-probe/run-probe.ps1 -PiRoot runtime/pi -ComparePiRoot tmp/pi-0.99.2 -Output tmp/pi-probe-0.87.1-isolated-final -GenerateFixture -SelfTest
./scripts/pi-probe/run-probe.ps1 -PiRoot tmp/pi-0.99.2 -ComparePiRoot runtime/pi -BaselineReport tmp/pi-probe-0.87.1-isolated-final/probe-report.json -Output tmp/pi-probe-0.99.2-isolated-final -SelfTest
```

每个输出目录保存 `probe-report.json` / `probe-report.md`、场景目录下 `rpc-*.json`（实际响应、事件、stderr、合成 HTTP 请求），候选目录另有 `probe-diff.json`。模型为隔离 `probe/probe-model` 本机随机端口 SSE，非云供应商。RPC 参数禁用 skills/prompts/themes，保持默认内置扩展；另外测试 `--no-extensions`。审批按产品参数组合禁自动扩展发现并显式加载真实 fufucode-bridge。

### 逐项结果

| ID | 实际检验 | 0.87.1 | 0.99.2 |
|---|---|---|---|
| 01 | get_state + 非空 get_available_models | PASS | PASS |
| 02 | get_entries 全量、since 排除游标、leafId、无效游标拒绝 | PASS | PASS |
| 03 | prompt 普通 text_delta、合成正文、agent_start→agent_end→agent_settled | PASS | PASS |
| 04 | streaming 中 steer/follow_up，clear_queue 两个精确字符串数组/第二次清空，abort 后 idle | PASS | PASS |
| 05 | set_model 成功返回模型对象、不存在模型失败 | PASS | PASS |
| 06 | 真实桥 confirm 允许/拒绝与磁盘效果，显式桥在 --no-extensions 下注册 | PASS | PASS |
| 07 | 坏显式扩展启动 code=1/stderr/无 ready；运行时 agent_start handler 抛错产生 extension_error | PASS | PASS |
| 08 | SDK 普通 inline、builtin factory、实际默认内置列表、故障 factory 的来源名 | PASS | PASS |
| 09 | get_commands 字段快照、默认与禁扩展对照 | PASS | PASS |
| 10 | fixture 两版本各写后由对方完整读并追加、已有 entry 内容保持 | PASS | PASS |

每版 **10 PASS / 0 FAIL / 0 SKIP**。套件自测 **14 PASS / 0 FAIL**：4 项直接运行实际 .NET FakePi 管道（启动、fixture 游标、stream、空队列），10 项向共享断言注入错误并要求 AssertionError。自测不能替代真实 SDK/桥/模型清单/跨版本探针；FakePi 对 get_available_models/steer/follow_up 仅保留 ACK-only 原行为，注册表已明确 IsSimulated=false。

### 差异报告（供协调者决策）

| 分类 | 证据与影响 |
|---|---|
| 行为变化，命名/禁用口径需审阅 | 旧 `llama` 的 sourceInfo 为 `path:<inline:llama.cpp>,source:inline`；新版为 `path:builtin:llama.cpp,source:builtin`。SDK 的失败 builtin factory errors.path 同样使用 `builtin:probe-broken-builtin`，普通 named/broken inline 两版仍 `<inline:name>`。**不是全局 inline→builtin 替换**。诊断保留原路径/来源，不能把 builtin 当磁盘扩展路径或统一替换全部 inline。 |
| 行为变化，影响产品命令面 | 旧 `--no-extensions` 下仍有隐藏 inline `/llama`；新版本下默认命令列表为空。产品 PersistentSessionManager 固定带此参数，因此升级后不能假定 `/llama` 自动存在。它仍显式加载 bridge/用户扩展；两版 bridge 的 status command 均注册为 source=extension，实际允许写入、拒绝不写入通过。Core 探针识别 name+source，没有匹配旧 inline 名称；本轮未发现该命名变化使审批桥失效。 |
| 新增能力 | 新版实际默认内置 factory 为 llama.cpp、codemode、tool-search、mcp（均 builtin:true，后三个 replaceable:true）；旧仅 hidden llama.cpp。默认 get_commands 新增 `/mcp`，旧 `/llama` 保留但来源改变；codemode/tool-search 是工具，不应推测为 slash command。本轮未调用 MCP/llama 服务或验证新增工具效果。 |
| 本轮范围内行为等价 | 启动/模型、游标、普通 SSE→settled、队列与取消、模型失败、显式 bridge、错误事件、fixture 文本数据往返均通过上述实际断言。不是所有 RPC 或生产会话格式完全兼容的承诺。 |

JSONL fixture `scripts/pi-probe/fixtures/pi-0.87.1.jsonl` 由真实锁定版 + 合成端点生成，header version=3；provenance 记录版本、时间、报告位置。两个方向分别让 producer 追加、consumer RPC 全量读取并追加，逐条比较已有 entry 的 ID/parentId/内容。只检验该 fixture 的模型/思考配置与普通文本；未验压缩、fork 分支、生产旧数据、真实供应商重试、多媒体或 metadata 迁移。双向 fixture PASS 不能代替升级/回退演练。

### 失败前轮与边界

首次错误预期坏显式扩展仍启动，实测它会退出；改为检验 code=1+stderr+无 ready。其次 tool_call handler 抛错走工具阻止结果，不是一般 extension_error，改用 agent_start handler 核实一般错误事件。早期快照发现用户 `.agents/skills` 会被发现，最终加禁 skills/prompts/themes 参数，使用上述独立 isolated-final 报告；早期失败报告未删除。FakePi 自测首次传相对 session 路径重复拼接，修为绝对路径，最终14项通过；未改产品测试断言。

没有执行升级、重包、真实用户数据跨版本升级/回退；没有批准 candidate 进入产品。未知版本命名 oracle 明确 SKIP，待核实后再补；详细准入/失败动作见 `docs/operations/pi-upgrade-sop.md`。

## 1.0.0 探针（A11+A12，2026-10-02）

### 授权、分支与安装

用户明确授权：main 上零提交探针；两版 10/10 且 JSONL 双向 PASS 后执行 `git checkout -b track-a-pi10`，只在该分支升级及提交，不合并、不 push、不重包、不跑跨版本 drill，Core/Pi 公共契约冻结。

开工时 A10 的 0.99.2 未提交改动先完整保存为 `tmp/a10-preserved-before-pi10.patch`，再恢复 main 文件并对 `runtime/pi` 执行 `npm ci --ignore-scripts --no-audit --no-fund`，实际版本核对为 0.87.1。main/阶段二起点均为 `cf57fe5eef93012d092a18e0a10649cfb3f5c2c3`。旧环境副本 `tmp/pi-0.87.1-pre-a10/` 保留。旁装和升级均使用精确 `@earendil-works/pi-coding-agent@1.0.0`，没有 latest；Node 24.15.0。阶段二 package、lock 的根依赖及 node_modules 包版本均为 1.0.0。

### 探针命令与门

以下从 A worktree 执行，各入口实际退出 0：

```powershell
./scripts/pi-probe/run-probe.ps1 -PiRoot runtime/pi -ComparePiRoot tmp/pi-1.0.0 -Output tmp/a11-pi-probe-0.87.1 -SelfTest
./scripts/pi-probe/run-probe.ps1 -PiRoot tmp/pi-1.0.0 -ComparePiRoot runtime/pi -BaselineReport tmp/a11-pi-probe-0.87.1/probe-report.json -Output tmp/a11-pi-probe-1.0.0 -SelfTest
# 阶段二安装后再次确认实际落锁目录
./scripts/pi-probe/run-probe.ps1 -PiRoot runtime/pi -ComparePiRoot tmp/pi-0.87.1-pre-a10 -BaselineReport tmp/a11-pi-probe-0.87.1/probe-report.json -Output tmp/a12-locked-probe -SelfTest
```

| ID / 检验 | 0.87.1 基线 | 1.0.0 旁装 | 1.0.0 落锁 |
|---|---|---|---|
| 01 启动、状态、模型清单 | PASS | PASS | PASS |
| 02 entries 全量/since/无效游标 | PASS | PASS | PASS |
| 03 text_delta 与 agent_settled 终态 | PASS | PASS | PASS |
| 04 steer/follow_up/clear_queue/abort | PASS | PASS | PASS |
| 05 模型切换及无模型失败 | PASS | PASS | PASS |
| 06 显式真实审批桥放行/拒绝及磁盘效果 | PASS | PASS | PASS |
| 07 扩展启动失败及运行时 extension_error | PASS | PASS | PASS |
| 08 SDK inline/builtin 来源及实际 factory | PASS | PASS | PASS |
| 09 get_commands 默认/禁扩展快照 | PASS | PASS | PASS |
| 10 JSONL 两版互读、续写、原 entry 保持 | PASS | PASS | PASS |
| 合计 / 自测 | 10 PASS / 14 PASS | 10 PASS / 14 PASS | 10 PASS / 14 PASS |

无 FAIL/SKIP；0.87.1→1.0.0 和 1.0.0→0.87.1 均实际读写副本并保持原 entry 的 ID/parentId/内容。阶段一由此满足用户自判门。原始响应/事件/SDK 数据保存在各报告的场景目录，候选及落锁目录有 `probe-diff.json`。模型请求均为本机隔离合成 SSE，不是云供应商验收。

### 版本差异与最小适配

1. 1.0.0 默认内置 factory 实测为 `llama.cpp`、`codemode`、`tool-search`、`mcp`，均 builtin:true，后三者 replaceable:true。探针新增该版本明确 oracle 与精确 flags 检查，既有版本断言保留；普通 named inline 仍 `<inline:name>`，builtin 使用 `builtin:<name>`。
2. `get_commands` 相比 0.87.1 增加 `/mcp`；`/llama` 从 `inline:<inline:llama.cpp>` 来源改为 `builtin:builtin:llama.cpp`（前项为 source，后项为 path）。1.0.0 的 `--no-extensions` 默认清单为空，0.87.1 仍带隐藏 llama。codemode/tool-search 为工具，不作为 slash command 声明。
3. 产品 `PersistentSessionManager` 已禁默认扩展并显式加载审批桥/所选用户扩展，命令查询透传；实际 bridge 注册与放行/拒绝通过，无需修改 Core/Pi 实现或公共签名。FakePi 的版本、builtin 常量/注册表来源注释对齐，未扩大其原有模拟能力；测试断言文件未改。setup-runtime 的显示版本改读实际包，避免新版本仍打印 0.87.1。
4. `defaultTools:["+codemode"]` 三组真实 1.0.0 请求对照均 PASS：产品 `--no-extensions`+显式 bridge 的请求仅 read/bash/edit/write，不含 codemode；默认扩展+bridge、显式 `builtin:codemode` 两组含 codemode。证据 `tmp/a12-default-tools/run-1790928041656/report.json` 及各 case 的请求/RPC；复现脚本 `tmp/a12-default-tools/verify.mjs`。核实来源 main.js:451/633/640、resource-loader.js:403-407/523-535、codemode/index.js:5-6/16-26。没有加入会限制用户扩展的工具硬白名单。

### Fixture 与验证

追加 `scripts/pi-probe/fixtures/pi-1.0.0.jsonl` 和对应 provenance；来自实际 1.0.0 RPC 持久化会话，逐字节复制，header version=3，6 entries，13016 bytes，SHA256 `5ec8b21f0e1b367fb39e9de14256b485aed8825364f221690dc5eef3126682e5`。原 0.87.1 fixture/provenance 未变；其 SHA256 仍为 `1D381D1D7A5DB3A3E24DDA92E9E91B9008BB26605E49B2A541CEEC89BCBB28A4`。provenance 记录实际版本、来源/时间、parent links、正文及 usage；合成会话不含生产凭据。

| 实际入口 | 结果 / 证据 |
|---|---|
| `dotnet build Fufucode.sln --no-restore -v quiet` | exit 0，0 警告/0 错误；`tmp/a12-build.log` |
| `dotnet tests/Fufucode.Core.Tests/bin/Debug/net10.0/Fufucode.Core.Tests.dll` | exit 0，原有 272 PASS；`tmp/a12-core.log`。断言未改；部分历史输出仍写 0.87.1，实际运行目录已核实为 1.0.0 |
| `dotnet tests/Fufucode.Pi.Tests/bin/Debug/net10.0/Fufucode.Pi.Tests.dll` | exit 0，18 PASS；`tmp/a12-pi.log` |
| `dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest` | exit 0，0 项失败；`tmp/a12-selftest.log`（未修改 App 源码） |
| `./runtime/node/node.exe scripts/cli-real-verify.mjs` | exit 0，实际 Pi+合成模型对话完成、允许写入/拒绝不写、缺凭据/缺模型失败边界；5 HTTP requests，`tmp/a12-cli-real.log`、`tmp/cli-real-1790928063047/` |

Core 可选 Atria 真模型组未显式启用，按原逻辑 SKIP（需 FUFUCODE_ATRIA_REAL_MODEL_TEST=1 和凭据）；未宣称云模型验证。未测试 MCP/llama 服务、codemode 执行效果、压缩/fork/重试/多媒体生产数据兼容。fixture 双向 PASS 不能代替 SQLite/项目归属/锁/设置及生产会话跨版本演练。App 源码未改；重包、drill 和 main 合并交协调者，只有独立候选分支完成升级。
