# F 轨道进度：M7 打包 spike

> 工作区：`C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode`（branch `track-f-packaging`，基线 main 提交 `20e85e6`）
> 更新：2026-09-28

## 已完成

| 任务 | 状态 | 结果 |
|---|---|---|
| 1. 可重复打包脚本 | ✅ 完成 | `scripts/packaging/package.ps1`：`dotnet publish` self-contained win-x64 + 管理式 node + 管理式 Pi（`dist/` 全树 + lockfile）+ 双启动器（`.cmd` / `.ps1`）+ README + zip |
| 2. 干净启动验证 | ✅ 通过 | 解包到 `%TEMP%\fff-pkg-test\`，剥离 PATH 后 selftest 全绿、Pi 启动并 `get_state` 成功、干净退出、零残留 |
| 3. 测量（§12 口径） | ✅ 实测 | 体积、冷启动、Pi 就绪、退出残留均已落盘；见下 |
| 4. 升级/回退设计 | ✅ 已写入 | `docs/decisions.md` 追加 `[轨道F]` 条目 |
| 5. 许可合规清单 | ✅ 已写入 | `labs/packaging/licenses.md` |

## 测量数据（实测，标注口径）

**包体积**（`out/build/` 布局，`out/Fufucode.zip` 压缩后）：

| 组件 | 原始大小 | 说明 |
|---|---|---|
| .NET 运行时 + 应用代码（`app/`） | 219,426,302 字节（209.3 MB） | self-contained win-x64，net10.0，Avalonia 12.1.3，含所有程序集与 native assets |
| 其中：应用代码（`app/` 减去 node 与 pi dist） | ≈ 103.9 MB | 粗估值，未做精确程序集级拆分 |
| Node 运行时（`runtime/node/node.exe`） | 91,694,408 字节（87.4 MB） | v24.15.0，从开发机精确版本复制 |
| Pi 依赖（`runtime/pi/node_modules`） | 237,476,308 字节（226.5 MB） | `@earendil-works/pi-coding-agent@0.87.1` + 81 个传递依赖，由 `package-lock.json` 固定 |
| Pi 可执行树（`runtime/pi/dist`） | 18,760,258 字节（17.9 MB） | 含 `bundle/`、`modes/`（主题 JSON）、`core/` 等；只 copy 内容、不嵌套目录 |
| **包原始合计** | ≈ 584.6 MB | `app/` + `runtime/` |
| **zip 压缩后** | 183.9 MB（192,870,485 字节，28,569 个条目） | 扁平布局（zip 根即包根），`Compress-Archive -CompressionLevel Optimal` |

**启动时序**（`labs/packaging/clean-start-report.jsonl`，单次实测，单位 ms）：

| 阶段 | 值 | 口径 |
|---|---|---|
| selftest | 9,991 | 进程启动 → `Fufucode.App.exe --selftest` 返回；0 项失败 |
| Pi spawn | 124 | `Process.Start` → 进程对象返回 |
| Pi 就绪 | 720 | spawn → 第一次 `get_state` 响应成功（含 Pi 初始化 + RPC 握手） |
| 第二次 `get_state` | 64 | 同一进程的重复查询，验证可复用性 |
| Pi 停止 | 62 | 关闭 stdin → 进程退出（正常退出码 0） |
| 残留进程 | 0 | 退出后扫描 `$testRoot` 路径下的 node/Fufucode 进程 |

> 口径说明：以上为**单次**实测值，非多次取平均；机器负载会影响结果。`app/` 的 103.9 MB 为粗估（按目录相减），未做逐程序集拆分。窗口可交互时间未测——`--selftest` 是无头模式，不渲染真实窗口；"冷启动到窗口可交互"需 C 轨道的集成版本在真实桌面会话下测量（当前 `main` 无真实模型凭据，无法完成含模型 RPC 的全链路启动）。

## 干净启动验证命令与证据

```powershell
# 1. 构建产物（在 track-f 工作区执行）
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/packaging/package.ps1

# 2. 干净目录 + 剥离开发机 PATH 的验证
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/packaging/verify-clean-start.ps1
```

验证要点（已在 `verify-clean-start.ps1` 中实现）：
- 解包目标为非开发路径 `%TEMP%\fff-pkg-test\`（每次清理后重解包）
- `PATH` 剔除 `nodejs`、`npm`、`node_modules`、`pi-coding-agent`、`AppData\Roaming\npm`；node/pi/npm 必须来自包内
- `PI_CODING_AGENT_DIR` 与 `--session-dir` 指向 `%LocalAppData%\Fufucode`（包内启动器同策略）
- 自测：`app/Fufucode.App.exe --selftest` 返回 0 且输出含 `0 项失败`
- Pi：从 `runtime/node/node.exe` 启动 `runtime/pi/dist/bundle/cli.js --mode rpc --no-session`，发送真实 `get_state` 并匹配响应 ID（避免把旧响应误当新响应）
- 关断：关闭 stdin（Pi 官方退出方式），5 s 超时后 `Kill(entireProcessTree)`
- 通过后输出 `=== clean-start verification: PASS ===`，退出码 0

**证据位置**：
- `labs/packaging/clean-start-report.jsonl`（每阶段结构化记录 + 时间戳）
- `labs/packaging/clean-start.log`（运行日志）
- `labs/packaging/package.log`（打包日志）

## 未验证项（如实列出，不得标为通过）

| 项目 | 状态 | 原因 |
|---|---|---|
| 代码签名 | ❌ 未验证 | 无签名证书；zip 与 exe 均未签名 |
| 安装器 | ❌ 未验证 | 只产出 zip/目录布局，无 MSI/MSIX/InnoSetup |
| 自动升级 | ❌ 未验证 | 未实现升级器；`runtime/<version>` 并存布局仅为设计（见 decisions.md） |
| 自动 Pi 升级 | ❌ 未验证 | 计划 §3 明确禁止启动时 `pi update`；升级需人工触发并验证 |
| 冷启动到窗口可交互 | ❌ 未测 | `--selftest` 为无头模式；真实窗口需 C 轨道集成后在桌面会话测量 |
| 真实模型 RPC（prompt） | ❌ 未验证 | 应用专属 `pi-agent` 无 API key；全局凭据不自动导入（计划 §3） |
| 会话写盘 → 关闭 → 重开恢复 | ❌ 未验证 | 依赖真实模型写盘，同上 |
| 多次测量取平均 | ❌ 未做 | 时序为单次值 |
| 内存/CPU 口径 | ❌ 未测 | 计划 §12 要求的进程组内存、长会话、停止响应尚未测量 |
| 长历史加载性能 | ❌ 未测 | 需真实会话数据 |
| 升级回退真实执行 | ❌ 未验证 | 仅为设计文档，未在磁盘上执行版本并存切换 |

## 已知问题与修复记录

- `package.ps1` 的 `$root` 计算：`$PSScriptRoot '..'` 指向 `scripts/` 而非仓库根，改为 `$PSScriptRoot '..\..'`
- `Copy-Item -Recurse` 会**嵌套**源目录（产生 `bundle/bundle/…`），改用 `Get-ChildItem src | Copy-Item -Recurse -Destination dst` 只 copy 内容
- Pi 需要完整 `dist/` 树（`bundle/cli.js` 依赖 `cli-runtime.js`，主题加载依赖 `dist/modes/...`），不能只 copy `bundle/`
- zip 布局：`Compress-Archive "$packageRoot\*"` 产生扁平 zip，验证脚本相应把 `$packageRoot` 设为 `$testRoot` 本身
- PowerShell 引擎：`powershell.exe`（Windows PowerShell 5.1）按系统 ANSI（GBK）读取 UTF-8 脚本，含中文的字符串出现乱码；统一改用 `pwsh`（PowerShell 7.6.6）
- Node 重定向 stdout 时**块缓冲**，轮询文件永远看不到输出；改用 .NET `ReadLineAsync` 的 runspace 持续 Drain
- `Start-Process -WorkingDirectory` 在 PS 7 中与 `-RedirectStandard*`/`-Environment` 参数集互斥，已去掉（App 使用 `AppBase`，不依赖 cwd）
- `PiRpcClient` 风格的 `$proc.Kill(entireProcessTree: $true)` 命名参数在 PS 7 的 .NET 方法调用中不支持，改用 `Kill($true)` 并兜底 `Stop-Process -Force`
- 响应匹配必须按请求 ID 扫描（不能取"第一个 id 匹配"），否则第二次查询会命中第一次的旧响应
- `WaitForExit()` 返回值在布尔上下文里被误写为 `exit True`，已修正为 `if (-not $proc.WaitForExit(5000)) { Kill }`

## 下一步

- 与 C 轨道同步后，测量真实桌面会话的"窗口可交互"时间与进程组内存（§12 口径）
- 待真实 API key 可用时，补充真实 `prompt` → 写盘 → 重开恢复的端到端验证
- 安装器与签名留给 M7 收尾阶段；当前 spike 已完成"未知拆解"的主体
## P1-4 修复（code-review-24b5b7e.md §3，轨道 F 确认）

- **问题**：`scripts/packaging/package.ps1` 生成的 `Fufucode.cmd` 把 `PI_CLI` 设为 `%~dp0runtime\pi\bundle\cli.js`，但 Pi 实际被复制到 `runtime\pi\dist\bundle\cli.js`（打包脚本 `:35-38` copy 整棵 `dist/`）；且 `Fufucode.ps1` 启动器完全不设置 Pi 环境变量，与 `.cmd` 不一致。
- **修复**：`PI_CLI` 改为 `%~dp0runtime\pi\dist\bundle\cli.js`；`Fufucode.ps1` 追加 `$Env:PI_CODING_AGENT_DIR = Join-Path $Env:FUFUCODE_DATA 'pi-agent'`，与 `.cmd` 的 `PI_CODING_AGENT_DIR` 一致。
- **验证**：修复后重跑 `package.ps1`（zip 183.97 MB）+ `verify-clean-start.ps1` → `=== clean-start verification: PASS ===`（EXIT=0）。打包产物中 `Fufucode.cmd` 的 `PI_CLI` 指向 `runtime\pi\dist\bundle\cli.js`，`Fufucode.ps1` 含 `PI_CODING_AGENT_DIR`。
- **状态**：已修、已验证。当前 App 外壳仍为 mock 驱动、未消费 `PI_CLI`（审查原文已注明），但 M7 集成时路径与变量已一致。

## M7 本轮执行（2026-09-29）

- 读取 main 中的 `docs/archive/assignments/track-f-m7.md`。本工作树没有该任务书；按任务要求尝试 `git merge main`，但 Git 公共元数据位于 `C:\Projects\Fufucode\.git`，沙箱对该目录无写权限，报 `ORIG_HEAD.lock: Permission denied`。未强行绕过。
- 新增 `scripts/packaging/Fufucode.iss`：Inno Setup 当前用户安装，`PrivilegesRequired=lowest`，消费 `out/build`。新增 `build-installer.ps1`。本机未找到 ISCC.exe，故未产出安装器，也未验证安装/卸载。暂未消费 G 轨 `assets/installer/`，当前分支无该目录。
- 新增 `integrity.ps1`：对发布目录全部普通文件生成 SHA256 清单，并逐文件校验缺失、篡改和多余文件。已接入 `package.ps1` 的压缩前阶段。合成目录中 1 个文件写清单后校验通过；修改该文件后检测 `changed: a.txt` 且退出码 1。PowerShell 脚本解析无错误。
- 本轮尚未实现运行时崩溃报告集成、版本并存切换、`get_state`+`get_entries since` 升级门、诊断一键导出。Core 侧崩溃报告附带校验结果需要 A 轨接口。未做真实发布包构建/安装/升级/回退/§12 性能复测。
- 工作区原有 `labs/packaging/clean-start-report.jsonl` 改动及 `out/` 未清理、未纳入本轮提交；无关文件未触碰。Git 提交需要写公共 Git 元数据，当前沙箱拒绝，故本轮代码未提交。

**下一步**：协调者提供可写 Git worktree 元数据与 Inno Setup 6 后，先同步 main；完成剩余发布流程与实测，通过后小步提交轨道 F 文件。升级与回退只能保证旧版能读取旧版创建的历史，不承诺新版本写出的会话或 SQLite 可被旧版读取。

## M7 本轮继续执行（2026-09-29）

- 已只读查看 `main:docs/archive/assignments/track-f-m7.md`。该任务书在本分支工作树内不存在；遵照本轮用户指令，未执行 git merge/commit/push，也未改写 Git 元数据。
- 复核当前未提交的 `Fufucode.iss`、`build-installer.ps1`、`integrity.ps1`、`package.ps1`。四个打包/验证 PowerShell 脚本经 Parser.ParseFile 检查，均为 0 语法错误。
- 修正 `verify-clean-start.ps1`：仓库根目录改为相对脚本定位；Pi 存活时不读取尚不可用的 ExitCode；报告不再保存 `get_state` 响应片段，只记录响应长度。这些改动尚未运行完整发布验证。
- 当前环境未找到 `ISCC.exe`，标准 Inno Setup 安装器无法编译。因此安装、卸载、从安装器产物升级/回退的验收链未执行；此前 `out/` 中的目录/zip 不能充当安装器证据。
- 当前轨道没有 `src/Fufucode.Core/`、`src/Fufucode.Pi/` 写入权。崩溃报告附加完整性结论、一键诊断入口、`runtime/<version>` 运行时选择和协议兼容探针需应用/Core 接口配合，交轨道 A；本轨道现有完整性脚本仅完成发布目录清单生成与独立校验，不能宣称运行时校验已接入。
- 未运行真实模型、会话升级/回退与 §12 内存/CPU/窗口响应测量；没有相应通过结论。当前 M7 状态：外部依赖阻塞，保留所有中间产物在本工作区。

## M7 本轮补充（2026-09-29，当前工作区）

- 任务书仍仅存在于 `main:docs/archive/assignments/track-f-m7.md`，已通过只读 `git show` 获取。遵照本轮用户指令，未执行 Git commit/merge/push，也未执行 dotnet build/test/publish 或其他构建命令。
- `integrity.ps1` 增加清单路径、重复路径、SHA256 字段和文件链接检查；`package.ps1` 在发布前清空自身 `out/build`，避免上次发布文件混入新清单。两份 PowerShell 文件仅通过 Parser.ParseFile 语法检查（均 0 错误），未运行实际打包或完整性测试。
- 仍缺安装器实际编译与安装/卸载证据；本机此前未找到 ISCC.exe。`assets/installer/` 当前分支不存在，Inno 脚本尚未消费 G 轨素材。
- 运行时完整性结果接入崩溃报告、一键诊断入口、`runtime/<version>` 选择及应用内升级门需要 A/D 轨接口与协调。当前脚本尚未提供完整升级/回退流程，不能宣称 M7 完成。`get_state` + `get_entries since` 的新旧版本兼容验证、真实安装器升级/回退演练和 §12 性能复测均未执行。
- 下一步：外部驱动器同步此工作树到最新 main 后，协调 A/D 轨接口；提供 Inno Setup 6 与安装器素材，再完成发布流程脚本和实际验收。旧版本读取新版本写出的会话或 SQLite 数据仍无兼容保证。

## 本轮 M7 交付与阻塞（2026-09-29）

- 遵照用户指示，未运行 git commit/merge/push、dotnet build/test/publish、安装器编译或发布验证。本地分支没有 `docs/archive/assignments/track-f-m7.md`；通过只读 `git show main:docs/archive/assignments/track-f-m7.md` 阅读任务书。
- 新增 `scripts/packaging/export-diagnostics.ps1`：输入发布目录、用户数据目录与输出 ZIP；生成应用/Node/Pi 版本、完整性校验结论，以及仅由固定事件名和级别组成的日志摘要。导出不复制原始日志、环境变量、会话或凭据。该脚本尚未执行验证，也未接入 UI。
- 当前没有 `ISCC.exe`，`assets/installer/` 尚不存在；无法生成安装器产物或提供干净环境安装/卸载证据。
- 需轨道 A/D 配合：应用启动时调用完整性校验并将结论附到崩溃报告；由 UI 调用诊断导出；接入 `runtime/<version>` 选择及 `get_state` + `get_entries since` 会话兼容探针。F 的脚本目前没有这些应用接口，升级/回退未完成。
- `verify-clean-start.ps1` 目前仍验证 ZIP 解压目录，未达到任务书的安装器发布验收；真实安装、启动、升级、回退和计划 §12 的性能数据均未验证。请外部驱动器完成构建、安装器编译和验证后记录证据。

## M7 真实安装器升级/回退演练（本轮派单 2026-09-30）——已通过

**结果：drill PASS**（`scripts/packaging/run-drill.ps1`，证据 `labs/packaging/drill/`）

### 执行序列与实测结果

| 阶段 | 结果 | 证据 |
|---|---|---|
| ISCC 编译 0.9.0 / 1.0.0 | ✅ 各 113.3 MB（`SolidCompression=no`） | `out/Fufucode-0.9.0-setup.exe`、`Fufucode-1.0.0-setup.exe`，`compile-090.log`/`compile-100.log` |
| 静默安装 0.9.0（`/VERYSILENT /LOG`） | ✅ exit 0 | `install-0.9.0.log` 未落盘（`/LOG` 需写进存在的 dir，见下），但安装完成并注册 `{640956A0-…}_is1` |
| 解析安装目录（`UsePreviousAppDir`） | ✅ `Programs\Fufucode`（正确目录） | `resolved-install-dir` 记录 |
| 布局校验 | ✅ `app/` + `runtime/node` + `runtime/pi/dist` + `Fufucode.cmd` + `integrity.sha256.json` 齐备 | `layout-0.9.0` |
| selftest | ✅ 45.9 s，`0 项失败` | `selftest-0.9.0.out.txt` |
| 已安装目录清单 | ✅ 15,280 文件 | `tree-0.9.0.json` |
| 覆盖升级到 1.0.0 | ✅ exit 0，同目录保留 | `install-1.0.0.log`、`resolved-install-dir-1.0` |
| 布局校验（1.0.0） | ✅ 同上 | `layout-1.0.0` |
| 完整性校验 `integrity.sha256.json` | ✅ 15,276 文件全部匹配，无 missing/changed | `integrity-1.0.0.log`（extra 已白名单） |
| 卸载 | ✅ exit 0，代码树移除 | `uninstall.log`、`code-tree-removed` |
| 用户数据保留 | ✅ `%LocalAppData%\Fufucode` 未触碰 | `user-data-preserved` |
| 回退重装 0.9.0 | ✅ exit 0 | `install-rollback-0.9.0.log` |

### 根因定位（本次演练的核心发现）

- **"setup files are corrupted" 的真凶是安装器截断**：早期一次前台 `ISCC` 编译被 180 s 超时杀停在写 EXE 中途，产出不完整包（93.8 MB → 后降到 61 MB）。我后续 `git merge main` 后在后台**无超时**完整编译（约 3 分钟），得到 113.3 MB 的完整包，全程安装成功。
- **`SolidCompression=no` 是错误归因**：二分最初指向它，实则是「被截断的旧 EXE vs 全新完整 build」混淆。决定性实验：`SolidCompression=yes` 的**完整**全量包（95.9 MB）也干净安装（`bisect-solidfull`）。`Fufucode.iss` 注释已更正为如实记录；`no` 保持为已证安全默认，`yes` 可被采用但未在本演练中作正式验证对象。
- **`UsePreviousAppDir` 捕获**：早期 bisect 脚本与正式 `.iss` 共用同一 `AppId`，导致 0.9.0 静默重定向到旧 `Fufucode-bisect` 目录。`run-drill.ps1` 改为从注册表 `InstallLocation` 解析真实目录并告警，bisect 脚本改用独立 `AppId`/`DefaultDirName`。
- **PowerShell 语法/运行时陷阱**（已逐一修复）：`-and` 不能跟在行尾续行符 `` ` `` 后；`WaitForExit()` 返回值泄漏为 `exit True`；`.Replace('-')` 优先级在 byte[] 上；`/LOG` 需可写路径且父目录已存在；`powershell.exe`（WinPS 5.1）按 ANSI 读 UTF-8 脚本致中文乱码 —— 统一 `pwsh`。

### 新增脚本（`scripts/packaging/`）

- `pi-pipe.ps1` —— 从 `verify-clean-start.ps1` 抽出的 Pi 管道助手（Start/Find/Stop）。
- `run-drill.ps1` —— 安装器升级/回退演练：静默安装、真实目录解析、布局校验、selftest、已安装清单、integrity 校验（白名单 Inno 卸载器与 runtime 生成的 `selftest-window.png`）、卸载、用户数据保留、回退重装。
- `build-installer-version.ps1` —— integrity manifest + 按版本编译安装器（`/DAppVersion`、`/DOutName`）。
- `generate-integrity.ps1` / `validate-integrity.ps1` —— 已删除：与 main 已带的 `scripts/packaging/integrity.ps1` 重复（属本轨），避免三份实现漂移。
- `integrity.ps1`（main 已带，沿用）—— `Write`/`Verify` 模式，SHA256 清单 + 路径遍历/重链接安全校验。

### `Fufucode.iss` 关键变更（相对 main）

- `OutputBaseFilename` 改为可参数化 `{#OutName}`，支持 `/DOutName=Fufucode-0.9.0-setup` 区分产物。
- 安装完整性清单 `integrity.sha256.json` 一并进入 `{app}`。
- `[Run]` 加 `--selftest`（`postinstall unchecked`）。
- `SetupLogging=yes`（`/LOG=` 由调用方指定具体文件）。
- `PrivilegesRequired=lowest` 保持不变；`DefaultDirName={localappdata}\Programs\Fufucode`。

### 未验证项（如实记录）

- **代码签名**：安装器与 exe 未签名（`bisect-solidfull` 等产物也无签名）。
- **MSI/MSIX 安装器**：只有 Inno Setup EXE；其他形态未做。
- **`SolidCompression=yes` 正式验证**：本次决定性实验证明它能正常安装全量包，但作为**默认**配置仍是 `no`；如需启用需以相同 drill 复测。
- **`/LOG=<file>` 形态下安装日志**：drill 里每次安装均传 `/LOG=<file>`，但 Inno 只在**发生错误**或交互时才写文件日志；正常静默安装无日志文件生成（退出码即证据）。如需常态日志可改 `SetupLoggingFile`（本版未启用）。
- **协议兼容探针回退**：`get_state` + `get_entries`（`since`）在升级后真实执行尚未演练（当前 drill 只到「旧版可重装」）。
- **真实模型 prompt 路径**：同上，缺 API key，无法完成真实模型对话。
- **崩溃报告附带校验结论**：`export-diagnostics.ps1` 已生成 `integrity.json`，但崩溃场景未演练。
- **多次测量取平均**：selftest 时长为单次值（45.9 s）。

## F3（2026-09-30 协调者派单）——D Wave2 实测打包四坑，全部修复并重出包验证：**drill PASS**

### 四个坑 → 修复 → 验证

| # | 问题 | 修复 | 证据 |
|---|---|---|---|
| ① | 安装包缺 `extensions/fufucode-bridge`，装上后审批不可用、发不了消息 | `package.ps1` 在 stage 阶段把 `extensions/fufucode-bridge/`（`index.ts`、`package.json`、`policy.ts`，仅生产扩展，不含 dev fixtures `broken-fixture`/`demo-extension`/`atria-widget-lab`）拷进 `out\build\extensions\fufucode-bridge\`，同步进 zip 与安装器；`integrity.sha256.json` 重算（15,279 → 15,282 文件） | `out/build/extensions/fufucode-bridge/*`、integrity manifest 含 3 条 `extensions/` 记录；drill `layout-0.9.0`/`layout-1.0.0` 均断言 `extensions/fufucode-bridge/*` 存在 |
| ② | `Fufucode.cmd` 对 `--selftest --realpi` 丢实参 | 删除只传 `%~1` 的特判分支，统一 `"%APP_DIR%\Fufucode.App.exe" %*` 透传全部参数 | `out/build/Fufucode.cmd`、`scripts/packaging/package.ps1` 模板同步 |
| ③ | 旧 0.9.0 安装器索引损坏/Inno 找不到数据 | 后台无超时完整重编译（≈3 min），产出完整 `Fufucode-0.9.0-setup.exe`（113,314,364 B）与 `Fufucode-1.0.0-setup.exe`（113,314,375 B） | `compile-090.log`/`compile-100.log` “Successful compile” |
| ④ | 开始菜单快捷方式工作目录 | `[Icons]` 两处加 `WorkingDir: "{userdocs}"`（开始菜单 + 桌面快捷方式都生效） | `Fufucode.iss`；安装器重出（包含该变更） |

### 重出安装器 + 完整 drill 序列（实测）

- `pwsh -File scripts/packaging/run-drill.ps1` → `=== drill: PASS ===`（EXIT=0），证据 `labs/packaging/drill/`
- 序列：装 0.9.0（exit 0）→ `resolved-install-dir = Programs\Fufucode` → 布局+integrity.manifest → selftest 46.4 s `0 项失败` → snapshot 15,283 文件 → 覆盖升级 1.0.0（exit 0，同目录）→ 布局 1.0.0 → integrity 15,279 文件全绿 → 卸载（代码树移除，仅 runtime `selftest-window.png` 残留属预期）→ 用户数据 `%LocalAppData%\Fufucode` 保留 → 回退重装 0.9.0（exit 0）
- `out/build/Fufucode.cmd` 与 `Fufucode.ps1` 两个启动器参数透传一致（`%*` / `@args`）

### 未验证项（如实，不假标）

- **代码签名**：两安装器与 exe 均未签名。
- **MSI/MSIX**：仅 Inno Setup EXE。
- **开始菜单快捷方式 WorkingDir 实测**：`.iss` 已写 `WorkingDir="{userdocs}"` 且安装器重出成功，但 drill 未逐字节核对开始菜单 `.lnk` 的 WorkingDir 字段（Inno 文档语义，未做 GUI 核对）。
- **bridge 装上后真实审批/消息链路**：包内文件已确认存在并被 manifest 覆盖；其运行时行为（Pi 加载、审批流）需真实模型凭据，本轮无法验证。
- **用户数据目录默认语义**：`FUFUCODE_DATA` 仍指向 `%LocalAppData%\Fufucode`，未随本次改动。

### 协调者复核（A6+F3 合并包，main@4dc6a16 后）

- 合并冲突已解（package.ps1 两处：bridge 拷贝并 F 注释；launcher 用 A 的参数路由，%* 透传覆盖 F 的坑②）。
- 发现并修复回归：A 的工程级 `dotnet publish`（单工程 App）在脏 obj 下产出缺预编译 XAML 的 app——清 obj/bin 重发后 selftest 0 失败。**教训**：包构建前必须清中间产物。
- 重跑 `cli-package-verify.ps1`（out/build 真实产物）17 项 launcher 全过；regen integrity（15,465）+重打 zip；直跑 ISCC 重出 0.9.0/1.0.0 安装器（各 ~197s，成功）。
- 重跑 run-drill.ps1：装0.9.0→布局+manifest→selftest 63.9s 0失败→升1.0.0→integrity 15,462→卸载→用户数据保留→回退0.9.0 → **PASS**（labs/packaging/drill/run.log + drill-report.jsonl）。
- 遗留（F 轨道后续）：`build-installer-version.ps1` 的 `param()` 不在首行导致 `-File` 调用报"param 不识别"（drill 内联了 ISCC 才不受影响）；P0-2 打包前先清 obj/bin 的建议并入流程。

## 1.3.0 发布包（2026-10-04 协调者派单）——**drill PASS**

工作区 `track-f-packaging`，开工 `git merge main` → **fast-forward 到 main@44bb2de**（本分支此前无独有提交）。背景：D 侧五区管线 R1-R4+D6 全部完成、Pi 内核 1.0.0、E1 浏览器 Preview 已接线。

### 产物（实测尺寸与指纹）

| 产物 | 字节 | SHA256 |
|---|---|---|
| `out/Fufucode-1.3.0-setup.exe` | 144,080,959（137.4 MB） | `568714844a7bbe7cc92a2332b8963be84f16850ec31eb5070348daf1c21dab03` |
| `out/Fufucode.zip` | 194,878,067（185.85 MB，16,603 个条目） | `0359a7e1361e6ff0f123c09c2d3b7ccbc9026711cbb58e8cb2c664662a8f75e8` |
| `out/build/` 载荷合计 | 552 MB：`app` 213 MB、`cli` 81 MB、`runtime` 256 MB、`extensions` 1 MB | 清单 `out/build/integrity.sha256.json` = `c68c8be532a16ff9cf907e30f02fc2460f9528504761e5b759538645f8afa24a`，共 **16,536 个文件** |
| `out/Fufucode-1.2.0-setup.exe`（旧版对照） | 143,513,927 | `84388117eed899c7d6d945f89e593342f1015df73186f5da0fce0416849e0d4c` |

> 1.2.0 旧包不是「同载荷换版本号」：取自 A12 交付时产出的真包（本机参照 worktree 的 `out/`，2026-10-02 01:33 落盘），复制入本轨 `out/` 后使用。因此本轮 drill 是**真实跨版本覆盖升级**，与 F2/F3 时代 0.9.0↔1.0.0 同载荷演练口径不同。

### 执行序列（`run-drill.ps1`，EXIT=0，`=== drill: PASS ===` 于 09:04:39）

| 阶段 | 结果 | 证据 |
|---|---|---|
| 打包前清中间产物 | ✅ `src/*` 下 obj/bin **零个**（`find` 无输出）；未复现「缺 XAML 包」历史问题 | `labs/packaging/package-1.3.0.log` |
| `package.ps1` | ✅ exit 0（App+Cli 双 self-contained publish、node、Pi 1.0.0 全树、bridge、双启动器、integrity、zip） | 同上 |
| 包上 selftest（安装器前置门） | ✅ `Fufucode.cmd --selftest` exit 0，`== 自测结束：0 项失败 ==`，77 s，371 条 `ok:`，含 E1 浏览器 URL/白名单/转义/二进制判定 | `labs/packaging/selftest-package-1.3.0.log` |
| 载荷完整性复验 | ✅ 删除 selftest 落下的 `app/selftest-window.png` 后 Verify：`ok:true checked:16536 missing:[] changed:[] extra:[]` | 本轮命令输出 |
| CLI 从包路径启动（SOP 6d） | ✅ `out/build/Fufucode.cmd settings get --json` exit 0 且输出合法 JSON；`out/build/cli/fufucode.exe --help` exit 0 | `cli-package-1.3.0.log`、`cli-exe-help-1.3.0.log` |
| ISCC 编译 1.3.0 | ✅ `Successful compile (196.422 sec)`，压缩 16,537 个文件条目 | `compile-1.3.0.log` |
| 装 1.2.0（静默） | ✅ exit 0，150 s；`resolved-install-dir = %LocalAppData%\Programs\Fufucode`（= DefaultDirName，未被 UsePreviousAppDir 重定向） | `drill-report.jsonl` |
| 布局校验 1.2.0 | ✅ `app/Fufucode.App.exe`、`runtime/node`、`runtime/pi/dist/bundle/cli.js`、`Fufucode.cmd`、`extensions/fufucode-bridge/{index.ts,package.json,policy.ts}` 齐备，`integrity.sha256.json` 在位 | `layout-1.2.0 pass:true` |
| 已装 1.2.0 selftest | ✅ exit 0，63,941 ms，`0 项失败` | `selftest-1.2.0.out.txt`（`.err.txt` 为空） |
| 1.2.0 树快照 | ✅ 16,536 文件（含逐文件 SHA256） | `tree-1.2.0.json` |
| 覆盖升级 1.3.0 | ✅ exit 0，74 s，同目录 | `upgrade-1.3.0 pass:true` |
| 布局校验 1.3.0 | ✅ 全部在位 | `layout-1.3.0` |
| **完整性校验 1.3.0** | ✅ `checked:16536 missing:[] changed:[]`；extra 仅白名单三项（`unins000.dat`、`unins000.exe`、`app/selftest-window.png`），**无意外残留** | `integrity-1.3.0.log`、`integrity-1.3.0 pass:true` |
| 1.3.0 树快照 + 版本增量 | ✅ 16,540 文件。增量：删除 **0** 个 / 新增 **4** 个（全为 E1 WebView2：`app/Microsoft.Web.WebView2.Core.dll`、`.xml`、`app/WebView2Loader.dll`、`app/runtimes/win-x64/native/WebView2Loader.dll`）/ 内容变化 28 个 | `tree-1.2.0.json` vs `tree-1.3.0.json` |
| 卸载 | ✅ exit 0；`code-tree-removed` 通过（无 .exe/.dll/.js/.cmd/.ps1/.json 残留），安装目录整体消失 | `uninstall.log`（2.86 MB） |
| 用户数据保留 | ✅ `%LocalAppData%\Fufucode` 未被触碰（`desktop.db`、`sessions`、`pi-agent`、`logs`、`settings.json` 均在） | `user-data-preserved pass:true` |
| 回退重装 1.2.0 | ✅ exit 0，64 s | `rollback-1.2.0 pass:true` |
| 演练后机器终态 | ✅ 安装目录不存在；`HKCU\...\Uninstall\{640956A0-…}_is1` 无键；`HKCU\Environment\Path` 中**无** Fufucode 条目且 `HKCU\Software\Fufucode\Installer` 标记已清——安装器「只回收自己添加的 PATH 项」逻辑在本路径闭环 | pwsh 实测输出 |

### 本轮新关闭的历史未验证项

| 项 | 此前的记录 | 本轮实测 |
|---|---|---|
| 快捷方式 WorkingDir 逐字段核对（F3 遗留「未逐字节核对 .lnk」） | 只在 `.iss` 里写了 `WorkingDir`，未核对产物 | 桌面 `Fufucode.lnk` 实测 `TargetPath = {app}\Fufucode.cmd`、`WorkingDirectory = C:\Documents`。本机该账户的 Known Folder「Personal」在注册表中即为 `C:\Documents`（`[Environment]::GetFolderPath('MyDocuments')` 返回同值，目录存在），故 `{userdocs}` 展开**正确、非缺陷** |
| 真实跨版本覆盖升级 | F2/F3 只做过 0.9.0↔1.0.0 同载荷 | 1.2.0 真包 → 1.3.0 真包，integrity 全绿、零残留 |
| 安装器 PATH 取处闭环 | 未记录 | 演练结束时 `HKCU\Environment\Path` 无 Fufucode 条目、`HKCU\Software\Fufucode\Installer` 标记已清 |
| CLI 从包路径启动 | 未测 | 包内 `.cmd` 与 `cli/fufucode.exe` 两入口均 exit 0 |

### 本轮代码与文档改动（均在 F 独占路径）

- `scripts/packaging/run-drill.ps1`：硬编码 0.9.0/1.0.0 → 参数化 `-OldVersion`/`-NewVersion`（默认 1.2.0/1.3.0）；阶段名与证据文件名随之版本戳化（避免旧证据名冒充新结论）；`$zero/$one` → `$oldSetup/$newSetup`；遗留标签 `resolved-install-dir-1.0` → `resolved-install-dir-$NewVersion`。Parser 检查 0 错误。
- `scripts/packaging/package.ps1`：包内 `README.txt` 的 Pi 版本串由陈旧的 `@0.87.1` 改为实际的 `@1.0.0`。
- `labs/packaging/licenses.md`：按 1.3.0 实际载荷重算——Pi 1.0.0 闭包逐包许可（脚本枚举 `node_modules` 全树 `package.json` 的 license 字段；117 个包按许可分组）；纠正 `tslib`（旧清单记 MIT，实为 **0BSD**）并消去 `buffer-equal-constant-time` 的重复行；新增 `Microsoft.Web.WebView2 1.0.4258.31`（已核对包内 `LICENSE.txt` 为 BSD-3-Clause 式再分发条款，另含 `NOTICE.txt`）、`Microsoft.Data.Sqlite.Core 10.0.12`（MIT）、`SQLitePCLRaw 2.1.12`（Apache-2.0，nuspec license expression 核对）；补 `cli/` 与 `app/` 同依赖口径；合规缺口新增第 8 项（WebView2 **运行时**前置——包内只有 SDK 程序集与 `WebView2Loader.dll`，安装器既不检测也不打包 Fixed Version）；第 6 项「无安装器」改为「已有 Inno 用户级 EXE，仍无 MSI/MSIX」。
- 新增日志/证据：`npm-ci-1.3.0.log`、`package-1.3.0.log`、`selftest-package-1.3.0.log`、`cli-package-1.3.0.log`、`cli-exe-help-1.3.0.log`、`compile-1.3.0.log`，以及 `drill/`（run.log、drill-report.jsonl、两版树快照、selftest 输出、uninstall 日志×2、pre-uninstall 日志×2）。

### 环境差异与处置（本轮实际遇到的阻塞，全部如实）

1. **本机执行账户是 `mio`，不是任务书里的 `21894`**：worktree 仍在 `C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode`（可读写），但 `%LOCALAPPDATA%` 指向 `C:\Users\mio\AppData\Local`，故安装/卸载/用户数据均落在 mio 档案下。
2. **任务书给的 ISCC 路径不存在**：`C:\Users\21894\AppData\Local\Programs\Inno Setup 6\ISCC.exe` 无此文件；实际使用 `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`（Inno Setup 6.7.3）。
3. **`runtime/` 在本 worktree 缺失**（gitignore，未随 main 合并进来），`package.ps1` preflight 直接抛错。处置：① `runtime/node/node.exe` 从参照工作区（`C:/Projects/Fufucode`）的受管 `runtime/node/node.exe` 按字节复制，实测 `--version` v24.15.0，SHA256 前缀 `3331e1ff…` 与 `%ProgramFiles%\nodejs\node.exe` 同值；② `runtime/pi/node_modules` 以 `npm ci --prefix runtime/pi --ignore-scripts` 依 lockfile 安装（121 包，Pi 1.0.0，日志 `npm-ci-1.3.0.log`）。安装前核对本轨 lock 与参照 lock：剥行尾后逐字节相同（`052811f5…`）。
4. **沙箱禁止 PowerShell 递归删除**：`package.ps1:11` 的 `Remove-Item -Recurse out\build` 报 `Access to the path 'C:\Users\21894\.codex' is denied`（NTFS ACL 本身没问题——同一目录用 shell `rm -rf` 可删），首轮打包 exit 1。处置：调用脚本前先用 shell 清空 `out/build` 与 `out/Fufucode.zip`，令脚本走「目录不存在→新建」分支，重跑 exit 0。**未改脚本语义**（防止旧版文件混入新清单的意图保留）。
5. **MSYS 吞掉 ISCC 的 `/D` 开关**：从 Git Bash 直调 ISCC 时 `/DAppVersion=1.3.0` 被当作路径参数，报 `You may not specify more than one script filename`。处置：改由 `pwsh` 传参，编译成功。
6. **演练前机器上有 1.1.0 时代的孤儿载荷**：`%LocalAppData%\Programs\Fufucode` 注册在 HKCU（`DisplayName: Fufucode version 1.1.0`），目录里同时存在 `unins000` 与 `unins001` 两套卸载器；逐个静默卸载均报 `Removed all? Yes` / exit 0，却只删掉 2 个文件（15,463 / 15,465 仍在），且注册表键随之消失——即该载荷不被任何卸载器认领，已无法常规卸载。为让 drill 测到干净的默认路径（否则旧残留会被 integrity 判为意外 extra，分不清是 1.3.0 的缺陷还是历史残留），**经用户明确批准后**删除 `%LocalAppData%\Programs\Fufucode` 整目录（纯程序载荷，可由 `out/Fufucode-1.2.0-setup.exe` 或 1.3.0 包复原）；`%LocalAppData%\Fufucode` 用户数据目录未触碰。证据：`drill/pre-uninstall-1.1.0.log`、`drill/pre-uninstall-1.1.0-attempt2.log`。**教训**：同一 `{app}` 目录里出现第二个 AppId 的卸载器（`unins001`）时，先前演练留下的实验安装会让后续卸载静默失效； drill 前应确认目标目录无孤儿载荷。
7. `build-installer-version.ps1` 的 `param()` 不在首行的老毛病**仍未修**（按本轮指示直跑 ISCC 绕过，未触碰该脚本）。

### 顺带观察（不改他人代码，仅记录）

- `--selftest` 的截图落点随启动上下文变化：从包目录跑为 `C:\Users\21894\.codex\worktrees\tmp\{w1,h2,h3}-shots`，从已装目录跑为 `C:\Users\mio\tmp\{w1,h2,h3}-shots`——两处都在 `{app}` 与 `%LocalAppData%\Fufucode` 之外，故不污染载荷完整性；但包内那次运行确实往 `out/build/app/` 写了 `selftest-window.png`，我已删除该文件后复验 integrity 全绿（表内已记）。属 A/D 轨自测器的路径选择，本轨不改。

### 未验证项（1.3.0，如实列出）

| 项目 | 状态 | 说明 |
|---|---|---|
| 代码签名 | ❌ 未验证 | 无证书；zip、setup.exe、`app/`、`cli/` 内 exe 均未签名 |
| MSI/MSIX | ❌ 未做 | 仅 Inno Setup EXE |
| WebView2 真实导航与像素渲染 | ❌ 未验证 | 无头 selftest 覆盖 URL/白名单/转义/二进制判定，但按 E 轨自身声明不触碰 WebView2 运行时；本机 WebView2 Runtime 在位（HKLM EdgeUpdate Clients `{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` `pv=154.0.4258.53`），**已装包 GUI 内 Preview 实机导航未做**（需真实桌面会话人工操作） |
| 目标机缺 WebView2 Runtime 时的行为 | ❌ 未验证 | 安装器不检测、不打包 Fixed Version runtime（见 licenses.md 缺口 8） |
| 开始菜单快捷方式 | ⚠️ 存疑 | 静默安装后在用户开始菜单目录检索不到任何 Fufucode `.lnk`（桌面项由 `desktopicon` 任务创建成功）。是否为静默模式下的预期行为，本轨未查 Inno 侧原因 |
| 1.2.0→1.3.0 会话/SQLite 数据双向兼容（SOP 6e） | ❌ 未做 | 本轮 drill 只验安装机制 + 卸载不破坏用户数据目录；未做「旧版读写 → 新版续写 → 旧版回读」的脱敏真实副本演练 |
| Pi 1.0.0 探针组（`scripts/pi-probe`） | ❌ 本轮未跑 | 1.0.0 的兼容结论沿用 A12 记录，本轮未重跑探针 |
| 已安装 GUI 首启到窗口可交互、进程组内存/CPU（§12 口径） | ❌ 未测 | drill 走无头 `--selftest`，未渲染真实窗口 |
| 真实模型 prompt / bridge 审批链路运行时 | ❌ 未验证 | 包内 `extensions/fufucode-bridge` 已确认在位并被清单覆盖，运行时行为需凭据 |
| 多次测量取平均 | ❌ 未做 | selftest 63.9 s / 77 s、ISCC 196.4 s 均为单次值 |
