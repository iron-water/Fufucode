# 轨道 B 进度：Fufucode 工具审批桥接扩展

> 任务来源：`docs/archive/assignments/track-b-assignment.md`（M3 扩展部分提前并行）。工作分支 `track-b`，工作区 `C:\Users\21894\.codex\worktrees\track-b-bridge\Fufucode`。

## 工作区与提交记录

- worktree：`C:\Users\21894\.codex\worktrees\track-b-bridge\Fufucode`（`git worktree add ... -b track-b main`，基线 `1107875`）。遵守 `AGENTS.md` 的 worktree 布局，未在共享主工作区或他人工作区提交。
- 提交（只含本轨独占文件 `extensions/fufucode-bridge/`、`scripts/bridge-verify/`、`docs/archive/progress/progress-b.md`）：
  - `e8db222` 桥接扩展本体（policy + tool_call 门禁 + status 探针命令）
  - `9cb073e` 验证驱动框架（RPC 客户端、stub 本地模型、A01–A03 场景）
  - `de37f42` 修复 A02（ui_request 事件化）；A03 坏扩展按致命启动退出处理
  - `c8ef183` 新增 a01-bash-deny（命令未执行证据）；记录全套结果

## 产物

- `extensions/fufucode-bridge/index.ts` —— `tool_call` 钩子，`ctx.ui.confirm` 经 `extension_ui_request/response` 往返；参数快照校验；无 UI 时 fail-safe 阻止。
- `extensions/fufucode-bridge/policy.ts` —— 可配置策略（`FUFUCODE_BRIDGE_CONFIG` 或扩展目录 `bridge.config.json` 覆盖默认值）。
- `extensions/fufucode-bridge/package.json` —— `pi.extensions` 声明入口。
- `scripts/bridge-verify/lib/pi-rpc.mjs` —— JSONL 帧解析、对话框自动应答器。
- `scripts/bridge-verify/lib/stub-model.mjs` —— 本地 OpenAI 兼容 stub 模型（驱动真实 agent 循环）。
- `scripts/bridge-verify/run.mjs` —— 场景驱动器；结果写 `bridge-verify-results.json`。

## 验证结果（真实 Pi，0.87.1，2026-09-28 实跑）

运行环境：`runtime/node/node.exe` v24.15.0 + `runtime/pi` 的 `dist/bundle/cli.js`；`PI_CODING_AGENT_DIR` 隔离配置目录；stub 模型经 `http://127.0.0.1:<port>` 提供 openai-completions 流。

命令：
```
cd C:\Users\21894\.codex\worktrees\track-b-bridge\Fufucode\scripts\bridge-verify
C:\Projects\Fufucode\runtime\node\node.exe run.mjs all
```

7/7 通过（证据为每次场景创建的临时目录；断言直接检查文件系统，不看消息内容）：

| 场景 | 结果 | 关键断言（证据） |
|---|---|---|
| a01-allow | PASS | 批准后目标文件**确实生成**（`existsSync(target)`=true），1 次 confirm 往返 |
| a01-deny | PASS | 目标文件**不存在**（未写盘），1 次 confirm 往返 |
| a01-timeout | PASS | 对话框超时（策略配置 1500ms，不人工应答）；目标文件**不存在** |
| a01-bash-deny | PASS | shell 命令**未执行**（标记文件不存在，仅检查消息不足以证明） |
| a02-nonblocking | PASS | 审批挂起期间 `get_commands` 仍被消费并应答（18 条命令，含侧探测本身） |
| a03-missing | PASS | 扩展未加载 → `get_commands` 无 `fufucode-bridge.status` → 报"审批不可用" |
| a03-broken | PASS | 坏 `-e` 扩展使 Pi 启动即退出（code=1）；驱动脚本识别并报"审批不可用" |

### 关键实现决策与核实依据（从源码核实，非凭记忆）

- `emitToolCall`（`dist/core/extensions/runner.js:852`）按扩展顺序串行，`result.block` 为真立即 return；`event.input` 可原地修改。→ 批准前后用 `JSON.stringify(event.input)` 快照比对，变化即重审或拒绝。
- `block` 在 `pi-agent-core/dist/agent-loop.js:491-510` 被消费为 `createErrorToolResult(reason)` + `isError:true` **立即返回**，工具不执行 —— 这是 A01 "文件/命令未变" 的底层依据。
- RPC confirm（`dist/modes/rpc/rpc-mode.js:60-90`）：超时 / `signal.abort` / `cancelled:true` 一律 resolve `false`；未超时无默认 true。→ 拒绝/取消/超时天然都按拒绝处理。
- `handleInputLine`（同文件 :604/629）以 `void` 分发、不串行 await，因此审批等待期间 stdin 帧仍被消费 —— A02 可测且已验证。
- A03 探针：`get_commands`（:542）列出扩展注册命令；`fufucode-bridge.status` 缺失即审批未生效。

## 与计划的偏差（如实记录）

1. **A03"加载失败"的实测行为与文档描述不同**：`-e` 指定的扩展在加载期抛错时，Pi 0.87.1 直接退出进程（stderr 打印 `Failed to load extension "..."` 与 `Hint: Start without extensions using "pi -ne"`），**不是**把错误收入 `errors` 数组后继续运行。任务书要求"加载失败必须可诊断"，实现按此满足（驱动脚本检测到进程退出 → 报"审批不可用"），但这意味着该子路径下无法观察到"扩展缺失但 Pi 仍在运行"的中间态；真实桌面侧应据此做退出码诊断。
2. **A03"缺失"路径以 `-e` 不传扩展模拟**：Pi 在完全无扩展配置下正常启动（17 条内置命令），探针命令缺失 → 正确报告"审批不可用"。
3. **stub 模型替代真实模型**：本机无可用真实模型凭据；用本地 OpenAI 兼容 stub 驱动真实 agent 循环产生真实工具调用。审批链路（扩展 → RPC 往返 → 工具执行/阻止）全部为真实 Pi 行为，仅 LLM 输出是编排脚本。**真实模型凭据下的验收未完成**，保留未验证。

## 未验证项

- 真实云模型（非 stub）下的完整审批往返。
- TUI 交互模式下的对话框行为（本轨只覆盖 RPC 模式，这也是任务书指定范围）。
- 多扩展并发修改 `event.input` 的快照失效路径（单扩展场景已覆盖快照校验逻辑本身）。

---

## M5 演示扩展与交互面清单（追加，2026-09-28）

> 任务来源：`docs/archive/assignments/track-b-m5.md`。前置已执行：`git merge main` 同步至 `20e85e6`。分支 `track-b`。

### 产物

- `extensions/demo-extension/index.ts`、`package.json` —— `demo_write_note` 自定义工具（被 bridge 的 unknown→confirm 规则覆盖，跨扩展审批链路验证）+ `/demo.ui-tour` 命令（全 `ctx.ui` 面走一遍，结果持久化到 `ui-tour-result.json`）。
- `scripts/demo-verify/run.mjs` —— 7 个场景的验证驱动；复用 `bridge-verify/lib` 的 RPC 客户端与 stub 模型；结果写 `demo-verify-results.json`。
- `docs/decisions.md` —— 追加 `[轨道B] M5 交互面清单`（冻结契约）。

### 验证结果（真实 Pi 0.87.1，实跑）

命令：
```
cd C:\Users\21894\.codex\worktrees\track-b-bridge\Fufucode\scripts\demo-verify
C:\Projects\Fufucode\runtime\node\node.exe run.mjs all
```

7/7 通过（证据 = 临时目录内的文件系统状态 + 协议帧记录）：

| 场景 | 结果 | 关键断言 |
|---|---|---|
| tool-allow | PASS | 批准后 `demo_write_note` 执行，文件落盘；bridge confirm 往返 1 次 |
| tool-deny | PASS | 拒绝后文件不存在；bridge confirm 往返 1 次 |
| command-tour-answer | PASS | select/confirm/input/editor 四类对话框全往返；`ui-tour-result.json` 值正确；editor 请求无 timeout 字段 |
| command-tour-cancel | PASS | cancelled→confirm=false，select/input/editor=undefined（JSON 序列化省略） |
| command-tour-timeout | PASS | select/confirm/input 按 timeout 自决；editor 无超时，host 应答 cancelled 后完成 |
| fire-forget-arrive | PASS | notify/setStatus/setWidget/setTitle/set_editor_text 帧全部到达且字段正确 |
| dead-lock-free | PASS | editor 挂起期间 `get_commands` 正常应答；prompt 响应在 handler 完成后才返回（实测证实，驱动据此修正） |

### 实测新发现（已冻结入 decisions.md `[轨道B]`）

1. **editor 无超时**：请求记录不带 `timeout` 字段，Pi 不设自决——host 必须应答，否则 promise 永久挂起。
2. **`/命令` 的 prompt 响应时序**：扩展命令 handler 在 `prompt()` 内部 await，`preflightResult(true)` 在 handler **完成后**才发——host 不得先等 prompt 响应再答对话框。
3. **免答方法即答案**：notify/setStatus/setWidget/setTitle/set_editor_text 无 pending 记录，对它们发响应被静默忽略。
4. **widgetLines 仅字符串数组**：组件工厂与 `setWidget(undefined)` 不产生任何帧。
5. **`/命令` 执行入口**：RPC `prompt` 消息以 `/` 开头即执行（`_tryExecuteExtensionCommand`），steer/follow_up 会拒绝 `/` 命令。

### 未验证项

- TUI 交互模式下的 UI 表现（本任务范围只覆盖 RPC 模式）。
- `custom()`/组件工厂等 RPC 不支持的 API（源码已确认返回 undefined/无帧，未逐一驱动）。

---

## Pi 升级雷达（追加，2026-09-29）

> 任务来源：`docs/archive/assignments/track-b-upgradewatch.md`。前置已执行：`git merge main` 至 `24b5b7e`。

### 产物

- `scripts/upgrade-watch/compare.mjs` —— pinned/上游版本协议面对比脚本（Node + `tar.exe`，无新依赖）。
- `scripts/upgrade-watch/report-selfcheck.md` —— 自检报告（runtime 0.87.1 vs npm 0.87.1，**0 差异**，作为工具正确性基线）。
- `scripts/upgrade-watch/report-0.87.0-to-0.87.1.md` —— 真实版本对比报告。

### 用法

```
cd scripts/upgrade-watch
# 基线 = runtime/pi 安装树（当前 pinned），目标 = npm 最新版
node compare.mjs
# 显式版本对（任一参数可为目录路径、npm 版本号或 "runtime"）
node compare.mjs --base 0.87.0 --target 0.87.1 --out report-x.md
```

### 对比面与提取依据（从代码提取，非凭文档记忆）

| 对比面 | 提取来源 | 说明 |
|---|---|---|
| RPC 命令清单 | `dist/modes/rpc/rpc-mode.js` 的 `case "…"` | 全部命令 + `extension_ui_response` 路由 |
| RPC 命令字段 | `dist/modes/rpc/rpc-types.d.ts` 接口逐字段 | 删字段=破坏性，加字段=需评估 |
| 事件类型 | **两个集合分开比**：①发射点字面量（`agent-session.js`/`rpc-mode.js`/`json-event.js`/`json-mode.js` 的 `type:"…"`）②`docs/json.md` 清单 | 之前误报过：0.87.0 的 json.md 漏录 14 个事件但代码早就有 |
| extension_ui 子协议 | `rpc-mode.js` 的 `method:"…"` 集合 + 各方法 timeout 支持 | |
| session 版本 | `dist/core/session-manager.js` 的 `CURRENT_SESSION_VERSION` | 变化即破坏性 |
| 协议文档 | `docs/{rpc,rpc-commands,rpc-extension-ui,json,session-format}.md` 内容哈希 | 变化→需评估 |

### 报告分档

- **破坏性**：RPC 命令/字段删除、事件停止发射、UI 方法删除、session 版本变更 → 退出码 3。
- **需评估**：命令/字段/事件/UI 方法新增、timeout 支持变化、文档内容变化 → 人工确认。
- **无影响**：两版本在所扫面完全一致。

### 0.87.0 → 0.87.1 实测结论（报告：report-0.87.0-to-0.87.1.md）

- **破坏性：0**。RPC 命令、字段、UI 方法、发射事件集、session 版本（均为 3）全部一致。
- **需评估：17 条**——全部是文档面：json.md 补录了 14 个早已发射但未文档化的事件（`agent_settled`、`auto_retry_*`、`compaction_*`、`tool_execution_*`、`extension_error` 等），rpc.md/session-format.md 同步修订。升级评估结论：**运行时零协议变化，仅需确认新文档条款**。

### 未验证项

- 未对比非相邻版本（如 0.86→0.87），脚本支持但本次只跑了相邻版本与自检。
- GitHub 上游源码对比未实现（npm tarball 即权威发布物，满足任务要求）。

---

## P2 小批修复（追加，2026-09-29）

> 任务来源：`docs/archive/assignments/track-b-p2.md`（code-review-24b5b7e.md 的 P2-11、P2-16）。前置已执行：`git merge main` 至 `da9bd79`。提交 `cb6cd3a`。

### P2-11 路径推导

- 修改：`scripts/bridge-verify/run.mjs`、`scripts/demo-verify/run.mjs` 不再硬编码 `C:\Projects\Fufucode`；新增共享库 `scripts/bridge-verify/lib/repo-root.mjs`。
- 推导规则：仓库根 = `resolveRepoRoot(import.meta.url)`（`scripts/<suite>` 上溯两级），扩展目录一律取 `REPO_ROOT/extensions/...`；pinned 运行时 = `REPO_ROOT/runtime`。
- 逃生口：`FFF_PROJECT_ROOT`（绝对路径覆盖仓库根）、`FFF_PI_RUNTIME`（单独覆盖运行时目录）。worktree 无 `runtime/` 时 `assertRuntime` 直接报出设置指引（实测错误信息含 `FFF_PI_RUNTIME=...` 示例），不再静默混用共享工作区文件。
- 等价验证（任务书要求两个 worktree 各跑一遍）：
  - 主 worktree `C:\Users\21894\.codex\worktrees\track-b-bridge\Fufucode`：`FFF_PI_RUNTIME=C:\Projects\Fufucode\runtime` → bridge-verify 8/8 PASS、demo-verify 7/7 PASS。
  - 临时 detached worktree `%TEMP%\fff-p2-equiv-wt`（HEAD `cb6cd3a`，无 runtime）：同样命令 → bridge-verify 8/8、demo-verify 7/7 全 PASS，证明脚本定位的是自身所在 worktree 的扩展而非共享工作区。
  - 反证：不设 `FFF_PI_RUNTIME` 时脚本立即失败并提示设置，而非悄悄指向 `C:\Projects\Fufucode`。

### P2-16 策略回退诊断

- 修改：`policy.ts` 配置读取改为返回 `{config, diagnostics}`；文件缺失（env 显式指定时）、JSON 解析失败、非对象值各生成一条诊断；行为不变——任何失败仍回退默认 `confirm` 策略（方向安全）。
- `index.ts`：`reportFallback` 每会话最多一次，`session_start`、首个 `tool_call`、`fufucode-bridge.status` 命令三处触发，经 `ctx.ui.notify(..., "warning")` 发出"自定义策略解析失败（…），已回退默认 confirm 策略"。
- 新场景 `policy-fallback`（bridge-verify）：`FUFUCODE_BRIDGE_CONFIG` 指向坏 JSON → 收到 warning notify（含文件与解析错误位置）；拒绝对话后 `out.txt` 不存在（默认 confirm 仍生效）。PASS。

### 验证记录

| worktree | bridge-verify | demo-verify |
|---|---|---|
| track-b-bridge（主） | 8/8 PASS | 7/7 PASS |
| %TEMP%\fff-p2-equiv-wt（detached cb6cd3a，无 runtime） | 8/8 PASS | 7/7 PASS |

证据：两套 `*-results.json`；policy-fallback 场景的 notify 文本含 JSON 解析错误位置。

### 未验证项

- 无（P2-11/P2-16 已按任务书完成标准全部验证）。

---

## M5 管理页验收扩展集（追加，2026-09-29）

> 任务来源：`docs/archive/assignments/track-b-fixtures.md`（为轨道 D 的 M5 插件管理页提供验收弹药）。前置已执行：`git merge main` 至 `13132e3`。

### 产物

- `extensions/atria-widget-lab/`（`index.ts`/`package.json`/`README.md`）——功能丰富但无害的"正常扩展"fixture：
  - 只读工具 `atria_read_snippet`（读文件前 N 行，上限 50）
  - 命令 `/atria-lab.tour [ms]`（select/input/editor + notify/setStatus/setWidget/setTitle/set_editor_text 全面，结果落盘 `atria-tour-result.json`）
  - 命令 `/atria-lab.status`（get_commands 探针，即管理页"已加载"观测点）
  - `session_start` 发 `setStatus("atriaWidgetLab","ready")` 作加载信号
  - 故意不含 `confirm`、不写任意文件——与 bridge/demo 行为不重叠。
- `extensions/broken-fixture/`（`index.ts`/`package.json`/`README.md`）——factory 内故意抛错 `broken-fixture: intentional load failure for management-page acceptance`。
- `scripts/demo-verify/run.mjs` —— 追加 `fixture-atria`/`fixture-atria-gated`/`fixture-broken` 三个场景；`lib/pi-rpc.mjs` 新增 `stderrText` 累积。

### 管理页验收用法（给轨道 D）

- **正常多能力扩展**：`get_commands` 出现 `atria-lab.tour`/`atria-lab.status`；RPC 会话启动即收到 `setStatus`/`atriaWidgetLab` 帧（实测 `session_start` 在 RPC 模式启动即触发）。
- **启停**：去掉 `-e` 后两命令从 `get_commands` 消失、`setStatus` 帧不再出现。
- **审批联动**：与 `fufucode-bridge` 同载时 `atria_read_snippet` 走 unknown→confirm 门禁（fixture-atria-gated 实测 1 次 confirm 往返后执行）。
- **加载错误展示**：broken-fixture 使 Pi 启动即退出（exit 1），stderr 打 `Failed to load extension "...broken-fixture\index.ts": ... intentional load failure` + `Hint: pi -ne`。管理页应把这类扩展显示为"导致启动失败"而非"已加载但有警告"。

### 验证记录（demo-verify 全量复跑）

10/10 PASS（新增 3 场景 + 既有 7 场景无回归）：`fixture-atria`（listed+tourOk+toolOk+sessionStartSignal）、`fixture-atria-gated`（confirm=1 后执行成功）、`fixture-broken`（exitCode=1，stderr 含加载错误诊断）。证据：`scripts/demo-verify/demo-verify-results.json`。

### 未验证项

- 无（两扩展各自触发方式与管理页用法已实测并写入 README）。

---

## 常驻验证岗（追加，2026-09-29）

> 任务来源：`docs/archive/assignments/track-b-verify.md`。前置已执行：`git merge main` 至 `eaf5134`。角色变更：轨道 B 转为常驻验证执行员（记录事实，不修复）。

### 环境接入（按红线"库内既有方法"）

- worktree 的 `runtime/` 只含被跟踪的 `pi/package.json`+lockfile；以 junction 接入共享 pinned 运行时：`runtime/node` → `C:\Projects\Fufucode\runtime\node`、`runtime/pi/node_modules` → `C:\Projects\Fufucode\runtime\pi\node_modules`（gitignored）。接入后 `repo-root.mjs` 的默认推导与 `RealSessionBootstrap.FindRuntimeRoot` 均生效，**无需任何 env 覆盖**。

### 首轮固定验证组（main@eaf5134，全绿）

| 命令 | 结果 |
|---|---|
| `dotnet build Fufucode.sln` | 0 错误 0 警告 |
| `--selftest`（mock） | 0 失败 |
| `--selftest --realpi`（真实 Pi + Atria 模型段） | 0 失败（审批链路 A01 在真实 Pi 上通过） |
| `tests/Fufucode.Core.Tests` | 150 PASS / 0 FAIL |
| `scripts/packaging/verify-clean-start.ps1` | PASS |
| `scripts/bridge-verify` / `scripts/demo-verify` | 8/8、10/10 |

执行日志：`docs/records/verify-log.md`（本轨独占）。异常摘要：无。

### 未验证项

- 无（首轮固定验证组全部执行通过）。

### 第二轮（派发：D Wave1+A/F 交付合并后）—— main@56e46f8，main 工作区执行，全绿

- 执行前 `git merge main` 于 track-b（fast-forward 至 `56e46f8`）；main 工作区运行前 `git status` 干净。
- 结果：build 0 错误 0 警告；selftest 0 失败；selftest --realpi 0 失败（含 Atria 真实模型段与 P2-17）；Core.Tests 156 PASS/0 FAIL；packaging verify-clean-start PASS；bridge-verify 8/8；demo-verify 10/10。
- 异常摘要：无。详见 `docs/records/verify-log.md` 第二轮记录。

### 第三轮（派发：main@d8a6017，含 3aae6c9 后合并）—— 固定验证组 + 安装版 CLI 冒烟，全绿

- 执行环境：main 工作区 `C:\Users\21894\.codex\worktrees\track-a-core\Fufucode`（HEAD=`d8a6017`，运行前干净）；安装版 `%LocalAppData%\Programs\Fufucode`。
- 约束遵守：本轮未 commit/merge/push；改动（`scripts/bridge-verify/lib/stub-server.mjs` 新增、两份 results.json 复写、本段记录）留在 worktree。
- 固定验证组：bridge-verify 8/8 PASS（a01 允许/拒绝/超时/bash 拒绝落盘校验、policy-fallback、a02 非阻塞、a03 缺失/损坏）；demo-verify 10/10 PASS（含 fixture-atria 正常/被门禁、fixture-broken 启动失败复现）。结果文件：`scripts/bridge-verify/bridge-verify-results.json`、`scripts/demo-verify/demo-verify-results.json`（main 工作区实跑，已同步回本 worktree）。

### 安装版 CLI 冒烟（新增）

| 检查 | 结果 | 证据 |
|---|---|---|
| `fufucode sessions list --json`（新终端，`cli/Fufucode.Cli.exe`） | PASS — exit 0，合法 JSON | 首轮 `[]`；chat 两趟后返回 1 条会话条目 |
| `fufucode chat "ping"` 对本地合成端点 | PASS — exit 0，输出 `stub round complete: fufucode-chat-ok` | 临时把 `%LocalAppData%\Fufucode\pi-agent\models.json` 的 atria baseUrl 指向 `http://127.0.0.1:18911/v1`（stub-server.mjs，openai-completions 兼容），测后已恢复原件（备份校验通过）。真实模型凭据段**本轮未验**（模型真实可用性非本任务范围；session jsonl 证实消息走通合成端点） |
| `fufucode chat "write the file"`（stub 发 write tool_call） | PASS — 审批拦截成立 | stderr 收到 `extension_ui_request`（method=confirm，title 含 `allow tool ''write''?`，message 含参数快照，timeout=120000）；EOF 取消后 toolResult=`fufucode-bridge: approval denied for ''write''`（isError=true），`bridge-check.txt` 未落盘；CLI exit=3 |
| 审批桥在安装包内加载 | PASS — `%LocalAppData%\Programs\Fufucode\extensions\fufucode-bridge` 已生效 | 上述拦截链即证据；全程无 `extension_error` 帧、无加载错误 stderr |

### 发现（按严重度分档，不修代码）

- **中**：`fufucode chat` 在审批被 EOF 取消后以 **exit 3** 退出。方向正确（拒绝≠成功），但脚本调用方若把非零视为"程序故障"会误判；建议文档注明 3 = 用户/管道取消批准路径。
- **低**：`sessions list --json` 条目的 `project` 字段记录为 `C:\Projects\Fufucode`，而该会话 jsonl 的 `cwd` 是 `--project` 指定的 `%TEMP%\fff-cli-smoke`——索引字段与实际会话 cwd 不一致（desktop.db 索引 `1969d272` vs Pi 会话目录 `66104b13`/`84f3c4c4` 且 id 体系也不同，属同一条目两种 id，需 A/C 确认是否预期）。
- **低**：`fufucode extensions list` 返回"No configured or discovered extensions"，内置审批桥不在列表中（合理但管理页需知：桥属内置链路非用户扩展）。
- **信息**：直接运行 `cli/Fufucode.Cli.exe`（不经 `Fufucode.cmd` 包装）也能正确定位数据目录与运行时；`extensions/` 目录随包分发可用。

### 未验证项

- 真实模型（atria apiKey 已设但本轮按派单用合成端点；真实模型验收已由 `--selftest --realpi` 覆盖）。
- TUI/桌面 UI 内审批交互（本轮为 CLI 冒烟）。
- 完整桌面版 build/selftest/packaging 组：本轮按派单范围聚焦 CLI 与扩展套件，未重跑 dotnet 组（上一轮 main@56e46f8 已全绿）。
