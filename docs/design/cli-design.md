# Fufucode CLI 命令面设计

状态：A7 GUI 控制面设计与 Headless preview 最小切片已实现并实测，11 项定向 CliTests 通过；控件操作真实，业务数据为 mock。A6 的真实 Pi + 本地脚本模型证据仍保留在 `docs/progress.md`。远端真实模型、真实 GUI attach、事件订阅及完整发布/安装仍未验。

## 设计边界

- CLI 消费 `Fufucode.Core` / `Fufucode.Pi` 公共用例，通过 `PersistentSessionManager` 打开 Pi RPC 会话，通过 `IChatSession` 收发，通过 `IExtensionUiSession` 审批。
- `fufucode <command>` 单次执行退出；裸命令、`chat --continue` 和 `chat --resume <id>` 进入逐行 REPL。无全屏重绘。
- 默认 project 为当前目录；`--project` 指定项目。指定 session 必须属于该项目；省略时恢复该项目最近会话，没有会话则新建。
- 项目信任沿用 Core 已保存决定，不自动替用户确认。未决定的受保护项目资源、明确拒绝信任的项目内扩展或不可用的审批桥均报错。
- `--json` 的数据只进 stdout；提示、诊断、错误只进 stderr。聊天每行均是完整 JSON：审批请求、审批命令结果及最终结果；不输出提示符。

## 全局参数

| 参数 | 含义 |
|---|---|
| `--project <path>` | 项目工作目录，必须存在。 |
| `--session <id>` | 复用该项目指定会话。 |
| `--json` | JSON 数据输出。 |
| `--pi-node <path>` / `--pi-cli <path>` | 显式指定 Node 和 Pi CLI；省略时从程序目录向上寻找 `runtime/`。 |
| `--app-data <path>` | 覆盖 Fufucode 本地数据根目录，供隔离和自动化运行。 |
| `--help` | 显示帮助，不启动 Pi。 |
| `--` | 结束全局参数解析，保留其后参数原文。 |

## 命令

退出码：`0` 成功；`2` 参数或子命令无效；`3` Core、Pi、模型或审批失败；`130` 用户取消/中断。错误文字写 stderr。未知或未实现的命令返回 `2`。

| 命令 | 参数与作用 | Core / RPC 映射 | 默认 stdout | `--json` stdout | 状态 |
|---|---|---|---|---|---|
| `chat <prompt>` | 发送一次并等待本轮结束。 | `PersistentSessionManager` → `IChatSession.SendAsync`，等待 `TurnSettled`；扩展处理命令可由 `IPromptHandledSession` 确认。 | 助手文本逐块输出，最终消息修正缺失/变更的文本；审批提示走 stderr。 | 最终 `{outcome,text,error}`；审批请求及命令结果在前。 | 已实现，待外部验证 |
| `chat --continue` | 最近会话的逐行对话。 | 打开最近会话，复用 chat runner。 | 每轮助手文本。 | 每轮一个最终结果对象及可选审批记录。 | 已实现，待外部验证 |
| `chat --resume <id>` | 指定会话逐行对话。 | `PersistentSessionManager.ResumeAsync`，先校验项目归属。 | 同上。 | 同上。 | 已实现，待外部验证 |
| `sessions list` | 当前项目会话，最近打开时间倒序。 | `DesktopMetadataStore.ListSessions`，不启动 Pi。 | ID、名称、时间、中断标记。 | 数组：`id/name/projectId/project/lastOpenedUtc/active`。不启动 Pi，**也不会把项目写入元数据**：未登记的项目返回空数组而不是新建项目行。 | 已实现，FakePi 已验 |
| `sessions show <id>` | 元数据及有界转录摘要；会启动 Pi 恢复历史。 | `ResumeAsync` → `SessionHistory`；REPL 当前会话刷新历史后读取。 | 会话字段及摘要。 | `id/name/projectId/project/lastOpenedUtc/active/leafId/totalEntries/summary`；`project` 为会话自身登记项目的规范路径（不是调用方传入的 `--project`），`projectId` 供 UI 直接映射项目记录；最多最近 100 条，每条文本最多 2000 字符。 | 已实现，FakePi 已验 |
| `sessions delete <id>` | 删除该项目已关闭会话。 | `SessionDeletion.Delete` 校验归属、路径与会话锁，移走已登记 JSONL 后删除元数据/草稿，元数据失败恢复文件。 | 删除结果。 | `{deleted:true,id}`。 | 已实现，待外部验证 |
| `models list` | 当前可用模型。 | `QueryAvailableModelsAsync` / `get_available_models`。 | 每行 `provider/modelId`。 | Pi 返回的数据对象。 | 已实现，待外部验证 |
| `models set <provider/modelId>` | 切换当前会话模型并保存应用默认值。 | `SessionModelPreference.ApplyAsync`：`set_model` → `get_state` 精确确认 → 刷新 adapter 模型 → `AppSettingsStore.Update`。 | 确认模型和保存结果。 | `{model:"provider/modelId",saved:true}`。 | 已实现，待外部验证 |
| `models thinking` | 当前模型实际支持的思考等级。**`set_thinking_level` 返回成功不等于生效**：不支持推理的模型会被静默钳制（D-8）。 | `ChatSessionCoordinator.QueryThinkingLevelsAsync` → `get_available_thinking_levels`（返回 `{levels:[...]}`）+ `get_state` 的 `thinkingLevel`。 | 可用等级列表与当前等级。 | `{levels,current,model}`。 | 已实现，FakePi 已验，真实模型待验 |
| `settings get [key]` | 应用设置及凭据配置状态，不启动 Pi。 | `AppSettingsStore.Current`、`ICredentialResolver`。 | 键值列表，凭据仅 true/false。 | 设置对象或单键对象，永不包含凭据值。 | 已实现，待外部验证 |
| `settings set <key> <value>` | 修改受支持键，原子保存并使用 Core 校验。 | `AppSettingsStore.Update`；凭据单独走 `ICredentialStore`。 | 更新键值；凭据只显示配置状态。 | `{key,updated:true}`；凭据另有 `configured` 布尔值。 | 已实现，待外部验证 |
| `extensions list` | 已配置、发现、缺失及有历史错误的扩展，不启动 Pi。 | `ProjectExtensionService.List`，共享 App 偏好键和发现规则。 | 路径、启用、存在、历史错误标记。 | 数组：`path/enabled/exists/hasRecordedError/loaded:null`。 | 已实现，待外部验证 |
| `extensions enable <path>` | 显式启用，验证新 Pi 进程后保存项目偏好。 | `ProjectExtensionService.ChangeAsync` → Core 信任/路径/会话锁检查 → 新连接验证 → 保存。 | 路径和验证会话。 | `{path,enabled:true,sessionId}`。 | 已实现，待外部验证 |
| `extensions disable <path>` | 移除显式启用，并验证不带该扩展的新进程。 | 同上；失败保留旧启用偏好。 | 路径和验证会话。 | `{path,enabled:false,sessionId}`。 | 已实现，待外部验证 |
| `diag export [<path>]` | 导出筛选后的诊断 JSONL，不启动 Pi。 | `DiagnosticExport.WriteJsonLinesAsync`。 | 路径及记录数。 | `{path,records}`。 | 已实现，待外部验证 |
| `changes diff <path> [--context N]` | 单文件**只读** unified diff：hunk 解析、上下文可调、行数有界。**绝不 stage / 还原 / revert。** | `DiffQuery.FileAsync` → `git diff --no-ext-diff --no-textconv --no-renames -U<N>`；拒绝越出项目的路径与重解析点，无 HEAD 时退回 `--cached`。 | `---/+++` 与 hunk 文本；二进制或不可用写明原因。 | `{path,oldPath,isBinary,truncated,context,error,hunks:[{header,oldStart,oldLines,newStart,newLines,lines:[{kind,text}]}]}`；`kind` ∈ `+`/`-`/空格/`\`。 | 已实现，已验 |
| `approve list` | 当前聊天/REPL 进程的挂起请求。 | `IExtensionUiSession.PendingExtensionUiRequests`。 | ID、方法、请求内容。 | 请求数组：`id/method/payload/receivedAt/timeoutMs`。 | 已实现，限同进程 |
| `approve accept <id> [value]` | 批准 confirm；select/input/editor 提交值。 | 找到并保留原 Core 请求对象 → `RespondToExtensionUiAsync`。 | 已响应结果。 | `{id,accepted:true}`。 | 已实现，限同进程 |
| `approve reject <id>` | confirm=false，其他对话 cancel。 | 同上，`Confirm(false)` / `Cancel()`。 | 拒绝结果；本轮审批失败。 | `{id,accepted:false}`；最终失败对象。 | 已实现，限同进程 |

## REPL 和无人值守审批

管理命令可逐行输入 `sessions ...`、`models ...`、`settings ...`、`extensions ...`、`diag ...`、`approve ...`，也可用对应 `/` 前缀。`/help` 显示帮助，`/exit`、`/quit` 退出。以这些管理词开头的聊天内容可用 `/chat <prompt>` 显式提交。引用用单引号/双引号包住值，反斜杠保持字面值；`""` 表示空字符串。

审批队列属于仍存活的 Pi RPC 进程。独立的 `fufucode approve ...` 返回 `2`，不能给另一个进程或已退出进程的旧请求授权。

聊天等待审批时，stdin 可输入 `approve list`、`approve accept <id> [value]`、`approve reject <id>`；可以回复本进程任意仍挂起的 ID。confirm 接受不带 value；select 必须使用 Pi 原样给出的选项字符串；input/editor 必须带 value，可用 `""` 提交空字符串。Core 校验原请求对象、代次、方法和选项，迟到 ID 返回 `3`，非法参数返回 `2`。有效拒绝/取消使本轮返回 `3`。

保留直接终端回复：confirm 只有 `y` 放行，其他输入拒绝；select 可给有效序号/选项原文，空行取消；input/editor 的空行提交空字符串。JSON 模式还接受旧格式 `{"id":"...","confirmed":true}` / `{"id":"...","value":"..."}` / `{"id":"...","cancel":true}`。

真实终端和管道由一个后台 stdin 线程读取，逐行放入有界队列。REPL 与审批复用同一队列；Core 回调只排队请求，不各自读取 stdin。等待队列可取消，底层同步终端读取不会阻挡进程退出。EOF 取消挂起请求并返回 `3`；请求过期、请求提前关闭、响应失败也返回 `3`，不会无限等待。Ctrl+C 清队列/中止并返回 `130`。

REPL 内 `sessions fork <entryId>`：对当前会话在指定 entry 处分叉。Pi 会把**同一个进程**重新绑定到新的 sessionId/sessionFile；Core 先取子会话锁再释放父会话锁（锁交接），保留父会话行并写入 `parent_session_id`/`fork_entry_id`/`forked_at_utc`（元数据 schema 3），父会话文件是否字节不变作为证据返回，不作保证。父会话对象交出 Pi 所有权后 dispose 不会杀进程。分叉要求当前会话空闲；Pi 拒绝未知 entry 时报 `3`。

正常模型轮次仍等待 `agent_settled`。对已被扩展处理而不启动模型的命令，Core 只有在 prompt 已成功确认、没有该轮运行事件、没有挂起 UI、`get_state` 证实空闲且队列为空时发 `PromptHandled`。最终 JSON 的 `outcome:"handled"` 区分这种命令成功，不声称生成了模型文本。

## 设置键

| 键 | 值 / 行为 |
|---|---|
| `selectedModel` | `provider/modelId`，`null` 清除新会话默认值；立即切换用 `models set`。 |
| `thinkingLevel` | Core 允许的等级或 `null`。 |
| `theme` | `system/light/dark`。 |
| `proxyUrl` | 不含凭据/查询串的 HTTP(S) URL 或 `null`。 |
| `endpoint` | 完整 JSON：`providerName/baseUrl/modelId/apiKeyEnvironmentVariable`，或 `null`；不接受密钥字段。 |
| `endpointCredential` | get 只返回是否已配置；set 使用独立凭据存储，`null` 删除。需先配置 endpoint。Windows 复用现有 Credential Manager；无可写存储的平台返回 `3`，可由外部设置环境变量。 |
| `endpointCredentialConfigured` | 只读 bool，含现有环境变量回退状态。 |

除 `models set` 外，设置修改在后面的新 Pi 进程/新会话创建时使用，不承诺变更其他已运行 GUI/CLI 进程。

## 扩展 UI 应答留痕（A13，D-6 建议）

`PiDiagnosticEvent` 增加可选 `Outcome` 字段（白名单：`value`/`confirmed`/`declined`/`cancelled`/`expired`/`closed`/`notified`/`ignored`，其他落 `other`）。`ChatSessionCoordinator` 在事件泵里为**每个**扩展 UI 事件记一条 `extension_ui_request`（`command` 为方法名，`requestId` 为 Pi/扩展给的对话 id，经长度与字符集过滤；通知类同时带 `outcome:"notified"`，未知方法带 `outcome:"ignored"`）；答复发出后记 `extension_ui_response`，`outcome` 区分用户确认/拒绝/取值、超时 `expired`、Stop/Close 放弃 `closed`，并带从请求到应答的耗时。对话正文、标题、选项文本一律不进日志。`DiagnosticExport` 复用同一白名单，导出仍不含 payload。这样"某次 write 的审批到底是谁给的"可在事后审计，而不必依赖共享桌面的人工回忆。

## 扩展、删除与诊断细节

- 项目扩展偏好复用 App 的 `app.enabled-extensions/app.added-extensions/app.extension-errors:<projectId>`。发现目录为 `<project>/.pi/extensions` 和私有 `<app-data>/pi-agent/extensions`；发现项默认禁用。每个 CLI 开会话入口读取显式启用偏好，不自动加载发现项。缺失路径保留在偏好及列表中，启动时跳过。
- 扩展启停使用指定/最近会话验证；没有会话则新建。REPL 释放当前锁后验证并重新打开。独立命令不会重启另一个 GUI/CLI 进程，已被其他进程持有的会话会报错。成功清除本批实际存在扩展的旧错误；加载失败仅记录安全类别并保留原启用偏好。列表的 `loaded:null` 表示没有查询另一进程的实际加载状态。
- 删除仅触及该项目已登记、位于应用会话目录内的 `.jsonl` 和相应元数据/草稿。活跃会话锁、越界路径、链接/目录联接均拒绝。已缺失文件允许删除其元数据；未登记孤立文件不推测归属。文件移走后的异常会报错，恢复/遗留隔离文件情况应由外部验证检查。
- 诊断仅通过现有脱敏 schema 导出 `app-*.jsonl` 元数据，不导出提示、回答、密钥、扩展错误原文或原始日志。默认写入 exports 下新文件；显式目标父目录须存在，拒绝覆盖现有目标及写入源 logs 目录。

## 示例

```text
fufucode sessions show <id> --project C:\work\repo --json
fufucode models set provider/model --project C:\work\repo --json
fufucode settings get endpointCredentialConfigured --json
fufucode settings set theme dark
fufucode extensions enable C:\extensions\example.ts --project C:\work\repo
fufucode diag export --json
fufucode chat "检查项目" --project C:\work\repo --json
# 在最后一个聊天进程的 stdin 内按实际请求 ID 输入：
approve list
approve accept <id>
```

## 验证交接

`CliTests.RunAsync` 接入审批/会话用例及 `CliSettingsTests`、`CliMaintenanceTests`。覆盖 FakePi 实际管道、审批副作用、并发对话请求、EOF、同步阻塞 stdin 的超时收敛、过期回复、handled 命令、会话锁/归属/越界删除、设置/凭据状态、实际模型确认、扩展加载参数/信任/回滚/发现、诊断脱敏及错误退出码。测试源码已添加，尚未在本派发执行；真实 Pi、真实 Windows stdin/Ctrl+C、凭据存储和打包后的 CLI 入口同样未验收。

### A6 验收更新（2026-09-30）

上段保留第二切片的历史交接状态。A6 已消费现有 CLI/App 产物完成真实 Pi + 本地脚本模型聊天、真实桥的 approve list/accept/reject（实际写入/拒绝副作用）、sessions/model/extensions/diag、缺凭据/缺模型、Windows CTRL_C_EVENT 退出 130，以及真实 GUI 持有同会话时 CLI 退出 3。命令、原始输出、报告路径详见 `docs/progress.md` 的 A6 段；本地脚本模型不是远端模型验收。

打包将 App 与 CLI 分目录发布，根 `Fufucode.cmd` / `.ps1` 无参数打开 GUI，其他命令进入 CLI（首参数 `--selftest` 保持 GUI 自测）。安装器向用户 PATH 添加 `cli/`，该目录提供原生 `fufucode.exe` 与 cmd 备用入口；裸 `fufucode` 为逐行 REPL。新终端中 `fufucode <command>` 无需源码或 `dotnet run`。已用现有 Debug CLI 跑过 17 项启动器验证；完整 self-contained 发布、安装后 PATH 与卸载仍未执行，尚不能宣称安装体验验收完成。
## GUI 组件控制面（A7，2026-10-01）

### 路线与首片边界

采用 **CLI → 独立 App 无头入口 → Avalonia Headless 实际窗口/控件 + AutomationPeer**。CLI 不引用 App 工程，Core 不引用 ViewModel 或 Avalonia。复用 selftest 已有 Headless/Skia 包，不引入新 NuGet。读取当前已实例化的 visual control tree，附带每个控件的 automation role/id/name；后续优先使用 Invoke/Value/Selection/ExpandCollapse/Scroll 等 provider，必要时才用与人手相同的路由输入。仅靠 Core 暴露 VM 操作无法表达焦点、弹窗、Flyout、模板、滚动和截图，也无法证明按钮绑定正确，因此不作为 GUI 控制主路线；业务状态、会话锁、审批仍由既有 Core 公共用例负责。

**最小切片为隔离 mock preview**：`ui snapshot`、`ui screenshot <新PNG路径>`、`ui invoke nav.chat|nav.extensions|nav.settings`。每个 CLI 命令创建独立、临时数据根和新的无头窗口，默认 Chat；`--page chat|extensions|settings` 可先经导航按钮切到指定页面。invoke 返回切换后的完整 snapshot，因此可观察效果；没有跨命令隐式持久化，不附着已打开 GUI、不连接 Pi、不读取用户 settings/会话。输出明确 `mode:"headless-preview",sessionSource:"mock"`。控件树包含实际已实例化模板，未打开 flyout/dialog 和虚拟化列表的未实现项不伪造为控件。三条 nav ID 稳定；其他 ID 为本次 snapshot 内结构地址，尚不可 invoke。

CLI 自动定位相邻发布目录 `app/Fufucode.App.exe`（开发时寻找对应 App bin），也接受 `--ui-app <App可执行文件>`。缺 App 返回 3；不通过源码 `dotnet run` 启动。`--session` 在 preview 明确拒绝；project/app-data/Pi 参数不建立真实业务上下文。App 新增 `--headless-ui` 入口，仅修改无头入口与 SelfTest 路径，不修改 D 的 Views/ViewModels。完整发布纳入新入口的验证由后续发布工程执行。

### 全部交互区域与命令草案

下表除三个 nav invoke 外均为设计，尚未实现。`ui set <id> <prop> <JSON值>` 只允许显式支持的 Value/Selection 属性，禁止任意 CLR 属性反射。集合 ID 使用业务键而非第几个；动作名也进入 allowlist。

| 实际区域 / 控件 | 稳定标识与命令草案 / 事件 |
|---|---|
| MainWindow：对话/扩展/设置导航 | `ui invoke nav.chat|nav.extensions|nav.settings`；`ui.page.changed`。 |
| 项目按钮 flyout、最近项目项、打开其他文件夹 | `ui invoke project.menu`、`ui invoke project.recent:<projectId>`、`ui invoke project.open --path <path>`；`project.changed`；原生目录选择器需参数化适配，不冒充 Headless 支持原生 dialog。 |
| 新建会话、会话 ListBox 选择 | `ui invoke sessions.new`、`ui set sessions.list selection <sessionId>`；`session.changed`，仍检查真实会话 lease。 |
| mock 生成 500 条演示 | `ui invoke demo.generate`；仅 mock，不能标注真实 Pi。 |
| 启动层打开项目、最近项目、重选/重试、停用失败扩展并重试、打开其他项目 | `ui invoke startup.open|recent:<id>|retry|disable-extension|other-project`；`ui.startup.changed`，停用需独立确认。 |
| Chat：会话标题重命名、Enter 保存/Esc 取消 | `ui invoke session.rename.begin`、`ui set session.rename text <name>`、`ui invoke session.rename.save|cancel`；`session.renamed`。 |
| 消息区域：代码复制/展开、thinking 展开、工具卡片展开、时间线滚动、回到最新 | `ui invoke timeline:<messageId>:<blockId>.copy|expand|collapse`、`ui set timeline scroll <position>`、`ui invoke timeline.latest`；`ui.clipboard.changed`、`ui.expansion.changed`、`ui.scroll.changed`。 |
| 草稿恢复卡片 | `ui invoke input.restore-draft`；`ui.value.changed`。 |
| 输入框、发送/停止、steer、Enter/Ctrl+Enter/IME | `ui set input.prompt text <text>`、`ui invoke input.send|stop|steer`；后续 `ui key` 只作用于指定 focus，IME 不用合成 Enter 代替；`chat.*` / `ui.value.changed`。 |
| 输入命令菜单 flyout、筛选、命令项填入 | `ui invoke input.commands`、`ui set input.command-filter text <text>`、`ui invoke input.command:<name>.fill`；填入不等于发送。 |
| Settings：主题 ComboBox、模型刷新/选择、思考等级 | `ui set settings.theme selection <system/light/dark>`、`ui invoke settings.models.refresh`、`ui set settings.model selection <provider/model>`、`ui set settings.thinking selection <level>`；`settings.changed` / `models.changed`。 |
| 代理输入、保存/清除、立即重启 | `ui set settings.proxy text <url>`、`ui invoke settings.proxy.save|clear`、`ui invoke settings.restart`；既有校验与空闲重启守卫。 |
| 端点表单展开、provider/baseURL/model、凭据模式/key/env、保存/移除 | `ui invoke settings.endpoint.form`、`ui set settings.endpoint.<field> value <value>`、`ui invoke settings.endpoint.save|remove`；key 为 write-only，不在 snapshot/event/日志回显，移除需确认。 |
| 设置页打开 models.json 所在目录/数据根/Pi 配置/日志目录、复制诊断、禁用的完整诊断导出按钮 | `ui invoke settings.folder:<kind>.open|settings.diagnostics.copy`；Shell/clipboard 在无头模式报告 supported/unsupported，不能静默成功；`settings.diagnostics.export` 当前 disabled、无 Command，必须拒绝 invoke。 |
| Extensions：添加、刷新、安全重启、撤销待应用改动 | `ui invoke extensions.add --path <path>|refresh|restart|undo`；沿用信任与安全重启守卫；`extensions.changed`。 |
| 扩展列表每项 Toggle、移除 flyout、确认移除/取消 | `ui set extension:<pathKey> enabled <bool>`、`ui invoke extension:<pathKey>.remove` → `ui invoke confirmation:<token>.accept|cancel`；不得将两步合并为默认确认。 |
| 扩展命令过滤/填入、RPC 说明展开 | `ui set extensions.filter text <text>`、`ui invoke extension-command:<name>.fill|extensions.rpc-info.expand`；`ui.value.changed`。 |
| 项目信任更改、立即安全重启 | `ui invoke extensions.trust.change|extensions.restart`；打开实际信任 dialog，不预填 Trust。 |
| ProjectTrustDialog：信任/不信任/窗口关闭 | `ui invoke trust:<generation>.trust|deny|cancel`；当前请求 generation + 项目路径匹配；`trust.requested/resolved`。 |
| ExtensionUiDialog：confirm 允许/拒绝，select 选择，input/editor 输入，提交/取消/关闭 | `ui set approval:<id> value <value>`、`ui invoke approval:<id>.accept|reject|cancel`；复用原 `IExtensionUiSession` token、选项、代次与超时；`approval.requested/closed`。 |
| 系统窗口关闭、正在运行时退出确认、文件/目录选择及动态确认弹窗 | `ui invoke window.close` → `confirmation:<token>`；原生窗口 chrome 与文件选择明确 capability，不与控件树混同。 |
| 通用交互：页面滚动、列表/下拉选择、焦点、文本选择/光标、键盘导航、Flyout关闭、窗口大小 | 后续 allowlist `ui scroll/select/focus/key/dismiss/window`，不通过任意属性反射实现；保留实际路由输入、可见/禁用与 IME 守卫。 |

### snapshot、invoke 与 screenshot 契约

stdout 始终为 JSON（ui 命令不要求 `--json`）；stderr 只写错误，沿用退出码 0/2/3/130。snapshot schemaVersion 1：`mode/sessionSource/page/tree`，每节点 `id/type/role/automationId/name/enabled/visible/focused/bounds/actions/children`；`actions` 为本控制层实际允许动作，不等同于 peer 的全部潜在能力。password 控件省略 name/value；不枚举 DataContext，不序列化任意 VM。超过节点/深度上限明确 `truncated:true`，不得悄悄假称完整树。bounds 为父节点坐标内 DIP，截图为当前窗口渲染像素；不对接 OS UIA 服务，因此跨平台无需桌面会话。后续持久宿主扩展为 roots（主窗口、独立审批/信任 dialog、popup root），标明集合项是否 materialized；首片仅一个实际主窗口，不声称未打开弹窗已枚举。

`ui invoke nav.settings` 通过当前实际导航 Button 的 `IInvokeProvider` 执行，检查 enabled/visible 和 provider，再等待 UI 布局后返回新页 snapshot，禁止直接写 `CurrentPage` 冒充点击。截图捕获 Skia 渲染帧，用 CreateNew 独占写入用户明确指定的新 PNG，不覆盖已有文件；输出 `{path,width,height,page,mode,sessionSource}`。未支持元素和 set/events 命令返回 2，不执行潜在业务动作。

### 事件流（设计；A7 首片不实现）

后续 `ui serve` 持有一个窗口、独立进程及状态；stdin 接收 `{id,method,params}` JSONL，stdout 单写入者推送响应与事件。`events stream --ui <instanceId>` 订阅该实例的认证本地通道，独立 CLI 不推测另一进程状态。响应 `{type:"response",id,result|error}`；事件 `{type:"event",schemaVersion:1,instanceId,seq,timestamp,kind,elementId,payload}`。按 UI Dispatcher 顺序递增 seq，nav invoke 完成前先发 `ui.page.changed` 再发 response；property/selection/焦点/弹窗/关闭订阅真实控件与 VM 通知，业务事件来自 Core。初始快照 + revision，后续变化携带 revision；动态弹窗包括 request ID 和 generation。

事件队列有界：文本/布局变化按 element 合并，审批/关闭/错误不得静默丢弃；背压时停止接收新动作，发 `events.gap` 并要求重新 snapshot，慢消费者最终断开。取消/EOF 关闭无头宿主并释放会话；重连重新获取实例和审批 token，绝不重放旧授权。key/value 敏感字段只发 changed/configured 布尔值。真实业务副作用事件包含动作来源与关联 request ID，避免将“已收到请求”当成“已完成”。

### 安全边界与后续验收

- 首片 allowlist 只有导航；无法发送提示、执行工具、保存设置/凭据、删除会话、切换信任或重启真实 Pi；未知元素失败，不降级到任意 ICommand / CLR 反射。
- 未来 real 模式仍经 Core + required bridge，approve 必须精确匹配本进程现存请求；UI invoke 不生成肯定回答，不缩短审批、不自动填 Trust、不伪造 user decision。
- 破坏性 GUI 动作保留实际确认步骤及本轮 token；agent 提交目标/意图仅打开确认，确认需明确第二动作。快照只报告当前实际已打开的弹窗，关闭行为沿用取消语义。
- 启动真实会话前沿用项目归属/信任/lease；无头窗口不能绕开另一个 GUI 的锁。已有 GUI attach 是后续独立本地 IPC 能力，A7 无此声明。
- 验收必须看实际控件树、导航前后差异、PNG 文件内容与退出码；CliTests 覆盖真实 App worker，而非手写树。UI 操作真实、mock 业务合成，两种证据分别记录。后续真实 Pi/审批/密钥遮蔽/破坏性确认/原生 dialog 与事件背压仍需独立验收。
## Wave5 契约支撑（A8，2026-10-01）

本轮提供 P1-3 / P0-3 最小只读契约；P1-2 / P2-1 仅设计与核实。开始和收尾检查时，main 与 D worktree 的 progress 尚无 Wave5「交 A 清单」；以下按本轮用户明确四项需求交付，D 后续清单若有新增字段应单独对齐，不声称已验收 Wave5 页面。

### P1-3 变更范围：两个独立口径

| Core 只读入口 | 口径与边界 |
|---|---|
| `ChangeRangeQuery.WorkspaceAsync(projectPath, ct)` | Git 整个 worktree 的当前 index + worktree + untracked 状态，Root 为 Git top-level（项目是子目录时仍为全 worktree）；不等于本 agent 的修改，不包含忽略文件。 |
| `ChangeRangeQuery.SessionWritesAsync(projectPath, messages, liveTools?, ct)` | 指定会话当前分支已确认成功的内置 `write/edit` 路径；历史 assistant toolCall 与紧随其后的 toolResult 成功证据配对，live card 必须 `IsFinal=true,IsError=false`。拒绝、待执行、结果缺失、失败不会宣称写入成功；复用 call ID 不挪用旧成功证据。 |

DTO `ChangeRangeReport(scope,root,available,files,error,excludedToolPaths)`；`ChangedFile(path,oldPath,kind,indexStatus,worktreeStatus,exists,isBinary,toolCallIds,isNew,isDeleted,addedLines,removedLines)`。`addedLines/removedLines` 直接取 Git numstat 两列，二进制（`--` 列）为 null——**不得把 null 当 0**。路径相对该 report.root；范围查询不返回文件内容或 tool args。kind 包括 modified/added/untracked/deleted/renamed/copied/type-changed/unmerged；工具范围在没有 Git 分类时为 written/missing。`isNew/isDeleted/isBinary` 可为 null，表示证据不足；不可把 null 当 false。binary 优先 Git numstat 的 `-`，否则采样当前文件前 8000 bytes 的 NUL（Git 风格启发式），不存在/链接/无读权限则 unknown。删除文件从 Git diff 获取 binary；两列状态保留 staged/unstaged 差异。

Git 使用 porcelain v1 `-z` 与 numstat `-z --no-renames`，保留空格/Unicode 文件名、rename 的 oldPath；禁止可选 index 更新、fsmonitor、external diff/textconv。未初始化 HEAD 可查询 staged 新文件。非 Git、Git 不可用/失败与真正空变更明确区分，`available:false,error:<category>`；不伪装为空成功。多条 Git 查询是点时快照，不保证外部同时写入时原子一致。

工具范围严格限于项目内路径，越界计入 excludedToolPaths；不推测 bash、外部程序、自定义工具的副作用，也不把本次 write 范围说成所有变更。new/deleted 分类引用查询时的工作区状态，**不能证明创建/删除由这个工具导致**；已确认写入文件后来消失且无 Git 证据仅标 missing。未来扩展其他工具要增加已核实路径/结果提取器。

**patch/hunk 读取（A13 新增，只读）**：`DiffQuery.FileAsync(projectPath, relativePath, contextLines=3, maximumLines=20000, ct)` → `FileDiff(path,oldPath,isBinary,truncated,hunks,error)`，`DiffHunk(header,oldStart,oldLines,newStart,newLines,lines)`、`DiffLine(kind,text)`，`kind` ∈ `+`/`-`/空格/`\`（无换行标记）。走 `git diff --no-ext-diff --no-textconv --no-renames -U<N>`（无 HEAD 退回 `--cached`），同样禁用可选 index 更新/fsmonitor。`error` 分类：`not-a-git-worktree`/`git-diff-failed`/`path-escapes-project`/`reparse-point-refused`/`git-unavailable`；二进制文件返回 `isBinary:true` 且无 hunk。越出项目的路径与链接/联接一律拒绝。行数预算截断时 `truncated:true`，**不得把截断的 hunk 当完整差异**。仍然**不实现**还原、stage、提交与写入——D 交 A 清单第 1 项里的写操作留待独立立项。

CLI：`changes list [--scope workspace|session] --project <path> [--session <id>] --json`。workspace 不读会话；session 默认选择该项目最近已登记会话，通过 `ReadOnlySessionReport.Load` 读取 JSONL，不启动 Pi、不拿 GUI 会话锁、不迁移/更新 SQLite。没有会话返回 sessionId:null；显式 ID 不属于当前项目失败。真实 App 可传 `History.CurrentBranchMessages` 加 `Coordinator.Snapshot.Tools`，没有新 UI 依赖。

### P0-3 用量：会话累计、每轮与未知

- `SessionUsageQuery.Query(messages, activeMessageIndex?)` 是 message list 纯投影；`MessageTimeline.GetUsage()` / `ChatSessionCoordinator.GetUsage()` 使用该投影并排除当前尚未 message_end 的 assistant，显式返回 excludedActiveAssistantCount。更新 delta 是同一响应的累计状态，不逐事件相加；终态每 assistant 消息计一次，同一轮多次模型/工具环响应各计一次。
- `SessionUsageQuery.QueryHistory(history)` / `SessionHistory.GetUsage()` 汇总 **所有已记录 session entry、所有分支**；每唯一 assistant entry 计一次，含 compaction 前保留在 JSONL 的消息。每轮锚点为最近祖先 user entry，`UserEntryId` 稳定，不能按 append 顺序把弃分支消息错归到另一个用户。无 user 祖先的 assistant 属 turn0；无回复用户仍有一轮，其值 unknown。与 current-branch live 投影必须分开标识。
- `UsageTotals` 的 input/output/cacheRead/cacheWrite/totalTokens 各是 `UsageCounter(value,knownSubtotal,knownMessageCount,unknownMessageCount)`。该分项任一 assistant 缺失时 value=null；已知部分只放 knownSubtotal，完全没有已知值（含空会话）subtotal=null。确实测得 0 才保留 0。totalTokens 仅累加 Pi 原始字段，不由 input/output/cache 推造；保留 Int64，显示层缩窄溢出不得变零。本轮不估算供应商额度、费用、context 占用。
- 恢复历史后重复查询纯投影，不重复累计。入口锚点在 `MessageTimeline` / Pi raw history；D 的 `HistoryMapper` 与 Core `MessageBlockMapper` 中 `UsageInfo` 是显示映射，不应用缺字段 `?? 0`，也不应用 synthetic `history-n` 当跨恢复的用量去重键。计费账单、实际跨重试请求成本、fork 新增成本不等于 JSONL 消息 token 累计；fork 拷贝的继承消息会包含在其 recorded history 中。

CLI：`usage show --project <path> [--session <id>] --json` 返回 scope=`recorded-session-all-branches`、sessionId、partialTail、report.session 与 report.turns。读取 metadata schema 2 的 ReadOnly 连接以及项目目录内登记 JSONL，拒绝越界/链接，32 MiB 有界读取；不改变用户设置、会话 active/lastOpened 或 Git index。并发写入的无换行无效尾行显式 partialTail=true，返回已完成前缀，不能声称整轮/整个文件已完整统计；完整行损坏则失败。当前协议没有持久化 turn_settled 到 JSONL，因此每轮表示 user 锚定的消息组，不承诺恢复后可断言该轮已可靠 settled。

### P1-2 可靠终态通知（设计；不新增 OS 投递）

现有订阅口为 `PersistentSession.Chat.Event` 的 `SessionEvent.TurnSettled`：`PiChatSessionAdapter` 仅由 `agent_settled` 发出，Coordinator 先设置 Idle/Completed|Failed|Interrupted|Unknown 再发布协议事件。不得从 `agent_end`、prompt ack、Activity Idle、最后一条文本、handled 扩展命令或历史恢复伪造完成通知；Unknown 不发成功通知。进程 fault 单独故障通知，不合成 settled。

D 在提交前订阅，关闭/切会话时退订；回调可能在 RPC 线程，快速入本地有界 Channel，OS 通知消费者异步处理。使用 sessionId + connection generation + 本地 submission/run 序号关联；settled 后同 run 只提醒一次，客户端本地序号不是 Pi 提供的 runId。当前事件没有回放、持久化、sequence，也没有已实现的跨进程订阅口；不能承诺重启/断线后 exactly-once。失焦策略、开关/声音、点击回该会话由 D 的 OS 层处理；通知只含用户可见摘要，不能带 tool args/key。可靠性下一步需测试双通知去重、取消/失败/进程退出、后台失焦、点击回会话与断线边界；本轮仅源码核对现有事件语义。

### P2-1 固定 Pi 0.87.1 fork/分支前置核实（不实现产品 fork）

使用已安装 SDK 构造合成消息文件，真实 Node/Pi RPC 无模型调用，结果见 `docs/progress.md`。已核实 `get_fork_messages` 返回用户 entryId/text；`fork {entryId:<user>}` 默认选中用户消息 **之前** 分叉，新 sessionId/file，响应 text 是该用户文本、cancelled=false；`clone` 完整复制当前 leaf 路径到新文件；两种子 JSONL header version=3，parentSession 是父 JSONL 绝对路径。`get_entries/get_tree` 返回 id/parentId/children/leafId。SDK `SessionManager.branch` 同文件改 leaf 保留旧分支，已实测；这不是标准 RPC 命令。

`navigate_tree` RPC 实测失败 `Unknown command: navigate_tree`；无效 fork ID 失败 `Invalid entry ID for forking`。树导航仅在已安装 SDK/扩展 ctx.navigateTree 中，不可虚构标准 RPC 能力。

**A13 已落地的部分（fork 元数据 + 锁交接）**：SQLite schema 3 为 sessions 增加 `parent_session_id`/`fork_entry_id`/`forked_at_utc`（每列先探测存在性再 ALTER，因此 user_version 落后但列已存在时不会报重复列；备份文件名随之变为 `desktop.db.before-v3-*`）。`DesktopMetadataStore.CreateForkSession` 建子行并继承父行名称；`PersistentSessionManager.ForkAsync(session, entryId)` 走 `fork` RPC → `get_state` 取新 sessionId/sessionFile → 校验新文件仍在项目会话目录内 → **先取子会话锁再交还父会话锁** → 登记子行 file_path、父行 `was_active=false` → 重载 entries 并 `RestoreHistory` → 父会话对象交出 Pi 所有权（`TransferOwnershipAsync`），此后 dispose 父对象不会杀掉共享进程。父文件 fork 前后 SHA256 作为 `ParentFileUnchanged` 证据返回，**不是 Pi 承诺**。仍**未实现**：另存子会话/clone 的产品入口、同文件树导航、共享工作目录的额外提示、跨进程 fork 冲突与 UI 入口。

源码依据均为本机 pinned 包 dist：`modes/rpc/rpc-mode.js` 的 fork/clone/get_fork_messages/get_entries/get_tree；`core/agent-session-runtime.js` 的 fork position before；`core/session-manager.js` 的 branch/createBranchedSession/parentSession。模型 fork 后续跑、运行中 fork、扩展取消、首消息空 fork、原文件逐字不变、原 parentSession 参数与 OS 通知均未验收；A8 不增加 fork 命令/元数据字段。
