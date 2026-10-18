# P2 固定版本桌面候选包 — 执行会话交付记录

日期：2026-10-11。任务书：`docs/tasks/hourly-P2-candidate.md`（难度 L3，推荐 `group/auto-qfmodel`）。
边界：`docs/tasks/execution-contract.md`、`docs/tasks/session-handoff-protocol.md`、`docs/tasks/development-topology-v2.md`、`docs/tasks/production-delivery.md` §8。

## 0. 开工身份卡

| 项 | 值 |
|---|---|
| cwd / 工作树 | `C:\Users\mio\.codex\worktrees\126c\Fufucode` |
| Git 状态 | detached HEAD，开工时工作树干净（`git status` clean，无继承未提交改动） |
| 开工 HEAD | `1d0b53ac00fc5113656bdb7777b380e4755ba133` |
| 固定产品来源 | `e64dd1b15e1cfdc1de99afae7d4cc3c32c2f047e`（HEAD 的父提交；`e64dd1b..HEAD` 仅一条任务书提交，`git diff` 只含 `docs/records/hourly-dispatch-ledger.md` 与 `docs/tasks/hourly-P2-candidate.md`） |
| 包装修复提交 | `43dd644`、`c120c96`（见 §4） |
| 允许路径 | `scripts/packaging/**`、`labs/packaging/**`（任务相关报告）、`docs/records/P2-candidate.md`；产物/日志在 `out/`、`tmp/` |
| 未触碰 | App/Core/Pi/共享解决方案、权威进度/决策、`reference/`、用户数据、全局运行时配置 |
| 模型纪律 | 零云模型调用：全部验证为本机进程/隔离环境，无模型试跑，故未使用 `devin/swe-2`（无需要试跑的环节） |

开工现场事实：本工作树的 `runtime/node/node.exe` 与 `runtime/pi/node_modules` 缺失（`runtime/` 被 `.gitignore` 忽略，只跟踪 `LICENSE`、`LICENSE.provenance.json`、`package.json`、`package-lock.json`）。`out/` 与 `labs/packaging/` 有来自上一区域的继承工件，其中 `out/Fufucode.zip` 与主区 10-08 的 ZIP 逐字节相同，不是本固定来源的产物。

## 1. 结论摘要

- 候选包与版本化安装程序均从固定产品来源 `e64dd1b` 构建完成，SHA256/大小/来源见 §5。
- 隔离验证全部实跑通过：解压启动自测、包内 Pi `get_state`、包内完整性校验、包内 Node/Pi/桥接发现、临时数据根恢复、干净 Windows Sandbox 安装/启动/卸载 12/12 通过。
- 实测发现并修复 2 个真实打包阻塞（PS5.1 解析失败、`wsb` 被继承 HTTP 代理阻断），小步提交。
- **本包状态：候选包交付 / 提交待验收。候选不称生产正式发布，不自称 G3/R 通过。**
- 未验项如实列出（§7）：IME/DPI/多显示器原生项、代码签名、OMP 打包内可用、升级安装路径。

## 2. 步骤 1 — 运行时核对与托管

只读检查主区已托管版本，按字节复制到本工作树，未做任何全局 npm 安装、未改 provider、未下载付费资源、未把机器特定路径写入代码。

| 组件 | 值 | 校验 |
|---|---|---|
| Node | `v24.15.0`，`runtime/node/node.exe`，91,694,408 B | SHA256 `3331e1ffe19874215472217c5e94f5a0c6d8e18c4ac7111d3937aa0ad5e9b4a5` = `LICENSE.provenance.json` 的 `binarySha256` |
| Node 许可 | `runtime/node/LICENSE` | 磁盘 SHA256 `8efdacdc…220c`；把 CRLF 归一为 LF 后为 `4573185d56580da2b890ba34a85a409257640f1c5632eade4300137266194d18` = provenance `licenseSha256`，git blob `403ea3c5717087b20e9cddaa2b4881256b6b4858` = provenance `gitBlob`。差异只来自本机 `core.autocrlf=true` 的检出换行，不修改该跟踪文件（见 §8-1） |
| Pi | `@earendil-works/pi-coding-agent@1.1.0`，15,072 文件 | `runtime/pi/node_modules` 复制自 `C:\Projects\Fucode\Fufucode@bf4cce6ce9b90a58ed6927710763d6f20edd8a63`；`dist/bundle/cli.js` SHA256 `e79626f2dd6f94aa45d30f3fa63cd84319a6eefcd150b353cfaf274366926774`，与主区逐字节一致；锁文件 git blob `5ed4c8085f10a31c72fb6ce8558fe8d51cbaff3d` 两区一致 |
| OMP | 未托管 | 仓库 `runtime/omp/` 不存在，包内也不含。见 §6-OMP |

## 3. 步骤 2 — 构建与测试（真实命令/退出码）

| 命令（workdir 均为本工作树） | 退出码 | 结果 | 证据 |
|---|---|---|---|
| `dotnet restore Fufucode.sln -v quiet` | 0 | 全新工作树先还原（`--no-restore` 首次失败为 NETSDK1004 缺资产文件，属工作树状态，非代码缺陷） | `tmp/`（PowerShell 7 默认 `Tee-Object` 未落该文件，退出码已记录） |
| `dotnet build Fufucode.sln --no-restore -v quiet` | 0 | 已成功生成，**0 个警告、0 个错误**，22.90 s | `tmp/build-sln.log` |
| `dotnet src\Fufucode.App\bin\Debug\net10.0\Fufucode.App.dll --selftest`（全量，`FUFUCODE_DATA` 指向 `tmp/p2-selftest-data`） | 0 | `== 自测结束：0 项失败 ==`，788 条 ok | `tmp/selftest-full.out.txt`、`tmp/selftest-full.err.txt`（0 B） |
| `dotnet run --project tests/Fufucode.Core.Tests --no-build` | 0 | 336 PASS / 0 FAIL / 1 SKIP（SKIP 需 `FUFUCODE_ATRIA_REAL_MODEL_TEST=1` 与真实凭据，属外部条件） | `tmp/core-tests.log` |
| `dotnet run --project tests/Fufucode.Pi.Tests --no-build` | 0 | 21 PASS / 0 FAIL（含真实 Pi `get_state`/models 查询） | `tmp/pi-tests.log` |
| `dotnet publish App/Cli -c Release -r win-x64 --self-contained`（经 `package.ps1`） | 0 | 发布日志无 warning/error 行 | `tmp/package-p2.log` |

不把构建通过当行为验收：行为证据来自上表的全量 `--selftest`、Core/Pi 套件与 §5–§6 的隔离运行。

## 4. 实测阻塞与增量修复（均已提交）

| # | 提交 | 现象（实跑） | 根因 | 处置 |
|---|---|---|---|---|
| 1 | `43dd644` fix(packaging): exit codes in clean-start and drill work on Windows PowerShell 5.1 | `powershell -NoProfile -File scripts\packaging\verify-clean-start.ps1` 直接 ParserError（`Unexpected token '?'`），一项检查都没跑就 exit 1 | 末行 `exit ($ok ? 0 : 1)` 是 PowerShell 7 三元运算符；Windows PowerShell 5.1 不解析。`run-drill.ps1` 同一写法 | 改为 `if ($ok) { exit 0 } else { exit 1 }`（5.1/7 均可解析，语义不变）。对 `scripts/packaging/*.ps1` 全量做 5.1 AST 解析检查：0 错误 |
| 2 | `c120c96` fix(packaging): bypass inherited HTTP proxy for the wsb loopback gRPC endpoint | `run-sandbox.ps1` 抛 `Windows Sandbox CLI start failed.`；`wsb.exe list` 同样失败：`Requesting HTTP version 2.0 with version policy RequestVersionExact while unable to establish HTTP/2 connection` | 本机继承 `HTTP_PROXY/HTTPS_PROXY=http://127.0.0.1:7897`，`wsb` 的 localhost gRPC 请求被当作 CONNECT 发给代理。对照：代理置空或 `NO_PROXY=localhost,127.0.0.1` 时 `wsb list` exit 0 | 仅为该进程把 `localhost,127.0.0.1` 并入 `NO_PROXY`（保留原有条目，不改系统/全局代理配置、不动其他流量） |

诊断中另外两次 `Sandbox interactive login did not become ready.` 不是打包脚本缺陷，也不是登录窗口过短：临时把 `connect` 超时提到 240 s/600 s 后仍失败，实测该超时不覆盖 `connect` 自身的阻塞。一次带计时的夹具运行显示 `wsb connect` 冷镜像阻塞约 394 s 后 `whoami` 立即就绪；失败集中在本机仍驻留一个未退出的 Windows Sandbox 实例（该功能每会话单实例）。停止我自己遗留的可丢弃实例、确认 `wsb list` 为空后，**提交态脚本（仅代理修复、超时保持原 90 s）直接 PASS**。据此把超时改动回退，不保留未证实的“修复”。该单实例驻留作为环境注意事项记入 §8-3。

## 5. 步骤 3/4 — 候选包、隔离验证与版本化安装程序

### 5.1 产物与校验值

| 产物（`out/`，未提交二进制） | 大小 | SHA256 | 来源 SHA |
|---|---|---|---|
| `out/Fufucode.zip` | 186,277,820 B（177.65 MB） | `a8c2033c49548070510d598c55bc55ca4f3ccf13f8d9b02b663aff2e340cbbcd` | 产品 `e64dd1b15e1cfdc1de99afae7d4cc3c32c2f047e` + 包装修复 `43dd644`、`c120c96` |
| `out/Fufucode-1.4.2-candidate-setup.exe` | 140,557,907 B（134.05 MB） | `a80e710c18f8ccfa5511da7595b1148e1e01709d91b988a715e93693e98e80d7` | 同上（Inno Setup `ProductVersion=1.4.2-candidate`） |
| 包内清单 `integrity.sha256.json` | 3,927,484 B | `2aa086b47c0019d109bd5ef7f88624396b74919605f1fa3fd7fcbb30e0da658e` | 15,542 条 |

载荷目录规模：`app` 213.7 MB、`cli` 80.0 MB、`runtime` 205.6 MB、`notices` 0.4 MB、`extensions` 0.0 MB（`tmp/package-p2.log`）。`archive-package.ps1`：`PASS archive: 15542 file hashes verified`。继承的旧 `out/Fufucode.zip` 已被本轮构建覆盖（其值原与主区 10-08 产物相同，SHA256 `14e4dcb2…e7c36`）。

### 5.2 解压 / 临时数据根启动 / 包内发现（新临时目录）

`pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\packaging\verify-clean-start.ps1` exit 0，解压到 `%TEMP%\fff-pkg-test-c2659bb6a5424174b2a88ab9fe2940fd\package`，PATH 剥离至 94 项（剔除含 node/npm/pi/fufucode 的目录），报告 `labs/packaging/clean-start-report.jsonl`：

| 阶段 | 结果 |
|---|---|
| selftest（打包 App，clean PATH，临时 `FUFUCODE_DATA` + 临时 project） | exit 0，`pass=true`，145,332 ms |
| pi-start（包内托管 Node 启 Pi RPC） | spawn 1,377 ms，ready 2,042 ms，`gotResponse=true`，进程存活 |
| get-state（第二次查询） | success，563 B，57 ms |
| pi-stop | graceful，76 ms，exit 0，`clean=true` |
| residual | 0 个包内残留进程 |
| overall | `pass=true` → `=== clean-start verification: PASS ===` |

同批隔离取证（非脚本，直接驱动包内产物）：

- 包内完整性校验：`integrity.ps1 -Mode Verify` 对解压树 → `{"ok":true,"checked":15542,"missing":[],"changed":[],"extra":[]}` exit 0。
- 包内桥接发现：按产品实际参数 `node.exe runtime/pi/.../cli.js --mode rpc --no-session --extension <pkg>/extensions/fufucode-bridge`（`PersistentSessionManager.cs:278` 的 `--extension` 形式）启动真实 Pi，`get_state` 返回 `success=true`，无 stderr，exit 0。`ResolveRuntime`/`AppDataLayout` 均按包内相对路径与 `--app-data` 解析，无机器特定路径。
- 临时数据根启动+恢复：包内 CLI 首次 `--app-data tmp/p2-cli-real\data2 models list` exit 0 并建出 `runtime/pi-agent/sessions/logs/exports/desktop.db`；全新进程对同一根 `sessions list` 返回持久条目 `c8552079b1df4958bfa4c4d2afe71929`，`sessions show <id>` 返回 `leafId=841ce1b5 totalEntries=1`，`settings get theme` → `{"theme":"system"}`。
- 四种启动器 + 空格路径夹具：`pwsh -File scripts\cli-package-verify.ps1 -CliPublishRoot out\build\cli -AppPublishRoot out\build\app` exit 0，`Launcher checks passed: 17`（含 `Fufucode.cmd --selftest` 与缺运行时负例 exit 3）。
- 包内硬编码检查（只针对打包内容，不做全仓防错扫描）：`app/`、`cli/`、`runtime/`、`extensions/`、`notices/`、清单、启动器、README 的 26 个非 `node_modules` 文本文件与 `node_modules/.bin` 脚本，均无 `C:\Users\mio`、旧机器 `21894`、`.codex\worktrees`、`Projects\Fucode` 痕迹；`scripts/packaging/*.ps1`、`Fufucode.iss`、`scripts/orchestrator.mjs` 亦无旧硬编码路径（production-delivery §2.1-C 的前置条件已满足）。

### 5.3 干净环境安装 / 启动 / 卸载（Windows Sandbox，未触碰宿主）

`pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\packaging\run-sandbox.ps1 -AppVersion 1.4.2-candidate`，`.wsb` 关网络、关剪贴板、`C:\Payload` 只读、`C:\Evidence` 可写。宿主 `C:\Users\mio\...` 安装区从未执行本产品的安装或卸载。

最终提交态脚本实跑（继承 `HTTP_PROXY=127.0.0.1:7897`、无 `NO_PROXY`，由补丁自行加环回豁免）exit 0，`Sandbox acceptance: PASS`，guest 身份 `WDAGUtilityAccount` / `Virtual Machine` / `NT 10.0.26100.0`，证据 `tmp/sandbox-efe47a8b027d4241819bbca2b6d4acf7\evidence\`（`complete.txt=PASS`、`results.json`、`install.log` 6,513,639 B、`uninstall.log` 2,983,349 B、`sandbox-info.json`）。12/12：

| 检查 | 结果 | 逐项证据（detail） |
|---|---|---|
| clean-image | PASS | 无历史安装，一次性 Sandbox 账户 |
| install | PASS | setup `/VERYSILENT` exit 0，`{localappdata}\Programs\Fufucode\app\Fufucode.App.exe` 存在 |
| silent-start-menu-shortcut | PASS | 快捷方式指向包内启动器，工作目录为用户 Documents |
| session-protocol-registration | PASS | `HKCU\Software\Classes\fufucode` 指向已安装 App，无运行时覆盖参数 |
| integrity | PASS | `checked=15542;changed=0` |
| **gui-start（安装后启动）** | PASS | 自包含 exe 创建原生顶级窗口（30 s 内 `MainWindowHandle != 0`） |
| **gui-exit** | PASS | `CloseMainWindow` 后 15 s 内退出 |
| bundled-pi-state | PASS | 包内 Node+Pi `get_state` 成功；无全局 Node/Pi、无开发检出 |
| bundled-pi-stop | PASS | 关 stdin 优雅退出，`graceful=True exit=0` |
| **uninstall（卸载）** | PASS | `unins000.exe /VERYSILENT` exit 0，App 可执行文件不再存在 |
| uninstall-shortcut-and-protocol | PASS | 安装器拥有的快捷方式与协议键已清除 |
| preserve-user-data | PASS | 卸载后 `%LocalAppData%\Fufucode` 标记文件内容保持 |

同一固定来源在同一主机重复可得：本轮共 4 次完整 PASS（`tmp/sandbox-bf1de72f…`、`tmp/sandbox-d5bd45ea…`、`tmp/sandbox-cdc5034a…`、最终 `tmp/sandbox-efe47a8b…`）。历史 D0/R-009 Sandbox 失败未被当作本轮结论，全部为本轮新实跑与新诊断。

## 6. OMP 依赖的真实记录（不假称干净机器双后端可用）

- 包内**不含** OMP：`out/build/runtime/omp` 不存在，`runtime/omp/` 在仓库与工作树均不存在。
- 代码事实：`RealSessionBootstrap.ResolveOmpRuntime` 默认取 `<runtime root>/runtime/omp/omp.exe`，可被 `--omp-exe` 覆盖；`PersistentSessionManager.cs:433` 在该文件缺失时抛 `FileNotFoundException("OMP runtime missing. Configure --omp-exe with the verified OMP 18.8.5 executable.")`。
- 打包链未纳入 OMP，因此本候选包在**未提供外部 OMP 可执行文件的机器上只有 Pi 后端可用**。OMP 属外部/宿主依赖（本机 `C:\Users\mio\.bun\bin\omp.exe` 存在），必须在安装时另行提供路径或另行打包，属发布范围决策，不在本包允许路径内擅改。
- Sandbox 与 clean-start 验收均只证明 Pi 后端；未声称双后端在干净机器可用。

## 7. 已实现 / 实际跑过 / 未验 / 阻塞

已实现：固定来源候选 ZIP、版本化安装程序、包内完整性清单与许可 notice 载荷、2 项打包脚本修复（各一小步提交）。

实际跑过并取证：解决方案构建 0 警告 0 错误；App 全量 `--selftest` 0 失败；Core 336/Pi 21 定向套件 exit 0；ZIP 归档 15,542 校验；解压后 clean-PATH 自测；包内 Pi 两次 `get_state` 与优雅停止；包内完整性 Verify；包内桥接显式加载；临时数据根启动+跨进程恢复；4 个启动器（含空格路径）；安装→原生窗口启动→关闭→卸载→用户数据保留（Sandbox 12/12）；打包内容硬编码路径定向检查。

未验（不得当通过）：

1. 原生 IME 组合态、系统 DPI/非 100% 缩放、多显示器布局（本包无 GUI 交互取证，Sandbox 仅证明窗口创建/退出）。
2. 代码签名与 SmartScreen/杀软行为；自动 Pi 升级（README 自述未验）。
3. 升级安装（已有 1.4.x 安装上覆盖）、卸载后重装、跨版本迁移路径。
4. OMP 后端在包内可用（§6）。
5. 真实云端模型对话闭环（本轮零云调用，`--selftest` 与 Pi 查询为本地/合成路径）。
6. WebView2 真实页面渲染（目标机需 Edge WebView2 Runtime，包内未验）。

阻塞：无外部阻塞需要用户决策；§8-3 为环境注意项，不阻塞候选包交付。

## 8. 环境与非缺陷观察（不改代码，交主控判断）

1. **`core.autocrlf=true` 使检出的 `runtime/node/LICENSE` 与 provenance 记录的 SHA256 不一致**（CRLF/LF；git blob 一致）。打包按磁盘字节复制，包内 LICENSE 为 CRLF 版。属本机 git 配置与跟踪字节的换行策略，不是产品回归；若要包内许可文本逐字节等于上游，需 `.gitattributes` 固定该路径为 `-text`，涉及跟踪文件与打包语义，超出本包允许范围，留主控决定。
2. **`package.ps1` 默认 `-AppVersion 1.4.0`**，本轮显式用 `1.4.2-candidate` 以避免与既有 1.4.0/1.4.1 产物和安装身份混淆。版本号策略由主控/发布决定，本包未改脚本默认值。
3. **Windows Sandbox 单实例驻留**：一次被中断的运行会遗留 `WindowsSandboxServer` 实例，后续 `wsb start/connect` 永不就绪，表现为“登录未就绪”而非真实安装失败。运维建议：验收前 `wsb list` 应为空。本轮未改脚本以规避它（未证实的“修复”不入库，见 §4）。
4. `Fufucode.Hosting.Tests.exe` 于 21:21 在宿主触发 `CLR20r3 / System.IndexOutOfRangeException` WER 崩溃报告（Application 日志）。该测试属 B1（`C:\Users\mio\.codex\worktrees\8bf3`）范围，本会话未运行它，仅登记事实供 B1/主控核对。
5. 本工作树 `out/` 下 243 个历史二进制工件仍处于 Git 跟踪状态（`.gitignore` 早于跟踪添加）。本轮遵循“不提交二进制”的要求，未提交这些改动，`git status` 中保留为未提交变更，交主控决定是收编新工件还是停止跟踪。

## 9. 公共接线需求（本包不改共享入口）

- 无强制接线。若主控要把固定候选包发布位写入权威进度/决策，需要：本记录 §5.1 的两条 SHA256 与来源 SHA 组合、`e64dd1b + 43dd644/c120c96` 的构建可复现说明、以及 §6 的 OMP 依赖决策（外部 `--omp-exe` 或纳入打包）。
- 打包脚本变更全部落在 `scripts/packaging/**`（本包允许路径），不改 `src/**`、`Fufucode.sln`、`docs/progress.md`、`docs/decisions.md`。

## 10. 交付卡

| 项 | 内容 |
|---|---|
| 任务 | P2 固定版本桌面候选包（`docs/tasks/hourly-P2-candidate.md`，L3，推荐 `group/auto-qfmodel`） |
| 状态 | **候选包交付 / 提交待验收**（非生产正式发布，非 G3/R 通过） |
| 工作树 | `C:\Users\mio\.codex\worktrees\126c\Fufucode`，detached HEAD |
| 开工 HEAD | `1d0b53ac00fc5113656bdb7777b380e4755ba133`（开工时干净，无继承未提交内容） |
| 固定产品来源 | `e64dd1b15e1cfdc1de99afae7d4cc3c32c2f047e` |
| 本任务提交 | `43dd64448fc25dab48c1739fa153aaac89d47bab`、`c120c96d33035d42410dd112b759960b521bc48b` |
| 提交范围 | `43dd644` = `fix(packaging): exit codes in clean-start and drill work on Windows PowerShell 5.1`（`scripts/packaging/verify-clean-start.ps1`、`scripts/packaging/run-drill.ps1`，各 1 行）；`c120c96` = `fix(packaging): bypass inherited HTTP proxy for the wsb loopback gRPC endpoint`（`scripts/packaging/run-sandbox.ps1`，+7）。本文档另成一条记录提交（自引用，不写自身 SHA） |
| 改动文件 | `scripts/packaging/verify-clean-start.ps1`、`scripts/packaging/run-drill.ps1`、`scripts/packaging/run-sandbox.ps1`、`docs/records/P2-candidate.md`、`labs/packaging/clean-start-report.jsonl`（本轮实跑报告，任务相关）；未提交 `out/**` 二进制 |
| 候选产物 | `C:\Users\mio\.codex\worktrees\126c\Fufucode\out\Fufucode.zip` — 186,277,820 B — `a8c2033c49548070510d598c55bc55ca4f3ccf13f8d9b02b663aff2e340cbbcd`；`C:\Users\mio\.codex\worktrees\126c\Fufucode\out\Fufucode-1.4.2-candidate-setup.exe` — 140,557,907 B — `a80e710c18f8ccfa5511da7595b1148e1e01709d91b988a715e93693e98e80d7`；包内清单 `integrity.sha256.json` — `2aa086b47c0019d109bd5ef7f88624396b74919605f1fa3fd7fcbb30e0da658e`（15,542 条） |
| 运行时来源 | Node `v24.15.0` `3331e1ff…9b4a5`（provenance 匹配）；Pi `@earendil-works/pi-coding-agent@1.1.0`，`cli.js e79626f2…926774`，复制自 `C:\Projects\Fucode\Fufucode@bf4cce6ce9b90a58ed6927710763d6f20edd8a63`；OMP 未打包（§6） |
| 实际命令与结果 | 见 §3（构建/全量自测/Core/Pi，全部 exit 0）、§5.2（`verify-clean-start.ps1` exit 0 PASS、`integrity.ps1 -Mode Verify` ok、`cli-package-verify.ps1` 17 项、桥接 `get_state success=true`、临时根跨进程恢复）、§5.3（`run-sandbox.ps1 -AppVersion 1.4.2-candidate` exit 0，Sandbox 12/12 PASS） |
| 证据位置 | `tmp/`：`build-sln.log`、`selftest-full.out.txt`、`core-tests.log`、`pi-tests.log`、`package-p2.log`、`clean-start-p2-pwsh.log`、`clean-start-p2.log`（5.1 失败原样留证）、`sandbox-p2-*.log`；`tmp/sandbox-efe47a8b027d4241819bbca2b6d4acf7/evidence/`（`results.json`/`install.log`/`uninstall.log`/`complete.txt`）；`labs/packaging/clean-start-report.jsonl`；`tmp/clean-start-report.pre-p2.jsonl.bak`（继承报告备份） |
| 未运行验证与原因 | §7 未验 6 项：需原生输入/DPI 工具或代码签名证书、需既有安装环境、需真实凭据与授权、需 WebView2 目标机场景；均超出本包允许范围或属外部条件 |
| 公共接线需求 | §9，无强制接线；版本策略与 OMP 打包决策留主控 |
| 继承改动保护 | 开工时工作树干净；主区 `C:\Projects\Fucode\Fufucode`（main `bf4cce6`）未做任何写入；`reference/` 未触碰；未改用户数据、未改全局运行时/代理配置 |
| 未做 | 未同步/集成/改写 main，未 push，未改写历史，未清理工作树，未删任何 worktree/会话/记忆/备份，未登录、未采购、未公开部署，未启动/停止他人会话，未自行开下一任务 |
| 需要协调 | 否。§8 三项（换行策略、`out/` 跟踪工件、Sandbox 单实例驻留）与 §6 OMP 打包决策可由主控在收编时一并裁定 |
| 原会话是否仍在写代码 | 否 |

## 11. 停止写入声明

本文档提交后即**停止写入**：不再修改代码、不再提交、不再动工作树状态（保留 `out/` 候选产物与 `tmp/` 证据目录原样，不清理）。等待主控验收/返工指令；不自行开始下一任务。
