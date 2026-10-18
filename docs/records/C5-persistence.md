---
task: C5-PERSIST
agent: codex
status: complete
runId: c5-persist-9fde
---

# C5-PERSIST 交付记录：原生控制客户端的本地草稿与请求记录模块

任务：C5-PERSIST｜难度：L3｜主模型 `group/auto-qfmodel`（实际执行：本会话 Codex Desktop 隔离工作树）。
任务书：`docs/tasks/hourly-C5-persistence.md`。状态：**模块交付待接线**（公共接线归主控 M）。

> 第 1 轮（提交 `28fcad6`/`21d6bc2`/记录 `5340981`+`0ebc1fa`，HEAD `0ebc1fa`）被 M 审核 **rework / not_integrated**，
> 4 个 finding。第 2 轮返工见 §9（当前 HEAD `09a9434`）。§1–§8 保留第 1 轮原文以便对照，其中数量类结论以 §9 为准。

## 1. 开工身份

| 项目 | 实际值 |
| --- | --- |
| 工作树绝对路径 | `C:\Users\mio\.codex\worktrees\9fde\Fufucode` |
| 分支/HEAD | `## HEAD (no branch)`，detached（App 授权新建的隔离工作树，未自行同步未来 main） |
| 起点 SHA | `9b06d6ce49db8e7df870a0691afb9a7bf3472274`（含 C5 任务书；冻结产品增量 `a35f360` 为其祖先） |
| 继承状态 | 开工时 `git status --porcelain` 干净，无继承用户改动，无未合并提交需要收编 |
| 允许范围 | 只写 `src/Fufucode.App/Features/Automation/Persistence/**`、`tests/Fufucode.App.Automation.Persistence.Tests/**`、本记录 |
| 依赖证据 | 起点已含 C5 第二增量（`fefe3bd`→`a35f360`）：`AutomationStore.ApplyControl*` 八个入口与 `ControlDecisionItem(Id, Kind, ObjectId, Version, EvidenceToken, Actionable, BlockedReason, Options, CommandType, ContextJson)` 已在源码内，草稿字段据此设计；未打开任何 Core 库 |

## 2. 本任务提交

| SHA | 范围 |
| --- | --- |
| `28fcad64ca78486667bda7f88ca87e22875a5646` | 持久化模块本体 12 个源文件（`src/.../Persistence/**`），1830 行新增 |
| `21d6bc2a53ebf86f367c10e3a66dd8430aa82077` | 独立无云验证工程 3 个文件（`tests/Fufucode.App.Automation.Persistence.Tests/**`），617 行新增 |
| 本记录 | `docs/records/C5-persistence.md`，首次提交 `5340981b68db01312fca622e88d85f6d0cef939b`（只含文档；本文件按协议再补一次 SHA，见下） |

第 2 轮（返工）新增提交见 §9.2。

`git diff --name-only 9b06d6c..HEAD` 全部落在上面两个允许目录内；`AutomationControlViewModel*`、`AutomationControlPageView*`、`src/Fufucode.Core/**`、组合根、`MainWindow*`、`Settings`、`Styles`、`SelfTestRunner`、`Fufucode.sln`、`docs/progress.md`、`docs/decisions.md`、failure-registry 与旧队列 **零改动**（已用 `git diff --stat` 核验为空）。未 push、未 reset、未清理工作树、未派下一任务。

## 3. 实现内容

新增文件（`src/Fufucode.App/Features/Automation/Persistence/`）：

| 文件 | 职责 |
| --- | --- |
| `AutomationPersistenceScope.cs` | scope 三字段绑定与规范化；Windows 大小写/斜杠归同一路径，非 Windows 不套用 Windows 比较语义；文件名只取 SHA-256，用户 key 不参与路径拼接 |
| `AutomationPersistenceModels.cs` | `AutomationDraftEntry`、`AutomationRequestRecord`、`AutomationRequestKind`（apply/review/reconsider/repair/integration/knowledge/relation/restructure，无 runtime/shell）、`AutomationRequestState`（仅 `pending_unknown`→`receipt_observed`） |
| `AutomationPersistenceLimits.cs` | 保守容量/UTF-8 上限，可按实例收紧，没有关闭校验的开关 |
| `AutomationPersistenceDiagnostics.cs` | 16 个错误码 + `AutomationPersistenceResult<T>`；成功但值为 null 是合法结果（"这条草稿不存在"），不当失败也不抛 |
| `AutomationPersistenceJson.cs` | 自写 reader 严格校验：拒绝注释、任意层级重复键、未闭合、非对象/数组根 |
| `AutomationPersistenceValidators.cs` | 草稿/请求条目自校验：标识、上限、JSON 合法、**摘要按原文重算**（不信任调用方哈希）、状态机合法、时间单调 |
| `AutomationPersistencePathGuard.cs` | 目录内自证 + 逐级重解析点检查；属性不可读按不安全处理，不跟随链接 |
| `AutomationPersistenceFileIo.cs` | 唯一磁盘边界 `IAutomationPersistenceFileIo`（可注入）；声明读取共享模式 `AutomationPersistenceReadPolicy.ReaderShareMode` |
| `AutomationPersistenceDocument.cs` | 落盘文档（`format`/`formatVersion`/`scope`/`generation`/`updatedAtMs`/`drafts`/`requests`）与双向严格转换；未来版本不降级、旧版本不隐式迁移、未知字段拒绝 |
| `AutomationPersistenceWriter.cs` | 独占锁 → 锁内重读 → `expectedGeneration` 核验 → generation+1 → 同目录临时文件 `Flush(true)` → `File.Replace`/`Move` 原子发布；任一环节失败保留原字节，失败清理临时文件 |
| `AutomationDraftStore.cs` | 草稿 Save/Load/Find/OpenFor/Remove；同 ID 同输入幂等且不改字节，同 ID 不同内容视为用户改写，`OpenFor` 在版本/证据前进时返回 `DraftSuperseded` 而不自动套用 |
| `AutomationRequestJournal.cs` | 不可变请求 Record/ObserveReceipt/Load/ListPendingUnknown/Find；同 commandId 不同 payload 拒绝、回执不可变、未知 commandId 拒绝；**没有任何执行/发送/重发入口** |

设计边界：本模块不调用 Core、不打开数据库、不启动 harness、不解析或迁移 Core 库、不判断"新路径等于旧权威"，也不删除用户草稿或来源；只读写自己固定目录（`<dataRoot>/automation-control-state-v1/<scopeHash>.json`）。

## 4. 实际验证（真实临时文件，零模型调用）

| 命令 | 结果 |
| --- | --- |
| `dotnet build tests/Fufucode.App.Automation.Persistence.Tests` | **已成功生成。0 个警告 0 个错误** |
| `dotnet run --project tests/Fufucode.App.Automation.Persistence.Tests` | 退出码 0，`C5-PERSIST 定向验证通过：135 项断言`，**0 项 SKIP** |
| `dotnet build src/Fufucode.App/Fufucode.App.csproj` | **0 个警告 0 个错误** |
| `Fufucode.App.exe --selftest`（App 全量无头自测，含本模块改动后构建） | 退出码 0，`== 自测结束：0 项失败 ==`，1076 条 `ok:`，stderr 0 字节 |

起点基线：改动前 `dotnet build src/Fufucode.App/Fufucode.App.csproj` 已是 0 警告 0 错误；起点未跑过 App 全量 selftest（改动后跑过，见上表），因此不声称基线自测对比。
证据位置：控制台全文 `tmp/c5-persist-evidence.txt`（本工作树，gitignore 内）；测试数据根 `tmp/c5-persistence-tests/20261012-191617-140/`；自测截图证据 `%TEMP%\fufucode-selftest-dea7bef7295b428ea58cbc68ab23295d\evidence\`。

135 项断言覆盖任务书验收点（逐条真实反例，不镜像实现、不把不支持项算 PASS）：

- 两 store 实例读到同一草稿与逐字段原样的 context/evidence/reason/response/候选 ID/version；请求原文与 SHA-256 相符；写请求不丢草稿。
- 同库路径但 projectKey 或 authorityIdentity 不同 → 隔离并各自读空；Windows 大小写与 `\`/`/` 差异 → 同 scope 同文件名。
- 空 key/空身份/相对路径拒绝，且**只做校验不产生任何磁盘副作用**；含 `../` 的 key 仍落在固定目录内，文件名只是哈希。
- 同 ID 同输入幂等且**文件字节完全不变**；同 `commandId` 不同 payload 拒绝且原字节保真。
- 同 generation 竞争只有一方成功、失败方明确 `GenerationConflict` 且**不丢稿**；新 generation 重试后两条共存；请求日志与草稿共享同一 generation。
- 原子失败三类真实路径：目标被 `FileStream(FileShare.None)` 独占锁占用 → 失败且释放锁后逐字节一致、无临时文件残留；注入 `Publish`/`WriteTempFile` 抛真实 `IOException` → `PublishFailed`/`TempWriteFailed` 且目标不动；`.lock` 被另一持有者占用 → `LockContended`。
- 读者按声明共享模式持句柄时写方仍能原子发布，且该读者看到的仍是它打开时刻的旧字节（不被半写污染），释放后新内容可见。
- 拒绝且保原字节：非法 JSON、重复顶层键、`formatVersion:999` 未来格式、未知顶层字段、scope 项目/库路径错配、摘要不符、非法状态、尾部截断、900 条超限、写入侧条目/UTF-8 超限、调用方伪造哈希；每种反例后都断言磁盘内容仍是"被拒绝的原文"。
- `pending_unknown` 反复对账读取不追加请求、不推进 generation、不改状态；只有显式 `ObserveReceipt` 前进一步；同回执重放幂等且不追加新请求；不同回执/未知 commandId/非法回执 JSON 全被拒且不改原状态。
- 旧草稿不自动套用到新版本、新证据或不符的待办 ID（`DraftSuperseded`），过期草稿仍在盘上。
- junction 反例（本机 `mklink /J` 真实建成）：数据根是链接 → `ReparsePoint` 拒绝；正常目录但**该 scope 目标文件位**是链接 → 读写都拒绝，外部目录内容与临时文件均不受影响。
- 结构性证据：模块公共 API 无 Apply/Send/Dispatch/Execute/Retry/Migrate/Approve/Integrate 类动作；源码 0 处引用 `Fufucode.Core`；0 处出现 Sqlite。
- 6 线程同 scope 并行：结果只有"成功"或"明示冲突"，盘上条数等于成功数且无重复 ID。

传递引用剥离核验：测试工程沿用 `StripUnusedWebView2ManagedRefs` 写法，其 `bin/Debug/net10.0` 只有 `Microsoft.Web.WebView2.Core.dll`，**无** `Microsoft.Web.WebView2.Wpf.dll` / `WinForms.dll` / `WindowsBase` 冲突（与 Storage.Tests、Library.Tests 现状一致）；全程未降警告、未加 `NoWarn`/`#pragma warning`/`SuppressMessage`、未关审计、未放宽任何断言（已 grep 确认为空）。

### 实测中发现并当场修复的缺陷（不属放宽验收，而是真实反例暴露）

1. `Path.GetFullPath` 会按进程 cwd 补全相对路径，导致"必须是绝对路径"检查形同虚设、scope 会随 cwd 漂移 → 改为在展开前用 `Path.IsPathRooted` 判定原始输入。
2. 数据根自身是 junction 时原检查漏网（固定目录会被链接带到任意位置）→ 写入前增加 `ReparseIssue(DataRoot)` 拒绝。
3. 读取侧条目校验失败会经 `ValueOrThrow()` 抛异常而非返回可诊断失败 → 改为 `IsSuccess` 分流，同时让"成功但值为 null"成为合法结果。
4. JSON 超 UTF-8 上限被误归为 `InvalidFileContent` → 拆分错误码，超限归 `Oversize`。

## 5. 未验证项（如实）

- **UI 接线未做、未验证**：本模块没有接 `AutomationControlViewModel`/`View`、组合根、`MainWindow.Workspace`、Settings、动作目录或全量 `SelfTestRunner`/sln。任务书禁止越界，M 同期在做创建表单公共入口。
- **原生指针/IME/DPI/焦点验证未做**：本模块无 UI，属 M 接线后的 V 门范围；无头证据不替代原生验收。
- **真机跨进程（两个 App 实例同时打开）未跑**：等价机制已由同进程多实例 + 真实文件锁 + `.lock` 独占句柄 + 6 线程并行覆盖，跨进程仍属推断而非实测。
- **非 Windows 平台语义未实测**：路径大小写敏感分支按 `OperatingSystem.IsWindows()` 分流实现并有单测点，但本机只有 Windows。
- **长稳/断电真实场景未跑**：只验证了 `Flush(true)` + 同目录原子发布的代码路径，没有做掉电注入。
- **模块交付 ≠ C5 整项验收**，也不等于跨重启 UI 功能可用或生产验收。

## 6. 精确 API 与 M 的接线要求

命名空间 `Fufucode.App.Features.Automation.Persistence`（App 工程内，M 无需加工程引用）。

```csharp
// 1) 构造（dataRoot 用用户明确提供的数据根，与 AppDataLayout 一致；authorityIdentity 由 M 从稳定库身份取得）
var scope = AutomationPersistenceScope.TryCreate(projectKey, databaseFullPath, authorityIdentity);   // 失败 => InvalidArgument
var drafts  = new AutomationDraftStore(dataRoot);      // 可选 limits / IAutomationPersistenceFileIo
var journal = new AutomationRequestJournal(dataRoot);

// 2) 草稿：todo 来自 ControlDecisionItem(Id/Kind/ObjectId/Version/EvidenceToken/ContextJson)
var gen = drafts.ReadGeneration(scope).ValueOrThrow();                       // 准备预期值（不存在=0）
drafts.Save(scope, new AutomationDraftEntry { DraftId, TodoId, TodoKind, TodoVersion,
              ContextJson, ContextSha256 = AutomationPersistenceValidation.Sha256HexUtf8(ContextJson),
              EvidenceToken, EvidenceTokenSha256 = ..., Reason, Evidence, Response,
              SelectedCandidateIds = [...] }, expectedGeneration: gen);       // GenerationConflict 时提示重取再存
drafts.Load(scope); drafts.Find(scope, draftId); drafts.Remove(scope, draftId, gen);
drafts.OpenFor(scope, draftId, todoId, todoKind, currentVersion, currentEvidenceToken, currentContextJson);
      // DraftSuperseded => 不自动套用，保留旧草稿由用户核对

// 3) 发送前记录不可变请求（payloadJson 就是即将交给 ApplyControl* 的命令 JSON）
journal.Record(scope, new AutomationRequestRecord { CommandId, Kind, PayloadJson,
              PayloadSha256 = AutomationPersistenceValidation.Sha256HexUtf8(PayloadJson),
              CreatedAtMs, State = AutomationRequestState.PendingUnknown }, expectedGeneration: journalGen);
journal.ListPendingUnknown(scope);   // 只列给人核对：不执行、不重发、不重写 revision
journal.ObserveReceipt(scope, commandId, receiptJson, observedAtMs: receiptTime);  // 唯一状态前进入口
journal.Find(scope, commandId); journal.Load(scope); journal.ReadGeneration(scope);
```

M 接线时必须保持的约定：

1. `authorityIdentity` 只能由 M 提供（数据库稳定身份）；模块不会自己判定新旧路径等价，也不打开库。
2. 每个 scope 的 `expectedGeneration` 由 UI 在读写之间携带；冲突提示"已在他处更新，请重取"，不得改成无条件覆盖。
3. 写请求发生在调用 `ApplyControl*` **之前**；`ObserveReceipt` 只在真正观察到 Core 回执之后调用，回执文本按原样存。超时/断开等结果未知情形保持 `pending_unknown`，由人工对账，模块与 UI 都不自动重发。
4. 读取失败（`InvalidFileContent`/`CorruptRecord`/`Oversize`/`ReparsePoint` 等）时**不得**重置、覆盖或删除用户文件，只提示；恢复手段属人工操作。
5. 需要 UI 提示时可复用 `AutomationPersistenceResult<T>.Failure.Code/Message`；`Message` 已含具体 scope/字段，可直接展示且不泄露库内容。
6. 接线到 `AutomationControlViewModel`/`MainWindow` 注册、Settings、全量 runner/sln 由 M 单写完成；本模块已避免任何共享文件冲突。

## 7. 补录

- 本记录首次提交 SHA：`5340981b68db01312fca622e88d85f6d0cef939b`。
- 补录该 SHA 的文档提交本身在 `git log --oneline -5` 可见（协议不允许文件写自己所在提交的 SHA，故不虚构）。

## 8. 状态

**模块交付待接线**。原会话不再写代码（本记录提交后停写，等待 M 独立复核）。
## 9. 第 2 轮返工（2026-10-12，响应 M 首轮 rework）

审核来源：`C:/Projects/Fucode/Fufucode/docs/records/M-C5-persistence-review-2026-10-13.md` 与
`docs/records/C5-persistence-review-rework-2026-10-13.json`（decision=rework，integrationStatus=not_integrated，mainSha=null）。
探针：`C:/Projects/Fucode/Fufucode/tmp/c5-persist-review/Program.cs`（本次**未修改地**复制回放到 `tmp/c5-m-review-replay/`）。
原模型、原会话、原工作树、原难度 L3；未同步/整合 main，未 push/reset/清树。

### 9.1 四个 finding 的根因与修法

| finding | 根因（实测复现） | 修法（不靠禁止合法文本遮掩） |
| --- | --- | --- |
| C5-PERSIST-SCOPE-1 | `ScopeHash` 用 `U+2224` 作分隔符 join，而 `U+2224` 是合法字符且不被控制字符校验拒绝，故 `(a∤C:\X, C:\Y, z)` 与 `(a, C:\X, C:\Y∤z)` 编码后完全相同 → 文件名碰撞，破坏「不同 scope 独立存储」契约 | 新增 `AutomationTupleEncoding`：**长度前缀**编码（`字符数 + U+241F + 字段 + U+241F`）。字段内容无法伪造边界，不同三元组必得不同哈希。ID/路径/身份的合法字符集与校验不变 |
| C5-PERSIST-PATH-1 | `Path.IsPathRooted` 对 `C:relative.db`（盘相对）与 `\relative.db`（根相对）都返回 true，`GetFullPath` 再按当前盘/cwd 补全 → scope 随 cwd 漂移 | 展开前改用 `Path.IsPathFullyQualified`（要求同时含根与盘，UNC 亦可），两种漂移写法都拒绝；完整绝对路径与大小写/斜杠等价性**保持不变**（P2 断言正面验证，没有误伤） |
| C5-PERSIST-FINGERPRINT-1 | 幂等判定用 `join(U+241F)` 与 `join(",")`，字段值本身可含这些字符 → `Reason="one␟two"/Evidence="three"` 与 `Reason="one"/Evidence="two␟three"` 指纹相同被误判幂等而**静默丢稿**；`["a,b"]` vs `["a","b"]` 同理 | 指纹改用 `AutomationTupleEncoding.Encode(...)` + `EncodeSequence(候选)`（元素数与每个元素边界都进编码）。请求侧 `ContentFingerprint` 同步。不禁用、不转义、不丢弃任何合法用户文本 |
| C5-PERSIST-GENERATION-1 | 4 个写入口的 `expectedGeneration` 默认 `null` 时按「接受当前磁盘状态」盲写，违背任务书「写入须预期 generation 匹配」 | 单一门放在唯一的发布点 `AutomationPersistenceWriter.Mutate`（`Save`/`Remove`/`Record`/`ObserveReceipt` 全部经它，已 grep 确认无旁路）：`null` → 新错误码 `MissingExpectedGeneration` 明示拒绝。参数保留可选签名，使调用点得到**可诊断失败结果**而非编译期改动；`GenerationConflict` 检测不受影响 |

附带自查修的 2 处（非 M finding，返工中发现）：F1 用例的预期 generation 必须在加锁前取（否则测到的是读路径）；junction 用例改为显式预期值，以证明拒绝来自写路径自身。

### 9.2 第 2 轮提交

| SHA | 范围 |
| --- | --- |
| `d3bddfdf5d72b7248b31e2a7fef3f7e70c108882` | 模块修复：新增 `AutomationTupleEncoding.cs`，改 `Scope`/`Models`/`Writer`/`Diagnostics`/`DraftStore`/`RequestJournal` 的注释与逻辑 |
| `09a943484bf1a3bf35167a9d2e6245d3b4267791` | 测试工程：新增 §P（P1–P6）并把所有写入改走带预期 generation 的 `Gated*`，163→166 断言 |
| 本轮记录 | `docs/records/C5-persistence.md`（含本节的 SHA 补录，见 §9.6） |

`git diff --name-only 0ebc1fa..HEAD` 仅 8 个文件，全在允许目录内；相对原起点 `9b06d6c` 的越界检查仍为空。

### 9.3 第 2 轮实际验证（真实文件、零模型、未改 M 探针）

| 命令 | 结果 |
| --- | --- |
| `dotnet build src/Fufucode.App/Fufucode.App.csproj` | 0 警告 0 错误 |
| `dotnet build tests/Fufucode.App.Automation.Persistence.Tests` | 0 警告 0 错误 |
| `dotnet run --project tests/Fufucode.App.Automation.Persistence.Tests` | 退出码 0，`166 项断言`，**PASS=166 / SKIP=0**（含原 135 项严格保留） |
| `dotnet run --project tmp/c5-m-review-replay`（**逐字复制的 M 探针**，只链接模块源、仅 BCL） | 退出码 0，`Review failures: 0`，五条反例全部 PASS（首轮为五条 FAIL） |
| `Fufucode.App.exe --selftest`（App 全量无头，返工后构建） | 退出码 0，`== 自测结束：0 项失败 ==`，1076 条 `ok:`，stderr 0 字节 |

M 探针五条逐字结果：distinct scope tuples… / drive-relative and root-relative… / changed reason+evidence… / candidate list element boundaries… / writes require an explicit expected generation → 均 `PASS`。

证据位置：`tmp/c5-persist-evidence.txt`（166 断言全文）；`tmp/c5-m-review-replay.log`；测试数据根 `tmp/c5-persistence-tests/20261012-195409-472/`；M 探针复放数据根 `%TEMP%\fufucode-c5-persist-review-d40ebddf9f854e12b4bb506d7bd1bd0f`；自测证据 `%TEMP%\fufucode-selftest-*\evidence\`。
`tmp/c5-m-review-replay/`、`tmp/c5-lock-probe/` 均在 gitignore 的 `tmp/` 内，未提交、未带入执行范围；M 的原探针目录未被写。

### 9.4 新增反例（P1–P6，与保留的 135 项同标准）

- P1：含 `U+2224` 的两个不同三元组 → `SameTargetAs=false`、`ScopeHash` 不同、可各自写入且互相读到空。
- P2：`C:relative.db`、`\relative.db`、`/relative.db` 全拒；完整绝对路径仍接受，且 `\`/`/` 等价与大小写等价未被收紧掉。
- P3：`␟` 文本的字段边界移动 → generation 推进且 `Find` 返回新文本；`["a,b"]→["a","b"]` 同理；顺序变化非幂等；**完全相同内容仍然幂等且字节不变**（幂等语义没被改坏）。
- P4：`Save`/`Remove`/`Record`/`ObserveReceipt` 省略预期 generation 各自 `MissingExpectedGeneration`，四次盲写后文件字节逐字节不变；补上预期值后同一写入成功（门不是永久失败）。
- P5：必填门之后两实例竞争仍只一方成功、失败方 `GenerationConflict` 且不丢稿；带预期 generation 的 `Remove` 正常生效。
- P6：旧命名派生留下的孤儿文件不被读取、迁移、覆盖或删除（模块只碰自己按当前编码算出的文件名）。

### 9.5 第 2 轮未验证项（如实，追加在 §5 之上）

- `ScopeHash` 编码变更导致**第 1 轮落盘文件的文件名不再被本轮识别**：本轮未做（也无权限做）任何迁移，接线前若已有真实文件会表现为「读到空」。P6 只证明旧文件被原样保留、不被破坏。是否迁移/废弃由 M 决定，建议 M 在接线时直接作废首轮测试数据（无生产数据）。
- `expectedGeneration` 保留可选签名（缺失即失败）。若 M 更希望编译期强制，可改为必填参数；本轮选择失败结果而非改签名，是为了不改变调用形态同时满足「缺 generation 明示拒绝」。
- 其余未验项与 §5 一致：UI 接线、原生指针/IME/DPI、真机跨进程双实例、非 Windows 平台、掉电注入均未跑。
- **模块交付 ≠ C5 整项验收**，也不等于跨重启 UI 功能可用或生产验收。

### 9.6 本节 SHA 补录

本轮首个记录提交 SHA 在写入时无法自指，故在此补录：`docs/records/C5-persistence.md` 的本节首次提交与后续补录提交都可在 `git log --oneline -6` 直接核对（HEAD 为最后一个文档提交）。

## 9.7 状态

**模块交付待接线**（第 2 轮）。四个 finding 已按 M 探针逐字复放证明修复，原 135 项断言全部保留并通过。本会话停写，等待 M 独立复核。

## 10. 第 3 轮返工（2026-10-12，响应 M 接线前新增两项 finding）

审核来源：`M-C5-persistence-review-2026-10-13.md` 顶部新段「后续接线前反例」与
`C5-persistence-review-extra-rework-2026-10-13.json`（decision=rework，integrationStatus=not_integrated，mainSha=null，
findings=C5-PERSIST-OPAQUE-ID-1、C5-PERSIST-NULL-VERSION-1）。前四项 verified 与上一轮接受事实不撤回；本轮不越权接线。
M 新探针 `tmp/c5-persist-scope-review/Program.cs` 被逐字复制到本树 `tmp/c5-scope-review-replay/`（只链接本模块源码、仅 BCL），M 原目录未被修改。

### 10.1 根因与修法

| finding | 根因（源码定位） | 修法 |
| --- | --- | --- |
| OPAQUE-ID-1 | `TryCreate` 以 `projectKey.Trim()` / `authorityIdentity.Trim()` 存值并按 Trim 后长度判上限，使 `project` 与 ` project `、`authority` 与 ` authority ` 归一为同一 scope；真实文件可被另一身份读回，不只是哈希层面 | 身份逐字保真：存原值，不 Trim、不折叠大小写；上限按原始长度判；纯空白仍拒（那是「没有身份」而非「带空格的身份」）。`CheckIdentifier` 同步同一规则（DraftId/todoId 等也逐字保真）。未新增任何合法字符限制 |
| NULL-VERSION-1 | 读侧用 `draft["todoVersion"]?.GetValueKind()` 判类型，而 JsonNode 把 JSON null 表示成 C# null 引用，`?.` 得 null 后落进 `is not JsonValueKind.Null` 分支被拒；写侧对 null 输出显式 JSON null，读写契约不一致，于是一份合法无版本草稿使整个 scope 不可读 | 新增 `Kind(owner,name)`：用 `ContainsKey` 与显式 `JsonValueKind.Null` 区分「省略」与「null」，两者都还原成 `long? null`（写侧格式不变）。字符串/布尔/对象/数组与超 long 范围仍严拒，未来格式拒绝不变；不造 0 版本、不禁止合法 null、不清空旧 scope |

自查纠正 2 处（返工过程中自己引入/发现）：
1. 我用行号写入覆盖掉 `TryCreate` 的局部 `Fail` 辅助方法与闭括号，已按 `git diff` 复核补回。
2. 测试首版用超长绝对库路径去验 `MaxIdentifierLength=10`，而库路径同受该上限约束会先撞限、区分度失真；改用短路径后断言才真正测到身份长度语义。

### 10.2 第 3 轮提交

| SHA | 范围 |
| --- | --- |
| `42bd095c249c0eddb6271bb56719309d489bca98` | 模块修复：`AutomationPersistenceScope.cs`（身份逐字保真）、`AutomationPersistenceModels.cs`（标识规则一致）、`AutomationPersistenceDocument.cs`（`Kind()` 读写契约） |
| `fda1ea98ae3938fff6158f86c7d190cd256e091e` | 测试工程 §Q（Q1/Q2 反例共 26 项新断言，166→192） |
| 本轮记录 | `docs/records/C5-persistence.md` 的 §10（含本节的 SHA 补录） |

`git diff --name-only 5b546f2..HEAD` 仅 4 个文件（3 模块 + 1 测试），全部在允许目录内；相对原起点 `9b06d6c` 的越界检查仍为空。共享 VM/View/Core/组合根/全局记录零改动。

### 10.3 第 3 轮实际验证（真实文件、零模型）

| 命令 | 结果 |
| --- | --- |
| `dotnet build src/Fufucode.App/Fufucode.App.csproj` | 0 警告 0 错误 |
| `dotnet build tests/Fufucode.App.Automation.Persistence.Tests` | 0 警告 0 错误 |
| `dotnet run --project tests/Fufucode.App.Automation.Persistence.Tests` | 退出码 0，`192 项断言`，PASS=192 / SKIP=0。分解（按本轮日志的断言标题前缀实测）：§Q 新增 26 项，其余 166 项即上一轮通过的全部断言，逐条原样保留未放宽。 |
| `dotnet run --project tmp/c5-scope-review-replay`（M 本轮新探针，逐字复制） | 退出码 0，`Scope review failures: 0`，四条全 PASS（M 日志 `tmp/m-c5-persist-extra-red.log` 为四条 FAIL） |
| `dotnet run --project tmp/c5-m-review-replay`（M 上一轮五反例探针，未改） | 退出码 0，`Review failures: 0`，五条仍全 PASS |
| `Fufucode.App.exe --selftest`（App 全量无头，本轮构建） | 退出码 0，`== 自测结束：0 项失败 ==`，1076 条 `ok:`，stderr 0 字节 |

M 四条逐字结果：opaque project identity is preserved and isolated exactly / opaque authority identity is preserved and isolated exactly / distinct exact project or authority keys cannot read another scope's persisted draft / a successfully saved nullable proposal version remains readable, without poisoning the whole scope。

证据位置：`tmp/c5-persist-evidence.txt`（192 断言全文）、`tmp/c5-scope-review-replay.log`、测试数据根 `tmp/c5-persistence-tests/20261012-203017-763/`、M 探针复放数据根 `%TEMP%\fufucode-c5-scope-review-1bb6ecfacd394b1c9d726365877023ef` 与 `%TEMP%\fufucode-c5-persist-review-c5bf975ed61b418bb0f842dddd72b1b1`。复放工程均在 gitignore 的 `tmp/` 内，未提交。

### 10.4 新增反例（§Q，26 项）

- Q1 身份保真：`" project "` / `" authority "` 原样保留；与无空格版本 `SameTargetAs=false` 且 `ScopeHash` 不同；exact 落盘后另一身份 `Load` 读到空、按同一 `DraftId` `Find` 也为 null，exact 自己仍完整可读；纯空白身份与纯空白 `DraftId` 被拒；上限按原始长度（10 字符接受、12 字符即 Trim 后等于 10 也拒）；`" id-with-spaces "` 逐字往返，去掉空格的 key 找不到它。
- Q2 null 版本：`TodoVersion=null` 的 knowledge 草稿 Save→Load/Find/OpenFor 全部保真且仍为 null（不造 0）；`OpenFor` 不因「当前有版本号」把无版本草稿判为过期；再次写入生效且同内容仍幂等；写侧文件内是显式 `"todoVersion":null`；把该值改成字符串或布尔 → `InvalidFileContent` 严拒且拒绝后不改写文件；手写「省略字段」形态与显式 null 同义，都还原成 null；全部反例后整 scope 仍可正常读取（未被毒化）。

### 10.5 第 3 轮未验证项（追加）

- 身份逐字保真会改变 `ScopeHash`：首/二轮的临时夹具文件名在本轮识别不到（表现为读到空）。与 §9.5 同理，M 已裁定首版只有临时夹具、不自动迁移；真实旧数据如出现需另行可复核迁移。
- 其余与 §5、§9.5 一致：UI/公共接线、原生指针/IME/DPI、真机跨进程双实例、非 Windows 平台、掉电注入未跑。
- **模块交付 ≠ C5 整项验收**，也不等于跨重启 UI 功能可用或生产验收。

### 10.6 本节 SHA 补录

本节记录提交自身的 SHA 无法在文件内自指，请以 `git log --format='%H %s' -6` 核对（HEAD 即本轮最后一个文档提交）。

### 10.7 状态（第 3 轮）

**模块交付待接线**。两项新 finding 已修，M 本轮四条与上一轮五条反例逐字复放均 0 failures，192 项断言全通过、0 跳过。本会话停写，等待 M 独立复核。

## 9.8 接线契约增量（覆盖 §6 第 2 条，M 请以本节为准）

返工后写入门是**全入口必填**：`Save`、`Remove`、`Record`、`ObserveReceipt` 四个写方法都必须显式传 `expectedGeneration`（新 scope 用 `0`）。

- 省略该参数不再是「接受当前磁盘状态」，而是返回 `MissingExpectedGeneration` 失败结果（文件字节不动）。
- §6 代码片段里的 `journal.ObserveReceipt(scope, commandId, receiptJson, observedAtMs: receiptTime)` 需补成
  `journal.ObserveReceipt(scope, commandId, receiptJson, expectedGeneration, receiptTime)`，否则会按上述规则被拒。
- 推荐次序固定为：`ReadGeneration(scope)` → 组装 → 写入 → 用返回值里的 `Generation` 更新 UI 持有的预期值。
- `GenerationConflict` 语义不变：提示「已在他处更新，请重取」，仍然不得改成无条件覆盖。
- 错误码新增 1 个，共 17 个：原 16 个 + `MissingExpectedGeneration`。
