# O3 资料库（N2）任务记录

日期：2026-10-10。执行会话：OMP 独立工作树 `wt-20261010-055801`（分支 `wt/20261010-055801`），起点 SHA `83045c9`。
任务书：`docs/tasks/new-product-capabilities.md` N2；派工：`docs/tasks/launch-prompts.md` O3；边界：`docs/tasks/execution-contract.md`。
修复轮：M 复核记录 `docs/records/M-O3-review-2026-10-10.md`（R-023/R-024/R-025，均 `open` → 本轮修复，待 M 复核）。

## 1. 范围与边界

- 目标：收藏（引用/副本）、来源与版本、标签、预览、全文检索、导入导出、显式聊天引用。
- 首版本地：不联网抓取网页、不做云同步、不做付费嵌入、不默认上传整个库。
- 只写 `src/Fufucode.App/Features/Library/**`、独立测试与本文档；**未修改**任何既有文件（`desktop.db`/`DesktopMetadataStore`、`AppDataLayout`、`MainWindow*`、`SettingsPage*`、`App` 组合根均未触碰）。
- 数据落点：`<AppDataLayout.Root>/library/library.db` 与 `<...>/library/copies/<entryId>/v<n>/<文件名>`，与 `desktop.db` 完全分离。
- 依赖：G0 已满足（独立工作树）。聊天真实接线需 G1，本轮只交付引用载荷契约。

## 2. 实现（src/Fufucode.App/Features/Library）

| 文件 | 职责 |
|---|---|
| `LibraryModels.cs` | 条目/版本/导入结果/检索命中/来源状态/引用载荷等记录与枚举 |
| `LibraryTokens.cs` | 分词：ASCII 单词小写化 + 汉字单字/相邻双字 bigram（不依赖 FTS5 与外部分词库，中文检索可命中） |
| `LibraryTextExtractor.cs` | 文本提取：BOM → 严格 UTF-8 → GB18030 回退；二进制/不支持格式返回 Unsupported 并给原因；超限截断如实标注 |
| `LibraryStore.cs` | 自有 SQLite（`entries`/`entry_tags`/`entry_text`/`index_tokens`/`entry_versions`），`user_version=1`，库内只记副本相对路径 |
| `LibraryService.cs` | 导入（文件/网页/文本片段）、重复检测（内容 SHA-256）、来源检查、版本接受、标签、检索、索引重建、导出/再导入、删除、引用载荷 |
| `LibraryViewModel.cs` | 页面视图模型：列表/检索/标签/预览/版本/导入导出/两段式删除/引用生成；`ILibraryFilePicker` 由宿主注入 |
| `LibraryPageView.axaml(.cs)` | 自包含页面视图；自带文件/目录选择器与剪贴板复制，未接共享外壳 |

关键行为：

- **引用 vs 副本**：引用只记来源路径+版本；副本复制内容到库内，源删除后仍可读。界面用 `ImportModeHint` 明示差异。
- **来源与版本**：`RefreshStatusAsync(acceptChange:false)` 只标记 `Missing`/`Changed`，**不覆盖已记录版本**；`acceptChange:true` 才追加 `v<n>`（副本模式保存新快照）并重索引。哈希未变但时间戳变化时判定为“内容一致”，不误报变化。
- **失败保留原件**：提取失败只清索引并记原因，文件（源/副本）不动。
- **删除语义**：默认删除条目+库内副本，**绝不删除用户源文件**（测试断言覆盖）。
- **检索**：`index_tokens` 倒排 + 命中数排序 + 标签过滤；查询与文档同一套分词保证 token 对齐。
- **大资料不阻塞**：导入/索引在 `Task.Run`/`async` 上执行，VM 用 `IndexPendingAsync` 后台补索引并上报进度。
- **文本条目保真**：`SessionSnippet`/`Text` 只给内容文件路径时会按文本原样读回；`Text` 与可读路径都缺失（或为空）时导入直接失败，不生成空文本条目。导出/再导入后原文、标签、来源大小与内容哈希一致。
- **页面临时脱离**：视图在工作台停靠换栏时会被移出视觉树再放回，这**不**释放视图模型；只有宿主调用 `LibraryPageView.Detach()` 才取消订阅并释放。同一视图模型实例重复 `Attach` 幂等，不重复订阅、不重复初始化；已释放实例重新挂载会抛 `ObjectDisposedException`。

## 3. 验证（实际运行）

命令与结果（本工作树，2026-10-10）：

```
dotnet build src/Fufucode.App/Fufucode.App.csproj            → 4 projects, 0 errors, 0 warnings
dotnet build Fufucode.sln                                    → 10 projects, 0 errors, 0 warnings
dotnet build tests/Fufucode.App.Library.Tests -t:Rebuild --no-incremental
                                                             → 5 projects, 0 errors, 0 warnings（清理增量缓存后）
dotnet run --project tests/Fufucode.App.Library.Tests        → 全部通过：130 项断言（含 31 项无头视图断言）
dotnet run --project src/Fufucode.App -- --selftest          → 自测结束：0 项失败（既有 mock/UI 段无回归）

定向测试 `tests/Fufucode.App.Library.Tests`（独立入口，未注册到全量自测，注册属 Codex 集成范围）覆盖验收点：

1. 中文文件名/中文目录导入、标题、标签、预览；
2. 中文全文检索命中与片段、标签过滤；
3. 同名文件（不同目录、不同内容）两条独立条目且来源可区分；
4. 重复导入按内容哈希识别为 `Duplicate` 且不新增条目；
5. 引用模式源移动/删除 → `Missing` 且如实说明；恢复后回 `Ok`；副本模式源删除后内容仍可读；
6. 内容变化：先标记 `Changed` 不覆盖旧版本，接受后追加 `v2` 快照、重索引，新内容可检索、旧 token 移除；
7. 索引重建覆盖全部条目、无残留待索引；
8. 仅链接网页条目标记未索引并说明“未抓取”，引用载荷如实说明无内容；
9. 会话片段导入并索引；
10. 二进制/不支持格式不索引但保留副本；
11. GB18030 文本解码成功并标注编码回退；
12. 引用载荷含标题/来源/版本哈希，片段来自真实内容；
13. 批量导出（manifest + 内容）与再导入、重复导入识别；
14. 删除条目不动用户源文件、清理库内副本；
15. 重开服务后条目/索引/标签一致；
16. 大文件（18 MB UTF-8）：超限截断如实标注、截断边界解码正确、内容仍可检索；
17. 无头视图（真实编译绑定）：列表项数量、选中、详情标题、预览内容、标签筛选、引用生成与复制请求；
18. 文本条目保真：`SessionSnippet`/`Text` 导出再导入原文/中文/标签/大小/哈希一致、可检索、可生成引用、重复导入识别；
19. 缺失内容：无 `Text`、空 `Text`、资料包内容文件缺失三种情况均报失败且不新增条目；
20. 页面生命周期（真实无头 Window）：临时移出视觉树不销毁视图模型、重挂后刷新/检索/引用仍可用、
    重复 `Attach` 幂等且无残留订阅、最终 `Detach` 释放视图模型。

## 3.1 M 复核后的修复（R-023 / R-024 / R-025）

根因、改动与证据：

| 编号 | 根因 | 改动 | 证据（本轮实际运行） |
|---|---|---|---|
| R-023 | `ImportBundleAsync` 对文本条目只传 `Path`，而 `ImportAsync` 把 `SessionSnippet`/`Text` 路由到只读 `Text` 的内联分支 → 静默写入空文本并报 `Imported=1` | 内联分支在 `Text` 缺失时按文本读回内容文件；`Text` 与可读路径都缺（或为空）时返回 `Failed`；新增 15 项保真断言与 5 项缺失内容断言 | `SessionSnippet 再导入保留原文与中文`、`内容哈希一致`、`内容缺失不生成空文本成功条目` 等全部通过 |
| R-024 | `OnDetachedFromVisualTree` 直接释放视图模型，工作台停靠换栏的临时脱离会销毁仍在使用的实例，重挂后 `ReloadAsync` 因 `_disposed` 直接返回 | 临时脱离不再释放；释放只发生在宿主 `Detach()`；同一实例重复 `Attach` 幂等（不重复订阅/初始化）；已释放实例重新挂载抛 `ObjectDisposedException`；新增 `IsDisposed` 只读状态 | `临时移出视觉树不销毁视图模型`、`重挂后刷新看到新增条目`、`重挂后检索仍可用`、`Detach 后没有残留订阅`（剪贴板哨兵）全部通过 |
| R-025 | 测试工程用 `MSBuildWarningsAsMessages` 降级 MSB3277，未真正剥离 WebView2 WPF/WinForms 传递引用；剥离项标识实际为 `buildTransitive\..\\lib_manual\...`，路径写法少一个反斜杠时 `Reference Remove` 不匹配 | 移除警告降级设置，按 App 既有 `StripUnusedWebView2ManagedRefs` 用同一路径写法真正剥离引用；未新增 `NoWarn` 或其他压制 | 清理 `obj/bin` 后 `-t:Rebuild --no-incremental` = 0 警告 0 错误；编译引用只剩 `Microsoft.Web.WebView2.Core.dll` |

复现口径：R-023/R-024 反例由 M 的独立诊断给出（`docs/records/M-O3-review-2026-10-10.md`），本轮的保真与生命周期断言针对同一路径；未放宽任何既有断言（总数由 90 增至 130，新增均为更严格断言）。

本轮自测中发现并修复（模块内，已由测试覆盖）：

- **截断边界编码误判**：读取上限在多字节字符中间截断时，严格 UTF-8 解码抛错会被误判为“非 UTF-8”，
  退回 GB18030 导致整篇乱码（表面仍标“已索引”，实际内容不可检索）。已改为 `Decoder` + `flush:false`
  （末尾不完整序列丢弃），并在测试中用 18 MB 中文文件复现与回归。
- 复制/导出移出调用线程（`Task.Run`），避免大文件导入阻塞 UI 线程。

## 4. 未验证 / 未接线

- **未接入共享外壳**：页面未注册到 `WorkbenchPageRegistry`，模块开关、设置导航、聊天输入框接线均未做（Codex 范围）。
- **原生交互未验证**：指针/键盘/输入法/DPI/主题切换仍需接线后由 V 项 computer-use 验收。无头视图（真实 `Window`）只证明编译绑定、渲染数据与移出/重挂生命周期，不是像素或原生输入证据。
- **网页抓取**：首版不联网，仅保存链接或用户提供的快照（任务书允许）。
- **OCR / 向量检索 / 云同步**：任务书列为后续阶段，未做。
- **大文件上限**：索引读取 16 MB、预览 400k 字符、单条 6 万 token，超限截断并标注（未做大文件性能实测）。

## 5. 公共接线需求（交 Codex）

1. 组合根创建共享 `LibraryService`（建议 `new LibraryService(AppDataLayout)`，目录 `<root>/library`）。
2. 工作台页面注册（`MainWindow.Workspace.cs` 内，与 `browser` 同形）：
   ```csharp
   _workbenchPages.RegisterOptional("library", "资料库", "Folder", "builtin.library",
       () => { var view = new LibraryPageView(); view.Attach(new LibraryViewModel(service)); return view; },
       control => ((LibraryPageView)control).Detach());
   ```
   并新增 `BuiltInModuleViewModel("builtin.library", "资料库", SetLibraryModuleEnabled)` 与 `WorkbenchModulePreferences` 的启用位（该文件属 Codex）。
   页面生命周期：`Attach` 注入的视图模型归视图所有，一个实例对应一次页面生命周期；
   临时脱离视觉树不需要宿主干预，模块重建页面时须创建新的 `LibraryViewModel`（已释放实例不可重挂）。
3. 聊天接线（G1 后）：
   - 引用：订阅 `LibraryViewModel.CitationCopyRequested`，把文本插入输入框（或调用 `LibraryService.BuildCitation` 自行取用）；
   - 收藏：聊天“收藏到资料库”调用 `LibraryViewModel.ImportSnippetAsync(title, text)`（内部走 `LibraryEntryKind.SessionSnippet`）。
   - 资料库**不**自动把条目送给模型；只有用户显式选择才生成引用文本。
4. 设置导航（可选）：`SettingsPageViewModel` 增加“资料库”入口与空态。
5. 自测注册（可选）：把 `tests/Fufucode.App.Library.Tests` 纳入全量自测/CI 清单。

## 6. 状态

模块交付待接线（修复轮后仍待 M 复核）。服务层与页面视图自包含、可编译，130 项定向断言通过；整项产品验收（原生交互 + 外壳接线）未完成。
