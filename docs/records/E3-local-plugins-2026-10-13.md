# E3-LOCAL：本地插件固定版本与失败恢复（2026-10-13）

任务：E3-LOCAL｜难度 L3｜推荐主模型 `group/auto-deepseek-v4-1-flash`｜实际执行 lead 为 `group/auto-deepseek-v4-1-flash`。

## 身份卡

| 项目 | 值 |
|---|---|
| 工作树绝对路径 | `C:\Users\mio\.codex\worktrees\2884\Fufucode` |
| 分支 / HEAD | detached HEAD（起点 `22f6bf4`） |
| 派工起点 SHA | `22f6bf4df764721d80dc59d613efad1dac5911c9`（仅派工文档，未同步未来 main） |
| 产品起点 SHA | `637bd9193df3536ae05ead09f1c094878ea5b89b`（已核实为派工起点祖先） |
| 继承改动 | 无：开工 `git status` 干净，全程未夹带他人改动 |
| 允许范围 | 新 `src/Fufucode.App/Features/Plugins/**`、独立 `tests/Fufucode.App.Plugins.Tests/**`、本记录、忽略的 `tmp/` 证据 |
| 未触碰 | Core/Pi/Hosting/Cli、现有 `ExtensionsPage*`/`ExtensionsPageViewModel`/`ProjectExtensionService`、MainWindow/Settings/Integration/Styles、`AppDataLayout`/`desktop.db`、solution、共享自测注册、包装、权威 progress/decisions、R1 候选包与其源 |

## 本次完成内容

新增独立模块 `Fufucode.App.Features.Plugins`（9 个文件），未接共享外壳（接线交 M）。

### 领域与服务

| 文件 | 职责 |
|---|---|
| `Contracts.cs` | 描述文件模型、宿主身份/快照/应用契约、状态与拒绝原因枚举、可嵌入注入接口 |
| `PluginDescriptorReader.cs` | `fufucode-plugin.json` 严格解析（id/displayName/version/backend/entryRelativePath/supportedHostVersions）；安全 ID 规则与显示名到路径的转义（`PluginIdRules`） |
| `PluginPackageScanner.cs` | 入口解析与越界拒绝、符号链接/reparse 拒绝、大小/数量上限、内容摘要（逐文件相对路径 + 字节 SHA256）、暂存复制 |
| `PluginCatalogState.cs` | `plugins/local/catalog.json` 版本化状态 + 原子写入（临时文件 + `File.Move(overwrite)`）+ 损坏/未来版本拒绝 |
| `LocalPluginService.cs` | 兼容判定、导入（校验→新暂存→原子发布）、应用/回退/移除、pending、代次 CAS、锁与失败保真 |

### 可嵌入控件

| 文件 | 职责 |
|---|---|
| `LocalPluginViewModel.cs` | 列表/选中/版本/预览/状态/错误绑定；命令：刷新、选目录、选入口、预览、导入、应用、回退、移除、按宿主快照确认 |
| `LocalPluginSectionView.axaml(.cs)` | 可嵌入现有扩展页的 UserControl，编译绑定（`x:DataType`），窄容器换行与两列→上下堆叠，`Attach/Detach` 生命周期与本地审查页一致 |

### 已实现行为（对应任务书 M 冻结首版）

1. **只本地导入固定版本，首个后端 Pi。** 目录 + 显式相对入口；描述文件字段齐全；不生成或修改 Pi/OMP 引擎配置。兼容规则为声明的精确宿主版本列表与注入宿主身份的精确匹配；`OMP/Codex/DSH` 明确 `unsupported`。
2. **副本与状态在 `AppDataLayout.Root/plugins/local`。** 稳定 ID/版本/后端/来源/摘要逐字绑定；路径用安全编码与内部 ID（不拼显示名）；导入先校验、复制到本轮新暂存、再原子发布；拒绝越界入口、链接遍历、畸形描述、超大包；同名版本内容不一致明确拒绝；不自动删除历史。
3. **状态来源分离。** 选定/待应用来自本产品目录状态，实际加载只来自调用方注入的真实宿主快照；文件存在或选定都不等于 active。宿主快照不可用时照常浏览/安装本地副本，应用不可用。
4. **更新/回退。** 活动使用或宿主状态未知只登记 pending，不原地改/删加载目录、不杀进程、不启动会话；应用经注入回调交 M 接入既有安全重启，无回调不假成功；失败保留旧选定版本与文件并保留诊断；成功后旧选定版本成为显式回退目标。
5. **移除分界。** 只允许移除本产品托管且未被宿主实际加载的版本；活动/未知先 pending；目录状态文件损坏或版本未知时不静默重建；generation CAS + OS 文件锁 + 原子写入。
6. **可嵌入控件。** 选择器/文件夹与入口选择/宿主快照/安全应用全部由注入委托或接口提供，不硬编码主区路径；外壳接线交 M。

## 验证证据（实际运行）

工作树内 `tmp/` 为忽略目录，日志已保留。

| 验证 | 命令 | 结果 | 证据 |
|---|---|---|---|
| 标准解决方案构建 | `dotnet build Fufucode.sln -v m` | **0 警告 0 错误**（退出 0） | 终端输出 |
| 独立定向套件 | `dotnet run --project tests/Fufucode.App.Plugins.Tests -c Release --no-build` | **ALL PASS (81)**，退出 0 | `tmp/e3-plugins-service.log` |
| 无头编译绑定/生命周期 | `dotnet run --project tests/Fufucode.App.Plugins.Tests -c Release --no-build -- --ui` | **UI ALL PASS (36)**，退出 0 | `tmp/e3-plugins-ui.log` |
| App 全量自测 | `dotnet run --project src/Fufucode.App -c Release -- --selftest` | **0 项失败**，退出 0 | `tmp/e3-selftest-rerun.log` |

覆盖的真实反例（全部使用真实临时目录与真实文件，不依赖字段自序列化）：

导入与重开读回、同名版本不同内容拒绝覆盖、同版本同摘要幂等、宿主身份未知/版本不匹配/`OMP`·`Codex`·`DSH` 明确拒绝应用且列表标 Blocked、缺描述文件拒绝、入口 `..` 越界与绝对路径与显式入口越界、包内 junction 遍历拒绝、应用经宿主回调、未确认不显示已加载、宿主快照确认后才 Loaded、更新失败保旧选定版本与文件并保留诊断、活动使用只登记 pending 且不动活动目录、宿主切换后按真实快照确认 pending、快照不可用只 pending、活动版本移除只 pending、非活动托管版本可移除、外部源保留、generation CAS 双向拒绝且无副本副作用、目录状态损坏/未来版本不重建不覆盖、**真实文件锁被占用时拒绝写入且状态不变**、模块数据根无 `desktop.db`、源目录全程未被改写。

无头真实窗口覆盖：编译绑定加载、目录/入口选择经注入委托、预览与导入、列表/版本控件反映 VM、应用经宿主回调、失败返回与诊断、移除只限托管非活动、临时移出视觉树不释放不丢状态、320 宽停靠栏下 9 个动作按钮全部可达、重复 `Attach` 幂等、最终 `Detach` 释放且释放后命令空转。

## 未运行验证与原因

- **真实 Pi 实际加载本次导入副本**：未运行。需要 M 在共享入口把 `ILocalPluginHost.SnapshotAsync/ApplyAsync` 接到既有安全重启与真实 Pi 启动路径；本包按任务书不越权接共享入口。当前"已加载"只由注入快照驱动，未声称生产 Pi 已加载。
- **原生 IME/DPI、外壳接线、系统级交互**：未运行，归 M/V。
- **`.pi\extensions` 等现有加载目录的联动**：未触碰，归 M。
- **Pi 入口发现规则的真机核对**：本模块只要求"显式相对入口 + 越界/存在校验"，未复制 Pi 的 `index.ts`/`package.json pi.extensions` 发现逻辑，避免虚构引擎能力；入口可用性由 M 接宿主时按已安装固定源码裁定。
- **OMP/Codex/DSH 适配**：首版明确不支持，未运行任何跨后端加载。
- **模型调用**：本包无模型请求，未使用任何模型 lead 试跑。

## 缺陷与观察（提请 M 登记全局）

1. **E3-OBS-1（非本包引入）**：App 全量自测中 `AutomationControlCreationSelfTest` 偶发失败一次，报文 `Actual creation key did not start command: restructure; ...; focused=False`，属无头焦点时序抖动；同版本立即重跑 **0 项失败**。失败日志 `tmp/e3-selftest-final.log`，通过日志 `tmp/e3-selftest-rerun.log`。该项在 M 的 Automation 所有权范围，本包未修改。
2. **E3-OBS-2**：`SessionLease.Acquire` 在某些文件系统上让底层 `IOException` 直接冒泡（英文原文），本模块已在自己内部统一包装为可解释中文；共享 Core 是否也应收口由 M 决定，本包未改 Core。

## 公共接线契约（交 M）

```csharp
// 1) 构造：根用调用方提供的 AppDataLayout，宿主可先为空。
var service = new LocalPluginService(layout, host);   // layout.Root → plugins/local

// 2) 宿主：M 实现这一个接口即可，身份必须来自真实运行身份。
public interface ILocalPluginHost
{
    HostIdentity Identity { get; }                     // 例 HostIdentity.Pi("1.1.0")；不确定用 HostIdentity.Unknown()
    Task<HostPluginSnapshot> SnapshotAsync(CancellationToken ct = default);   // 真实加载路径；拿不到返回 Success=false
    Task<HostApplyResult> ApplyAsync(string candidateEntryPath, string pluginId, string version,
        CancellationToken ct = default);               // 接既有安全重启；未成功返回 Success=false
}

// 3) 控件：嵌入现有扩展页；选择器可选注入。
var vm = new LocalPluginViewModel(service, picker);
view.Attach(vm);          // 临时移出视觉树不需要 Detach
view.Detach();            // 最终释放

// 4) 安全重启完成后（M 的既有路径），调用一次：
await service.ResolvePendingAsync(ct);   // 只按真实快照登记事实，不执行更新/重启/模型请求
```

- M 还需：宿主安全重启后刷新扩展页，并调用 `ResolvePendingAsync`；把"插件是本地可执行代码、权限由宿主负责"的文案与现有扩展页合并；不把本模块的"已交宿主应用"当作"已加载"。
- 可选：需要并发保护时用 `service.CurrentGeneration()` 读代次，再以 `expectedGeneration` 传入 `ImportAsync`/`ApplyVersionAsync` 做 compare-and-set。

## 交付卡

```
交付卡
任务ID与软件：E3-LOCAL（Fufucode，Avalonia桌面 + Codex执行会话）
工作树绝对路径、分支/HEAD、起点SHA：
  C:\Users\mio\.codex\worktrees\2884\Fufucode；detached HEAD；派工起点 22f6bf4df764721d80dc59d613efad1dac5911c9；
  产品起点 637bd9193df3536ae05ead09f1c094878ea5b89b
本任务提交SHA：
  c53e1d7（模块：领域/服务/可嵌入控件）
  1a669e1（独立套件 + 门禁/回退/刷新/锁错误修正）
  f5421d6（本记录；其后仅有一次 SHA 补录提交，见交付 JSON 的 commits/head）
改动文件、继承用户改动的保护情况：
  新增 src/Fufucode.App/Features/Plugins/{Contracts,PluginDescriptorReader,PluginPackageScanner,
  PluginCatalogState,LocalPluginService,LocalPluginViewModel,LocalPluginSectionView.axaml(.cs),PluginRecordExtensions.cs}
  新增 tests/Fufucode.App.Plugins.Tests/{Fixtures,Program,ServiceTests,UiTests.cs,csproj}
  新增 docs/records/E3-local-plugins-2026-10-13.md
  继承改动：无（开工时工作树干净）；未改 Core/Pi/Hosting/Cli、现有ExtensionsPage/VM、MainWindow/Settings/
  Integration/Styles、AppDataLayout/desktop.db、solution、共享自测注册、包装、权威progress与R1候选。
实际验证命令、结果、证据位置：
  dotnet build Fufucode.sln -v m → 0警告0错误（退出0）
  dotnet run --project tests/Fufucode.App.Plugins.Tests -c Release --no-build → ALL PASS (81)，退出0；tmp/e3-plugins-service.log
  dotnet run --project tests/Fufucode.App.Plugins.Tests -c Release --no-build -- --ui → UI ALL PASS (36)，退出0；tmp/e3-plugins-ui.log
  dotnet run --project src/Fufucode.App -c Release -- --selftest → 0项失败，退出0；tmp/e3-selftest-rerun.log
未运行验证与原因：
  真实Pi实际加载本模块导入副本、宿主安全重启回调、原生IME/DPI、外壳接线（依赖M接共享入口，本包不越权）；
  未做任何模型调用；未声称OMP/Codex/DSH可运行。
公共接线需求、接口/入口/依赖：
  ILocalPluginHost（Identity/SnapshotAsync/ApplyAsync）+ ILocalPluginPicker（目录/入口选择）+ LocalPluginService(AppDataLayout, host)
  + LocalPluginSectionView.Attach(vm) + 安全重启后调用 ResolvePendingAsync。详见本记录"公共接线契约"。
状态：模块交付待接线
原会话是否仍在写代码：否
```

---

# 返工轮（第二次交付）：M 独立审核四项 finding

依据：`docs/records/M-E3-local-review-2026-10-13.md`、`docs/records/E3-local-review-rework-2026-10-13.json`（主仓库只读）。
决定 `rework / not_integrated`，原 L3、原模型 `group/auto-deepseek-v4-1-flash`、原线程原树；未换模型、未新建工作树、未接公共入口。

## 红→绿证据

| 项目 | 红（返工前 HEAD `21eebc5`） | 绿（本轮修复后） |
|---|---|---|
| M 忠实反例（5 条） | `FAIL` 5 / `Review: 5 failed`，M 原日志 `C:\Projects\Fucode\Fufucode\tmp\m-e3-review\results.log`（只读引用，未改动） | 我复制的同一 Program.cs：**5 PASS / Review: 0 failed**，退出 0，日志 `tmp/e3-mirror-green.log` |
| 本模块独立服务套件 | 81 PASS（不含返工反例） | **ALL PASS (125)**，退出 0，日志 `tmp/e3-plugins-service.log` |
| 无头编译绑定/生命周期 | UI ALL PASS (36) | **UI ALL PASS (46)**，退出 0，日志 `tmp/e3-plugins-ui.log` |
| 标准解决方案构建 | 0 警告 0 错误 | **0 警告 0 错误**，退出 0 |

复制说明：M 的 `tmp/m-e3-review/Program.cs` 原样复制到本树 `tmp/e3-rework-mirror/`（断言未改、未放宽），其 `Review.csproj` 指向本树 Release 程序集；M 的日志与断言未被修改。

## 四项修复

1. **E3-VERSION-COMPAT-1（版本级冻结声明）**：`PluginVersionRecord` 新增 `Backend` 与 `SupportedHostVersions`，导入时把该固定版本的声明冻结写进版本记录；`ApplyVersionAsync`/列表/回退一律按**所选版本**的冻结声明判定，插件级字段只作旧数据回退与展示，后续导入不再改写旧版本门禁。旧目录状态（版本级字段为空）自动回退插件级，不需要重建。
2. **E3-PENDING-REMOVE-1（pending 绑定具体目标）**：插件级 `PendingRemove` 布尔替换为 `PendingRemoveVersionId`；`ResolvePendingAsync` 改为**只读核对**：只登记"宿主已实际加载待应用版本"这一条事实，**不删除任何文件**；实际删除只由显式 `RemoveVersionAsync` 执行，且只作用于原目标版本。列表中 `PendingRemoveReady` 表明宿主已确认未使用该目标、可执行移除。
3. **E3-REMOVE-FAILURE-1（移除失败保真）**：删除顺序改为**先删文件、后提交目录状态**（此前是先提交状态、再吞掉删除异常）；不再吞 `IOException`/`UnauthorizedAccessException`，删除失败返回失败并保留记录、文件与诊断（写诊断失败也如实附加到返回消息）。目录状态提交失败时明确报告"文件已删除但状态未提交，重试可收敛"。目录状态不可读/被占用时在删除前就失败，不改动任何内容。仅暂存目录清理仍是尽力而为（不影响所有权事实）。
4. **E3-STALE-APPLY-1（跨 await 操作身份）**：新增持久化 `OperationToken`。接受应用请求时写入新令牌；任何完成提交（成功/失败/异常/取消）都在锁内核对令牌，令牌已被更新的操作替换时返回 `StaleOperation` 且**不写任何状态**——旧结果不得覆盖新 pending/选定/回退目标，也不得污染新诊断。延后（快照未知/活动使用）会保留 pending 目标并释放令牌，使按真实快照确认仍可推进。应用等待期间对同一版本的移除请求返回 `OperationBusy`，不删除候选文件。令牌随目录状态持久化，因此**跨服务实例竞争**同样被判定为过期。

邻接边界（已纳入本套件）：版本声明跨重开保留、待移除目标跨重开保留、移除 v2 不影响同时待移除的 v1、旧失败/旧异常不污染较新成功、取消有界回收自己的 pending、释放锁后重试移除收敛、应用等待期间移除被拒且候选文件保留。

## 本轮未运行验证与原因

- **App 全量自测**：本轮未跑。任务书本轮明确"R1 正在串行包内 UI 稳定性核查，本轮服务测试优先，UI/全量自测需要时先协调排期，不能与其原生验证同时抢窗口"。本模块自己的无头套件（`--ui`，Avalonia Headless，不创建桌面原生窗口）已跑并保存证据；全量自测留待 M 排期复核。
- **上一轮 `focused=False` 偶发失败**：根因仍未确定。本轮不宣称它是非模块引入或只是假抖动；红日志 `tmp/e3-selftest-final.log` 保留，M 审查记录亦保留该观察。
- **真实 Pi 实际加载、宿主安全重启、原生 IME/DPI、外壳接线**：仍归 M，本包未越权接公共入口。
- **"文件已删除但目录状态提交失败"这一具体时序**：代码路径存在且有明确可收敛文案，但未构造出确定性外部注入（删除与提交之间的失败点无法从公开 API 触发），标记为已实现未实测。

## 返工交付卡（第二次）

```
交付卡
任务ID与软件：E3-LOCAL 返工轮（Fufucode，Avalonia桌面 + Codex执行会话）
工作树绝对路径、分支/HEAD、起点SHA：
  C:\Users\mio\.codex\worktrees\2884\Fufucode；detached HEAD；
  原派工起点 22f6bf4df764721d80dc59d613efad1dac5911c9；被审核 HEAD 21eebc564e28f2158a268d55a1ab72ffbecd0ec3
本任务提交SHA：
  c53e1d7（模块）
  1a669e1（独立套件 + 首轮门禁/回退/刷新/锁修正）
  f5421d6（交付记录）
  21eebc5（记录SHA补录；被M审核的HEAD）
  b3cdfa9（返工四项：版本级冻结声明/目标绑定pending/移除失败保真/跨await操作身份 + 邻接反例）
  <本记录返工段提交见交付JSON的commits字段>
改动文件、继承用户改动的保护情况：
  本轮改动：src/Fufucode.App/Features/Plugins/{Contracts,PluginCatalogState,LocalPluginService,LocalPluginViewModel,LocalPluginSectionView.axaml}
  与 tests/Fufucode.App.Plugins.Tests/{Fixtures,ServiceTests,UiTests}.cs
  仍未触碰 Core/Pi/Hosting/Cli、现有 ExtensionsPage/VM、MainWindow/Integration/Settings/Styles、
  AppDataLayout/desktop.db、solution、共享自测注册、包装、权威 progress/decisions、R1 候选与其源；未动 M 的 tmp/m-e3-review 日志与断言。
实际验证命令、结果、证据位置：
  dotnet build Fufucode.sln -v m → 0警告0错误（退出0）
  dotnet run --project tests/Fufucode.App.Plugins.Tests -c Release --no-build → ALL PASS (125)，退出0；tmp/e3-plugins-service.log
  dotnet run --project tests/Fufucode.App.Plugins.Tests -c Release --no-build -- --ui → UI ALL PASS (46)，退出0；tmp/e3-plugins-ui.log
  M 忠实反例原样副本（5 条）→ 5 PASS / 0 failed，退出0；tmp/e3-mirror-green.log（红基线见 M 的 tmp/m-e3-review/results.log）
未运行验证与原因：
  全量 App 自测（R1 原生 UI 稳定性核查期间的窗口协调，本轮服务优先）；真实 Pi 加载/安全重启/原生 IME-DPI/外壳接线（M 所有）；
  "文件已删除但状态提交失败"时序的确定性外部注入（已实现、文案可收敛，未实测）；上一轮 focused=False 根因仍未确定。
公共接线需求、接口/入口/依赖：
  接口未变：ILocalPluginHost（Identity/SnapshotAsync/ApplyAsync）+ ILocalPluginPicker + LocalPluginService(AppDataLayout, host)
  + LocalPluginSectionView.Attach(vm) + 安全重启后调用 ResolvePendingAsync（本轮起该入口只读、不删文件）。
  M 若要让"待移除"落地，需在宿主确认未使用后显式再次调用 RemoveVersionAsync(pluginId, versionId)；界面已把该目标显示为可执行移除。
状态：提交待验收（返工）
原会话是否仍在写代码：否
```

---

# 返工轮（第三次交付）：M 第一修复轮复核的三项相邻不变量

依据：`docs/records/M-E3-local-rework-review-2026-10-13.md`、`docs/records/E3-local-review-rework2-2026-10-13.json`（主仓库只读）。
增量基准 HEAD `200a36a46fdae8958167a9189343f224c7742990`；决定仍为 `rework / not_integrated`，原 L3、原模型、原线程原树，未换模型、未新建工作树。

## 红→绿证据

| 项目 | 红（基准 `200a36a`） | 绿（本轮修复后） |
|---|---|---|
| M 相邻反例（4 条） | `FAIL` 4 / `Adjacent review: 4 failed`，M 原日志 `C:\Projects\Fucode\Fufucode\tmp\m-e3-adjacent\results.log`（只读引用，未改动） | 同一 Program.cs 原样副本：**4 PASS / Adjacent review: 0 failed**，退出 0，日志 `tmp/e3-adjacent-green.log` |
| M 首轮五反例（不改的回归门） | 5 PASS（`rework1-green.log`） | 仍 **5 PASS / Review: 0 failed**，日志 `tmp/e3-mirror-green.log` |
| 本模块独立服务套件 | 125 PASS | **ALL PASS (156)**，退出 0，日志 `tmp/e3-plugins-service.log` |
| 无头编译绑定/生命周期 | UI ALL PASS (46) | **UI ALL PASS (46)**，退出 0，日志 `tmp/e3-plugins-ui.log` |
| 标准解决方案构建 | 0 警告 0 错误 | **0 警告 0 错误**，退出 0 |

复制说明：M 的 `tmp/m-e3-adjacent/Program.cs` 原样复制到本树 `tmp/e3-adjacent-mirror/`（断言未改、未放宽）；原 `tmp/m-e3-review/Program.cs` 仍以 `tmp/e3-rework-mirror/` 复跑。M 的日志与断言未被修改。

## 三项修复

1. **E3-SUPERSEDED-INFLIGHT-1（在途候选的真实资源保护）**：新增持久化 `InFlight`（每条含 VersionId/Token/StartedAtUtc）。调用宿主前登记，回调返回（成功/失败/异常/取消）后按令牌回收。删除路径在**同一个独占写入区**内复核 `InFlight`、`PendingApplyVersionId`、宿主活动报告后才删文件，因此"读后删前"别的实例登记在途的竞态被关掉；跨实例/跨进程、异常与停止未知都靠持久登记保护，而不是只 CAS 最后目录字段。保护不会永久锁死：宿主可在确认无后续副作用后调用 `AbandonInFlightAsync(pluginId, versionId, reason)` 显式对账回收（该入口同时回收指向该候选的令牌与 pending）。竞争场景保留：较新的应用请求照常被接受并生效，只是旧候选在其回调结束前不可删除。
2. **E3-CANCELLED-COMPLETION-1（自己的取消 ≠ 被新操作取代）**：所有完成提交改用 `CancellationToken.None` 记账（调用方取消的是宿主操作，不是本地记账），因此不会再因取消异常而留下永久 `OperationToken`/`PendingApplyVersionId`。分支分诊：
   - 宿主返回成功但调用方已取消 → 记录 `cancelled_unknown` 回执与"结果未知、未自动重发"诊断，清 pending/令牌，**不**宣称成功、**不**伪造选定；
   - 宿主调用抛 `OperationCanceledException` → 同样 `cancelled_unknown`，正确报 `OperationCancelled`（不再误报 `StaleOperation`）；
   - 调用宿主前已取消 → 不调用宿主、`cancelled` 回执；
   - 只有令牌确实已被更新的操作替换时才是 `Superseded`。
   新增持久化 `LastApplyReceipt`（VersionId/Token/Outcome/Message/AtUtc）供宿主只读核对，跨重开保留，不自动重发。
3. **E3-FIXED-CONTENT-1（应用/回退前复核固定摘要与入口）**：新增 `VerifyManagedCopy`：应用与回退在调用宿主之前复核托管副本路径在根内、目录存在且非重解析点、文件数量/大小上限、入口仍可解析、逐文件内容摘要与导入时 `ContentHash` 完全一致。任一不符即拒绝且**零宿主副作用**，并把诊断写入该版本的 `IntegrityError`/`IntegrityCheckedAtUtc`；绝不改写记录里的旧摘要制造通过。恢复导入时字节后可正常应用（不是永久损坏判定）。

邻接补充（已纳入本套件）：在程候选跨实例不可删、回调结束即可显式移除、对账回收需宿主证据且不永久锁死、迟到完成在插件记录已被移除时不抛异常也不复活记录、取消三分支与回执、篡改/入口缺失/回退三条拒绝路径、恢复字节后可应用、回执跨重开保留。

## 本轮未运行验证与原因

- **App 全量自测**：按派工要求让位 R1 的原生包装验收（服务检查优先）；本模块自身无头套件已跑并留证。
- **真实 Pi 实际加载、宿主安全重启接线、现有扩展页接线、原生 IME/DPI**：M 所有，本包仍未越权接公共入口。
- **宿主进程崩溃后真实回收演练**：`AbandonInFlightAsync` 已实现并有测试，但真实崩溃/重启场景需 M 在接宿主时演练。
- 上一轮 `focused=False` 偶发失败根因仍未确定；红日志 `tmp/e3-selftest-final.log` 保留。

## 返工交付卡（第三次）

```
交付卡
任务ID与软件：E3-LOCAL 第二修复轮（Fufucode，Avalonia桌面 + Codex执行会话）
工作树绝对路径、分支/HEAD、起点SHA：
  C:\Users\mio\.codex\worktrees\2884\Fufucode；detached HEAD；
  原派工起点 22f6bf4df764721d80dc59d613efad1dac5911c9；本修复轮增量基准 200a36a46fdae8958167a9189343f224c7742990
本任务提交SHA：
  c53e1d7 / 1a669e1 / f5421d6 / 21eebc5（首轮交付，被第一次审核）
  b3cdfa9 / 200a36a（第一修复轮，被第二次审核）
  245bba9（本轮：在途候选保护、取消/取代分诊、固定摘要复核 + 邻接反例）
  <本记录本轮段落提交见交付JSON的commits字段>
改动文件、继承用户改动的保护情况：
  本轮改动：src/Fufucode.App/Features/Plugins/{Contracts,PluginCatalogState,LocalPluginService,LocalPluginViewModel,LocalPluginSectionView.axaml}
  与 tests/Fufucode.App.Plugins.Tests/ServiceTests.cs（Fixtures.cs 未改）
  仍未触碰 Core/Pi/Hosting/Cli、现有 ExtensionsPage/VM、MainWindow/Integration/Settings/Styles、
  AppDataLayout/desktop.db、solution、共享自测注册、包装、权威 progress/decisions、R1 候选与其源；未动 M 的 tmp 日志与断言。
实际验证命令、结果、证据位置：
  dotnet build Fufucode.sln -v m → 0警告0错误（退出0）
  dotnet run --project tests/Fufucode.App.Plugins.Tests -c Release --no-build → ALL PASS (156)，退出0；tmp/e3-plugins-service.log
  dotnet run --project tests/Fufucode.App.Plugins.Tests -c Release --no-build -- --ui → UI ALL PASS (46)，退出0；tmp/e3-plugins-ui.log
  M 相邻反例原样副本（4 条）→ 4 PASS / 0 failed，退出0；tmp/e3-adjacent-green.log（红基线 tmp/m-e3-adjacent/results.log）
  M 首轮反例原样副本（5 条）→ 仍 5 PASS / 0 failed，退出0；tmp/e3-mirror-green.log
未运行验证与原因：
  全量 App 自测（让位 R1 原生包装验收的串行安排）；真实 Pi 加载/安全重启/页面接线/原生 IME-DPI（M 所有）；
  宿主进程崩溃后的真实对账演练（机制已有测试，真机演练交 M）；focused=False 根因仍未确定。
公共接线需求、接口/入口/依赖：
  ILocalPluginHost / ILocalPluginPicker / LocalPluginService(AppDataLayout, host) / LocalPluginSectionView.Attach(vm) 未变；
  新增一项宿主可选入口：AbandonInFlightAsync(pluginId, versionId, reason) —— 仅当宿主确认该候选不会再加载也不会有后续副作用时调用，
  用于持有者进程异常结束后的显式对账回收；本模块绝不自行解除在途保护。
  语义提醒：应用/回退现在会在调用宿主前复核托管副本的固定摘要与入口，篡改/损坏一律拒绝且无宿主副作用。
状态：提交待验收（第二修复轮）
原会话是否仍在写代码：否
```

---

# 返工轮（第四次交付）：E3-RECEIPT-IDENTITY-1 回执身份缺陷

依据：`docs/records/M-E3-receipt-review-2026-10-13.md`、`docs/records/E3-local-review-rework3-2026-10-13.json`、`tmp/m-e3-receipt/Program.cs`（主仓库只读）。
增量基准 HEAD `ff6bab6a1eeabd838820cbd98c91ab1d4b47fbc2`；决定 `rework / not_integrated`，原 L3、原模型、原线程原树；本轮只修这一个具体缺陷与必要邻接，未扩大公共宿主/页面范围。

## 缺陷与修复

**E3-RECEIPT-IDENTITY-1**：`CompleteAsync` 从可变的 `PendingApplyVersionId`/上一条回执推断回执的 VersionId，导致"旧 v1 失败"被写成"v2 失败"。真实反例复现：Apply1 挂起 → Apply2 成功（回执 v2/token2/confirmed）→ 旧 1 返回失败 → 回执变成 `VersionId=2.0 / Token=token1 / Outcome=failed / Message="actual failure of version 1"`。

修复：

1. `CompleteAsync` 增加**显式的原请求版本参数**（接受请求时的 versionId），并优先采用该操作自己持久化在途登记里的 `VersionId`；两者都不来自当前 pending 或上一条回执。
2. 回执分两处写：`LastApplyReceipt` 只在"令牌仍是当前令牌"时更新；迟到/被取代的结果只写入新的有界历史 `RecentReceipts`（最多 20 条，按 token 去重），因此**不会顶掉已确认的新回执**，也不会把自己的结果写成别人的版本。
3. 历史在"插件记录已不存在"时不复活记录（仍安全返回未提交，不抛异常）。
4. 取消/异常/完整性拒绝/延后/对账回收（`AbandonInFlightAsync`）各分支都传自己的原版本；对账回收仍按显式 versionId 匹配在途登记，不从可变当前 pending 猜归属。

## 红→绿证据

| 项目 | 红（基准 `ff6bab6`） | 绿（本轮修复后） |
|---|---|---|
| M 回执身份反例 | `FAIL E3-RECEIPT-IDENTITY`，`{"before":{v2/token2/confirmed},"after":{v2/token1/failed}}`，日志 `C:\Projects\Fucode\Fufucode\tmp\m-e3-receipt\results.log`（只读引用，未改动） | 同一 Program.cs 原样副本：**PASS**，`after` 保持 `v2/token2/confirmed`（confirmed 新回执未被顶掉；迟到 v1 结果进原身份历史），日志 `tmp/e3-receipt-green.log` |
| M 首轮五反例 | 5 PASS | 仍 **5 PASS / Review: 0 failed**（`tmp/e3-mirror-green.log`） |
| M 相邻四反例 | 4 PASS | 仍 **4 PASS / Adjacent review: 0 failed**（`tmp/e3-adjacent-green.log`） |
| 本模块独立服务套件 | 156 PASS | **ALL PASS (166)**，退出 0（`tmp/e3-plugins-service.log`） |
| 无头编译绑定/生命周期 | UI ALL PASS (46) | **UI ALL PASS (46)**，退出 0（`tmp/e3-plugins-ui.log`） |
| 标准解决方案构建 | 0 警告 0 错误 | **0 警告 0 错误**，退出 0 |

本轮新增严格邻接断言（已纳入独立套件）：迟到失败/迟到成功都按 v1 原身份入历史且最新回执保持 v2/confirmed；历史中不存在"旧令牌配新版本"的污染；取消分支回执身份为原请求版本且为 `cancelled_unknown`；完整性拒绝回执绑定被拒绝的原版本；对账回收后的迟到结果仍按原身份入历史、最新回执不携带其他版本身份。

## 本轮未运行验证与原因

- **App 全量自测 / 原生 UI**：按派工要求让位 M 在主区做公共工作台只读刷新与焦点问题的原生验证（服务测试独立进行，不抢窗口）。
- **真实 Pi 加载、宿主安全重启接线、现有扩展页接线、原生 IME/DPI**：M 所有；本包未越权接公共入口。
- **回执历史的跨进程并发清理**：已按 token 去重并有 20 条上限，未做长时间多进程压力验证。
- 上一轮 `focused=False` 偶发失败根因仍未确定；红日志 `tmp/e3-selftest-final.log` 保留。

## 返工交付卡（第四次）

```
交付卡
任务ID与软件：E3-LOCAL 第三修复轮（Fufucode，Avalonia桌面 + Codex执行会话）
工作树绝对路径、分支/HEAD、起点SHA：
  C:\Users\mio\.codex\worktrees\2884\Fufucode；detached HEAD；
  原派工起点 22f6bf4df764721d80dc59d613efad1dac5911c9；本轮增量基准 ff6bab6a1eeabd838820cbd98c91ab1d4b47fbc2
本任务提交SHA：
  c53e1d7 / 1a669e1 / f5421d6 / 21eebc5（首轮）
  b3cdfa9 / 200a36a（第一修复轮）
  245bba9 / ff6bab6（第二修复轮）
  b47e8d9fe09f9c2b40462cda25ef24cf3564e584（本轮：回执身份绑定原请求版本与自身令牌 + 原身份历史）
  <本记录本轮段落提交与本卡补正提交见交付JSON的commits字段，不在文档内自引用>
改动文件、继承用户改动的保护情况：
  本轮改动：src/Fufucode.App/Features/Plugins/{LocalPluginService.cs,PluginCatalogState.cs}
  与 tests/Fufucode.App.Plugins.Tests/ServiceTests.cs（共 3 个文件）
  仍未触碰 Core/Pi/Hosting/Cli、现有 ExtensionsPage/VM、MainWindow/Integration/Settings/Styles、
  AppDataLayout/desktop.db、solution、共享自测注册、包装、权威 progress/decisions、R1 候选与其源；未动 M 的 tmp 日志与断言。
实际验证命令、结果、证据位置：
  dotnet build Fufucode.sln -v m → 0警告0错误（退出0）
  dotnet run --project tests/Fufucode.App.Plugins.Tests -c Release --no-build → ALL PASS (166)，退出0；tmp/e3-plugins-service.log
  dotnet run --project tests/Fufucode.App.Plugins.Tests -c Release --no-build -- --ui → UI ALL PASS (46)，退出0；tmp/e3-plugins-ui.log
  M 回执身份反例原样副本 → PASS，退出0；tmp/e3-receipt-green.log（红基线 tmp/m-e3-receipt/results.log）
  M 首轮 5 反例 → 仍 0 failed；tmp/e3-mirror-green.log
  M 相邻 4 反例 → 仍 0 failed；tmp/e3-adjacent-green.log
未运行验证与原因：
  App 全量自测与原生 UI（让位 M 主区焦点问题原生验证的串行安排）；真实 Pi 加载/安全重启/页面接线/原生 IME-DPI（M 所有）；
  回执历史多进程长压；focused=False 根因仍未确定。
公共接线需求、接口/入口/依赖：
  ILocalPluginHost / ILocalPluginPicker / LocalPluginService / LocalPluginSectionView 接口未变；
  AbandonInFlightAsync(pluginId, versionId, reason) 仍为宿主显式对账入口。
  数据新增：PluginRecord.RecentReceipts（有界原身份历史）。宿主可只读核对 LastApplyReceipt（最新确认结果）与 RecentReceipts（含迟到/取消/异常的原身份记录）。
  语义提醒：应用/回退前仍会复核托管副本固定摘要与入口；ResolvePendingAsync 仍是只读核对入口。
状态：提交待验收（第三修复轮）
原会话是否仍在写代码：否
```

---

## 元数据补正（2026-10-13，纯文档）

- 第四次交付 JSON 卡里 `b47e8d9eb0f0b6b1b8bd3fa1d4dfa1a24bb9c1a3` 是**错误的完整 SHA（该对象不存在）**；经原树 `git log --format="%H %s"` 实查，该提交的真实完整 SHA 是 `b47e8d9fe09f9c2b40462cda25ef24cf3564e584`。
- 本记录正文此前只使用短前缀 `b47e8d9`，该前缀真实存在且指向同一提交；本次把它补成完整 SHA，并新增本补正段说明来源。
- 本补正**只改文档**：未改实现、未改测试、未重跑任何验证、未同步 main、未 reset/push/删树，也未开始新任务。此前所有红/绿证据日志与继承状态保持不变。
- 当前真实 HEAD 由 `git rev-parse HEAD` 查询后写入本次交付 JSON；补正提交自身的完整 SHA 同样只从 `git log` 实查生成，不在文档内自引用。
