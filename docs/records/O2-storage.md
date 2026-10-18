# O2 存储管理（M2）

日期：2026-10-10。任务：O2 → OMP，存储管理（L2 常规 → 修复轮 L3 较难，共三轮）。软件：OMP 执行会话（原独立工作树）。
任务书：`docs/tasks/management-surfaces.md` M2；派工：`docs/tasks/launch-prompts.md` O2；边界：`docs/tasks/execution-contract.md`。
M 复核：`docs/records/M-O2-review-2026-10-10.md`（R-030 P1 / R-031 P2 / 缓存分类）、
`docs/records/M-O2-repair-review-2026-10-10.md`（R-030 仍 open：快照条目未查完整祖先链）。
状态见文末交付卡：**模块交付，待 M 公共接线与原生验收。**

## 1. 任务与范围

按 M2：展示应用数据各目录的实际占用（计量可取消、不阻塞输入）；提供打开目录、保留策略与明确范围的清理，
预览实际目标与预计大小、结束记录实际结果；默认仅清理可重建缓存与过期日志；活跃写入数据不移动；
第一阶段不做数据根迁移；浏览器数据清理提示登录状态影响，且不把模型认证与浏览器 cookie 混在一起管理。

修复轮追加要求：
- 第二轮（M-O2-review）：发现、预览与执行都不得跟随 junction/符号链接删除目标；执行时重新验证日志保留期限与预览是否过时；站点离线存储不默认清理。
- 第三轮（M-O2-repair-review）：每个快照文件/目录在实际操作前检查**从可信边界到该对象的完整祖先链**；
  当前重扫已排除或不可安全到达的旧快照路径不得继续删除；**无法读取属性不能当作安全**；目录删除同样不得穿越链接。

允许范围：`src/Fufucode.App/Features/Storage`、独立测试与本记录。共享外壳（`MainWindow*`、`SettingsPage*`、
`WorkbenchPageRegistry`、`AppOptions`、组合根、全局 `failure-registry.md`）本任务**未改**，接线交 M。

## 2. 开工身份卡（事实）

| 项目 | 事实 |
|---|---|
| 工作树 | `C:\Users\mio\.omp\wt\wt-20261010-072036-c2a5227`（独立 worktree，三轮沿用，未新建） |
| 分支 / HEAD | `wt/20261010-072036`（非 detached）；起点 `8cf3921` |
| 起点与 main 的关系 | `8cf3921` 是 main 当前 `a411415` 的父提交；main 只多 M 自己的工作树恢复记录（3 个文档），**代码基线一致**，按纪律未整合 main |
| 继承改动 | 无：工作树开工时 `git status --porcelain` 为空；后续轮次只在本任务提交上追加 |
| 依赖 | 仅 G0；G0 已交付（`docs/records/G0-roadmap-2026-10-10.md`） |

## 3. 核实的现状（决定了设计）

- `AppDataLayout` 定义 `Root` 与 `runtime` / `pi-agent` / `sessions` / `desktop.db` / `logs` / `exports`；
  浏览器 profile 落在 `Root/webview`（`BrowserUserDataLayout`），资料库落在 `Root/library`（`LibraryService`）。
- 设置页「关于」区只列出三个路径 + 打开按钮，全仓没有占用计量、保留策略或清理入口 → O2 建立这一层。
- M 两轮独立诊断（直接编译真实 Storage 源文件）复现：清理跟随 junction 删除数据根外用户文件（R-030）、
  旧预览删除已重新写入的日志（R-031）、以及修复后仍存在的「目标本身不是链接但预览后子目录被替换为链接」缺口。

## 4. 交付内容

`src/Fufucode.App/Features/Storage/`（**9 个源文件**，本任务全部新代码）：

| 文件 | 职责 |
|---|---|
| `StorageModels.cs` | 区域/清理范围/目标快照/跳过项/结果模型；跳过类别含链接、越界、缺失、**属性不可读**、预览后变化、不再过期 |
| `StorageCatalog.cs` | 区域清单；可重建缓存名集合与站点数据名集合（含 Service Worker 离线存储与 CacheStorage） |
| `StoragePathGuard.cs` | 重解析点判定与**路径安全检查**：`CheckElement`/`CheckChain`/`ChainGuard`，属性读不出来一律按不安全处理 |
| `StorageUsageMeter.cs` | 计量：只读元数据、不跟随重解析点、不可读条目计入 `SkippedEntries`、按条目检查取消、按区域上报进度 |
| `StorageRetentionPolicy.cs` | 日志保留天数（1–365 夹紧）+ 过期判定 + 说明文本 |
| `StorageFormat.cs` | 占用大小统一显示口径 |
| `StorageCleaner.cs` | 预览（快照 + 跳过清单 + 提示）与执行：链接不跟随、**逐条复核完整祖先链**、目录只删预览时存在的条目、文件执行前重新核对身份与保留期限、逐项失败容忍、越界拒绝、不搬迁数据 |
| `StorageManagerViewModel.cs` | 计量/取消/预览/清理/取消清理/打开目录、状态与结果文本、跳过清单、保留天数联动、`IDisposable` 取消在途操作 |
| `StoragePage.cs` | 独立管理页：区域行、清理范围勾选、预览目标、**跳过（未删除）**、结果与失败明细、站点数据确认行 |

## 5. 计量口径、路径安全规则与保护边界

1. **计量口径可核对**：目标大小 = 其下所有文件长度之和（元数据读取），与测试的独立记账逐项相等；
   `SkippedEntries` 单独计数；区域互不重叠，求和 = 数据根实际占用。
2. **路径安全（发现/预览/执行同一套）**：任何 junction、目录符号链接或文件符号链接都不跟随；
   既不进入链接目标，也不把链接当作清理目标，更不删除链接目标本身。判定按路径逐级读取属性，
   字符串前缀不作为实际落点证明；**属性读不出来（权限、路径长度等）按不安全处理**，绝不当作可删除。
3. **完整祖先链**：执行阶段对每个快照文件与快照目录，用带记忆的 `ChainGuard` 复核「从区域根到该对象」的整条链；
   任一级是链接或不可读即跳过并报告。目标目录本身不是链接、但预览后其**子目录（含两级嵌套的中间目录）**
   被替换为链接时同样拒绝——旧快照路径不再被删除。
4. **删除范围与预览一致**：目录目标只删预览时存在的条目，且该条目必须在执行前的重扫中**仍可安全到达**
   （当前扫描已排除经由链接的路径）；预览后新增的文件/目录不纳入并如实报告「如需一并清理请重新预览」。
5. **文件目标执行前重新验证**：长度或最后写入时间变化、或（日志范围）执行时已不再超过保留期限时，
   不按旧预览删除并报告原因。
6. **可取消**：计量与清理都接受取消；取消的计量不发布部分结果；取消的清理返回结果并如实标注 `Canceled`、
   未处理目标数；取消发生在目标处理中途时保留已完成的部分，不抛异常给调用方。
7. **默认范围固定**：`DefaultScopes` = 可重建缓存 + 过期日志。浏览器登录/站点数据（cookie、登录态、历史、
   站点存储、Service Worker 离线存储与 CacheStorage）默认关闭，需显式勾选且页面要求二次确认。
8. **保护范围永不进入目标**：sessions、pi-agent（模型认证）、runtime、exports、library、desktop.db 与配置，
   以及数据根外的项目源码/工作树全部不可清理；另有区域根归属校验。
9. **活跃/被占用文件**：记录失败原因并继续其余目标；被占用文件与含它的目录原样保留。
10. **登录数据与模型认证分开**；**不做数据根迁移**（只有删除明确目标，没有移动/复制/重定向数据的路径）。

## 6. 实际验证

命令与结果（本工作树，2026-10-10）：

```
dotnet build src/Fufucode.App/Fufucode.App.csproj -v quiet   → 4 projects, 0 errors, 0 warnings
dotnet build Fufucode.sln -v quiet                           → 10 projects, 0 errors, 0 warnings
dotnet build tests/Fufucode.App.Storage.Tests -t:Rebuild --no-incremental → 5 projects, 0 errors, 0 warnings
dotnet run --project tests/Fufucode.App.Storage.Tests       → 全部通过：212 项断言
dotnet run --project src/Fufucode.App -- --selftest         → == 自测结束：0 项失败 ==
```

数据根：`tmp/o2-storage-tests/<时间戳>`（`.gitignore` 已忽略 `tmp/`），全程**不触碰**真实 `%LocalAppData%\Fufucode`；
无模型调用（不需要模型参数，不新增收费调用）。

定向测试 `tests/Fufucode.App.Storage.Tests`（独立入口，未注册到全量自测）覆盖：

1. 计量口径：整根合计/各区域/文件数与独立记账逐项相等；根区域不与子目录重复计数；不存在路径如实报告；
2. 预览：默认范围只含缓存与过期日志；未过期日志、日志目录中的非日志文件、未选择的站点数据、
   会话/认证/资料/运行时/导出都不在目标内；跳过清单带原因且无重复项；
3. 默认清理实际变化：删除量与预览一致，占用 23100 → 6204 字节有记录，缓存目录保留且已清空，
   未过期日志、站点数据与 Service Worker 离线存储保留；会话/认证/运行时/导出/资料与配置**逐字节不变**；
4. 活跃文件失败：独占句柄占用一个缓存文件后清理仍完成，失败项带「占用」原因，被占用文件内容未变，
   删除量只统计真正删除的字节，保护范围不受影响；
5. 站点数据范围：显式选择后 cookie/历史/站点存储/Service Worker 离线存储成为目标，profile 配置不是目标；
6. 保留策略：14/60 天下目标集合正确；天数越界被夹紧；
7. 取消：计量可取消且不返回部分结果；清理可取消且如实标记未完成、剩余目标数正确、已完成目标确实清空；
8. 链接保护（**R-030**）：缓存内 junction、缓存名目录本身是 junction、祖先链接、自引用链接全部不跟随；
   外部哨兵与链接目标内容/哈希不变、链接本身未被删除；预览后把**目标目录**替换成 junction 后执行旧预览 → 拒绝；
   **预览后把目标内的子目录替换成 junction（单级与两级嵌套父目录）→ 外部同名哨兵内容/哈希不变、
   跳过项只涉及缓存内路径**；发现与清理全程不循环（实测 10–23 ms）；文件符号链接本机不可用，如实记为 SKIP；
9. 执行前重新验证（**R-031**）：预览后保留期提高到 60 天 → 30 天前日志保留并报告「不再过期」；
   预览后重写的日志内容与哈希不变并报告「已变化」；预览后新增的过期日志与缓存文件都不进入旧预览删除范围；
   预览后消失的文件不算失败、如实报告缺失；
10. 缓存分类：`CacheStorage` / `Service Worker` 不在默认可重建缓存集合内、归入站点数据；默认清理不动离线存储；
11. 视图模型与无头页面：默认范围、计量结果发布与取消、预览失效规则、结果文本含前后占用、保留天数夹紧、
    打开目录回调、全新安装空数据根；页面渲染按钮/区域行/真实路径/计量中禁用/保留天数双向绑定/
    预览摘要与目标行/**跳过（未删除）分组与原因**、站点数据勾选写回与确认门禁、结果分组、
    页面执行清理后链接目标文件仍在。

### 6.1 忠实回退（证明断言覆盖缺陷本身）

按 O1 的做法，把被修逻辑改回旧实现（保留新 API 以不掩盖），确认断言确实变红：

| 轮次 | 回退内容 | 结果 |
|---|---|---|
| 第二轮 | 去掉链接守卫（发现跳过、快照跳过链接、执行前路径链检查） | 首次失败出现在链接组：`FAIL 链接内容不参与预览计量（1265674）`（旧实现跟随链接与自引用循环，直到路径长度异常才停；85 条通过后终止） |
| 第二轮 | 去掉执行前重校验（长度/最后写入时间/保留期限）并把目录改回「删除当前全部文件」 | 首次失败出现在过时组：`FAIL 执行时不再过期的日志被保留（未按旧预览删除）`（113 条通过后终止） |
| 第三轮 | 快照条目改回「只检查条目自身」（不做祖先链、不要求当前重扫可达） | 首次失败即出现在新反例：`FAIL 嵌套替换：外部同名文件内容与哈希不变`（外部 victim.txt 被删除；88 条通过后终止） |

数据根外的哨兵与外部链接目标在断言里同时核对文件大小与 SHA-256，不是只看「文件还在」。

### 6.2 自测中发现并修复的模块内缺陷

| 编号 | 根因 | 修复 | 忠实回归 |
|---|---|---|---|
| O2-L1 | 清理取消时把「刚开始就被取消的目标」记为已处理，剩余目标数报 0 | 只统计**完整处理完**的目标（`completedTargets`） | 「取消后剩余目标如实计数」等断言覆盖 |
| O2-L2 | 目录目标改为先取快照后，取消信号在目标处理中途到达会从扫描抛出 `OperationCanceledException`，整个结果丢失 | 取消改为在目标处理边界捕获：已完成删除保留，结果标 `Canceled` 并给出未处理目标数 | 「清理取消被如实标记为未完成」等断言在本轮重构后确实变红，修好后恢复 |
| O2-L3 | 第三轮重写预览阶段扫描时丢掉了「目录内链接逐项记录」（扫描的 Link 分支只收集 links 列表就 continue），目录目标内部的链接不再进入跳过清单 | 扫描的两个 Link 分支（子目录/文件）直接记录跳过项；预览改为单次扫描，避免重复条目 | 第二轮既有断言 `FAIL 缓存内链接在预览中如实报告原因` 确实变红，修复后恢复；新增「预览跳过清单没有重复条目」断言 |

（不占用全局编号：R-026/027/028 由 O1 使用、R-030/R-031 由 M 登记，本会话不新增全局条目，`failure-registry.md` 未改动。）

## 7. 未运行 / 未验证 / 待接线

- **未接入共享外壳**：页面未注册到 `WorkbenchPageRegistry`/设置导航，未在真实产品构建里打开过。
- **原生交互未验证**：指针、DPI、主题切换、窗口缩放与真实输入法行为需接线后由 V 项 computer-use 验收。
- **真实 WebView2 占用未验证**：被占用文件用独占句柄模拟；真实进程锁与实际 `EBWebView` 目录布局未验证
  （缓存/站点数据目录名集合按 Chromium 常见布局，接线后应复核）。
- **文件符号链接未验证**：本机无 Developer Mode/权限，如实记为 SKIP；junction 与目录替换路径已实测。
- **属性不可读（ACL 拒绝）未实测**：实现按 fail-closed 处理（`StoragePathSafety.Unreadable`），
  但未在夹具上修改权限去制造拒绝读；已覆盖的替代情形是「条目缺失/目录被替换」路径。
- **未在真实数据根清理**；**网站登录态与离线内容影响未实测**。
- 未修改 `reference/`；未 push、未整合 main、未清理工作树。

## 8. 公共接线需求（交 M）

1. 组合根（`MainWindow`，与 `ui.json`/资料库同一份数据根）：
   ```csharp
   var root = Program.SelfTestDataRoot ?? _options.CreateAppDataLayout().Root;
   var layout = new AppDataLayout(root);
   var catalog = new StorageCatalog(layout, Path.Combine(root, "webview"), Path.Combine(root, "library"));
   var viewModel = new StorageManagerViewModel(catalog, logRetentionDays: 14, openFolder: OpenFolderAsync);
   ```
   `webview`/`library` 必须与 `BrowserUserDataLayout.Resolve(...)`、`LibraryService` 同源。
2. 页面入口（二选一，不要两处同时接）：
   - 工作台页（与 `MainWindow.Workspace.cs:68-78` 的 browser/library 同形）：
     ```csharp
     _workbenchPages.RegisterOptional("storage", "存储", "Archive", "builtin.storage",
         () => new StoragePage(new StorageManagerViewModel(catalog, 14, OpenFolderAsync)),
         control => ((StoragePage)control).ViewModel.Dispose());
     _storageModule = new("builtin.storage", "存储", SetStorageModuleEnabled);
     ```
     并同步 `BuiltInModuleViewModel` 列表与 `WorkbenchModulePreferences` 启用位（属 M 的共享外壳）。
   - 或设置页子页：`SettingsPageViewModel` 增加入口 + `WorkbenchSectionsView` 分支承载 `StoragePage`。
3. `openFolder` 复用 `MainWindow.OpenFolderAsync`；不注入时页面「打开」按钮禁用（已测）。
4. 页面销毁时调用 `StoragePage.ViewModel.Dispose()`；页面只用 Avalonia 基础控件与 `SettingsLayout` 既有类名，不需要改 `Styles`。
5. 自测注册（可选）：加 `--storage-only` 频道或把 `tests/Fufucode.App.Storage.Tests` 纳入 CI；本任务未改 `SelfTestRunner`。

## 9. 状态

模块交付（R-030 含残留缺口、R-031、缓存分类均已修复），待 M 公共接线与原生验收。本地定向 212 项断言通过，
App/solution 构建 0 警告 0 错误，全量 `--selftest` 0 项失败；真实数据根未触碰。

```text
交付卡
任务ID与软件：O2（存储管理，L2 实现 + L3 链接/祖先链/过时边界修复）· OMP 执行会话
工作树绝对路径、分支/HEAD、起点SHA：
  C:\Users\mio\.omp\wt\wt-20261010-072036-c2a5227
  分支 wt/20261010-072036（非 detached）；起点 8cf3921（main 代码基线一致，未整合 main）
本任务提交SHA（多个提交注明范围）：
  b94e0fb / 1a49e47 / c988980 / 55cea62 / 3982abf（首轮：模块、测试补充、记录、SHA 补录）
  be708ac（第二轮：链接不跟随 + 执行前重校验 + 缓存分类）
  3940a90 / 7012b39 / 282bd8c（第二轮记录、SHA 与清单补录）
  fc95202（第三轮：快照条目逐级祖先链 + fail-closed 属性读取 + 新反例）
  12ec793（O2-L3 修复：目录内链接在预览中逐项记录、单次扫描无重复项）
  2f700bd / 5f0d4da 及其后一条仅补录本清单、不改变交付内容的提交（第三轮记录与 SHA 补录）
改动文件、继承用户改动的保护情况：
  src/Fufucode.App/Features/Storage/ 共 9 个源文件；tests/Fufucode.App.Storage.Tests 3 文件；docs/records/O2-storage.md
  未改 MainWindow/Settings/组合根/Styles/SelfTestRunner/全局 failure-registry；继承改动无；未 push、未整合 main、未清理工作树
实际验证命令、结果、证据位置：
  dotnet build Fufucode.sln -v quiet → 10 projects, 0 errors, 0 warnings
  dotnet run --project tests/Fufucode.App.Storage.Tests → 全部通过：212 项断言
  dotnet run --project src/Fufucode.App -- --selftest → == 自测结束：0 项失败 ==
  忠实回退三轮证据（含本轮「只查条目自身」→ 88 条后失败于嵌套替换反例）见 §6.1
未运行验证与原因：
  真实外壳内启动该页、原生指针/DPI/主题/输入法（需接线后 V 项）；真实 WebView2 进程锁与 profile 布局；
  文件符号链接与 ACL 拒绝读（本机无权限，如实 SKIP）；真实数据根清理与网站登录态/离线内容前后对比
公共接线需求、接口/入口/依赖：
  见 §8：StorageCatalog(root, root/webview, root/library) + StorageManagerViewModel(catalog, 14, OpenFolderAsync)；
  页面注册（"storage"/builtin.storage 或设置子页，二选一）；销毁时 Dispose 视图模型；可选 --storage-only 频道
状态：模块交付待接线（R-030 残留缺口已修复，待 M 复核）
原会话是否仍在写代码：否
```
