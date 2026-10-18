# PKG-1 原生安装包渠道 — 执行会话交付记录

日期：2026-10-15。任务书：`docs/tasks/native-kernel-plugin-marketplace.md`（主控派单向导 `docs/面向人类/next-stage-v4-roadmap.md` PKG-1，L3）。
工作树：`C:/Projects/Fucode/worktrees/pkg1-packaging`，分支 `agent/pkg1-packaging`。

## 0. 开工身份

| 项 | 值 |
|---|---|
| cwd / 工作树 | `C:\Projects\Fucode\worktrees\pkg1-packaging` |
| 开工 HEAD | `d4e36716951af09e0de011ee5f7b6273383eb19b`（= `main`，工作区干净） |
| 允许路径 | `scripts/packaging/**`、`docs/records/PKG1-native-packaging-delivery.md`；产物在 `out/`、`tmp/` |
| 未触碰 | `src/**` 产品代码、其他 `docs/`、`reference/`、用户数据、全局运行时配置 |

## 1. 结论摘要

- 安装包流水线已解耦为**双安装包渠道**：全量包 `package.ps1` + `Fufucode.iss` 保持不变；新增原生渠道 `package-native.ps1` + `Fufucode-Native.iss`。
- 原生渠道只发布 `Fufucode.App` 与 `Fufucode.Cli`（win-x64 self-contained），**不含** `runtime/node`、`runtime/pi`、`extensions/fufucode-bridge`、`notices`。
- 产出 `Fufucode-Native-Setup.exe`（**78.74 MB**）与 `FufucodeNative.zip`（114.14 MB），比全量 Setup（**134.97 MB**）小 **56.23 MB**，且秒级安装（ISCC solid 压缩在 15k→~450 文件量级上编译 89 秒，安装窗口无长解压）。
- 本机实测：原生 CLI/GUI 在**无 Node 的 PATH** 下正常启动（CLI `settings get` exit 0；打包 GUI mock 窗口 4.4s 创建并正常退出）。
- **本包状态：模块交付 / 已提交待主控审核。不称生产线/G3/R 通过**，不清算全量渠道存在性。

## 2. 为什么两侧必须分开打（事实依据）

1. 组合根把 `runtime/pi` 当硬依赖：`src/Fufucode.App/Integration/RealSessionBootstrap.cs` 的 `ResolveRuntime(options, required: true)` 在缺 Node/Pi 时抛 `FileNotFoundException`；`src/Fufucode.Cli/Program.cs` 的 `ResolveRuntime` 同理抛异常。把 Node/Pi 塞进“原生”包会把这个门槛重新带回来。
2. `runtime/`、`out/` 本被 `.gitignore` 忽略；本工作树 `runtime/` 只有 `LICENSE`/`provenance`/`package.json`/`package-lock.json`，`node.exe` 与 `node_modules` 不在版本库里。因此**任何**渠道的完整构建都需要外部补齐 `runtime/node/node.exe` 与 `runtime/pi/node_modules`——这与渠道解耦无关，是既有运行时分发约束。
3. 结论：全量渠道继续承担“Pi/OMP/DSH/Codex 插件运行时就位”的既有交付；原生渠道只交付纯 C# 内核载荷。四后端以插件身份默认启用、runtime 归插件依赖部署，属 `MKT`/`PKG-2` 范围，本次不越权接线。

## 3. 改动清单

新增：

| 文件 | 作用 |
|---|---|
| `scripts/packaging/package-native.ps1` | 原生渠道构建：App+Cli self-contained 发布 → 禁目录守卫（`runtime`/`extensions`/`notices` 一旦出现即失败）→ 原生启动器/README → `integrity-native.sha256.json` → `out/FufucodeNative.zip` → 编译 `Fufucode-Native-Setup.exe` |
| `scripts/packaging/Fufucode-Native.iss` | 独立 AppId`{7C4D2E31-5B60-4F27-9F0E-2A6C1D8B3E54}`、独立默认目录 `%LocalAppData%\Programs\Fufucode Native`、`SolidCompression=yes`、独立 PATH 记账键 `Software\Fufucode\Installer\Native`（与全量包互不卸载对方） |
| `scripts/packaging/test-native-channel.ps1` | 原生渠道回归：清单名、归档内容、Node 载荷拒绝；不触发 `dotnet publish`/`ISCC`，数秒跑完 |

修改（向后兼容，默认值不变）：

| 文件 | 改动 |
|---|---|
| `scripts/packaging/integrity.ps1` | 新增可选 `-ManifestName`（默认 `integrity.sha256.json`，原生渠道传 `integrity-native.sha256.json`） |
| `scripts/packaging/archive-package.ps1` | 同上新增 `-ManifestName`；归档时按该名排除清单自身 |
| `scripts/packaging/build-installer-version.ps1` | 新增可选 `-IssName`、`-PackageRoot`、`-NoManifest`（默认行为 = 原全量渠道） |

未修改：`package.ps1`、`Fufucode.iss`、`src/**`、`Fufucode.sln`。

## 4. 构建与验证证据（本机实测）

### 4.1 全量渠道未被破坏（重建一次）

`package.ps1`（含 `runtime/` 由主区 `C:\Projects\Fucode\Fufucode\runtime` 补齐后）完整跑通：

- `out/Fufucode.zip` 192,272,823 B（183.37 MB），15,543 条目，`runtime/pi` 15,074 条。
- `out/Fufucode-1.4.0-setup.exe` 141,523,895 B（134.97 MB）。
- 全量 Setup 的逐文件完整性校验与逐文件读哈希归档（`archive-package.ps1`）均通过。

### 4.2 原生渠道构建

| 产物 | 大小 | SHA256 |
|---|---|---|
| `out/Fufucode-Native-Setup.exe` | 82,567,905 B（78.74 MB） | `092cde8a68664661cb7ad972c712ab267054c3e9fb28d4d0a1affb52c472c404` |
| `out/FufucodeNative.zip` | 119,686,954 B（114.14 MB） | `175734d1c448d5d4013a5b89927c321b16ff66ee477603f493a42ed3397795b9` |

对比全量：Setup 体积 **−56.23 MB（−41.7%）**，载荷文件数 **451 → 全量约 15,542**（约 −97%）。

### 4.3 原生包内容与边界

`out/build-native/` 顶层：`app/`、`cli/`、`Fufucode.cmd`、`Fufucode.ps1`、`README.txt`、`integrity-native.sha256.json`。

- 完整性清单 `integrity-native.sha256.json`：**451** 条，`integrity.ps1 -Mode Verify` 返回 `{ok:true, checked:451, missing:[], changed:[], extra:[]}`。
- 禁目录检查：`runtime/`、`extensions/`、`notices/` 在载荷中**均不存在**。
- `Fufucode.cmd`/`Fufucode.ps1` 不设置 `NODE`/`PI_CLI`/`PI_CODING_AGENT_DIR`，只设 `FUFUCODE_DATA`。
- 归档 `FufucodeNative.zip` 由 `archive-package.ps1 -ManifestName integrity-native.sha256.json` 逐文件哈希校验通过（2-file 夹具测试同验证该参数路径）。

### 4.4 启动行为（本机）

| 场景 | 命令 | 结果 |
|---|---|---|
| 原生 CLI（清空 PATH，无 `node`） | `out\build-native\cli\Fufucode.Cli.exe settings get` | exit **0**，输出 `selectedModel=atria/Atria-Dawn-Preview` 等真实设置行；`Get-Command node` 为空 |
| 原生 CLI 参数校验 | `... --version` | exit 2，`Unsupported or incomplete command`（参数语义正确，非崩溃） |
| 打包 GUI mock 生命周期 | `scripts/packaging/native-start-smoke.ps1 -PackageRoot out\build-native` | `passed:true, windowCreatedMs:4445, exitCode:0` |

证据目录：`tmp/native-package-f4190b0a82a1490d87b46328f9ace465/result.json`（smoke）。

### 4.5 回归测试

| 测试 | 结果 |
|---|---|
| `test-native-channel.ps1` | PASS：原生清单名、归档内容、Node 载荷拒绝 |
| `test-archive.ps1` | PASS：共享读归档、改动源拒绝、旧归档保留、partial 清理（证明默认清单名未回归） |

## 5. 未验证 / 未执行项

> 以下均**未**在本轮验证，不得当作通过；主控如需可作为独立任务。

- 未做 `runtime/node`+`runtime/pi` **全量渠道**的干净机安装/启动（构建通过≠安装通过）；本轮只重建产物，未在沙盒重跑全量安装。
- 未做原生 Setup 的**真实静默安装/卸载**路径（`/VERYSILENT`、PATH 增删、AppId 并存）端到端验证；本轮只编译成功 + 载荷级验证。
- 未验证四后端**以插件身份**在原生包内默认启用/可安装（属 `MKT`/`PKG-2` 范围）；原生包本身仍不接线 Pi/OMP/DSH/Codex。
- 未做代码签名、未做 `Fufucode-Cli` PATH 在真实新会话中的可解析验证。
- 未跑 `PKG-2` 的“纯净环境无 Node 进程树启动与会话”完整验收。

## 6. 遗留与公共接线需求

- 原生包尚无“从市场安装 Pi 插件并下发 Node/Pi 运行时”的公共接线；本包按设计不含，需 `MKT`/`PKG-2` 在组合根补齐，不能靠拷 `runtime/` 绕过。
- 原生包与全量包同绑 `fufucode:` URL 协议，后装者覆盖前装者；原生包 URL 命令指向 `app\Fufucode.App.exe`。若需并存语义由 M 决定。
- `runtime/` 不进版本库是既有约束；CI/沙盒补 `runtime/` 的步骤由 M 决定是否纳入 PKG-2。

## 7. 提交与状态

- 本轮只提交 `scripts/packaging/{package-native.ps1,Fufucode-Native.iss,test-native-channel.ps1}` 与 `scripts/packaging/{integrity,archive-package,build-installer-version}.ps1` 的向后兼容扩展及本记录。
- `out/` 与 `tmp/` 为构建产物/证据，未纳入本次提交。
- 交付状态：模块交付、已提交待审核；生产验收 open。
