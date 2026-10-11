# 许可合规清单（M7 发布包）

> 生成方式：`scripts/packaging/package.ps1` —— `dotnet publish` self-contained win-x64（App + Cli 双发布）+ `npm ci --prefix runtime/pi --ignore-scripts`（`runtime/pi/package-lock.json` 固定）+ 开发机精确 Node 可执行文件按字节复制。本清单为**已分发**组件的范围；`reference/UniGetUI` 为内部参考克隆，未打包、未分发。
> 更新日期：2026-10-08（1.4.0 生产收口；最终包是否已包含补充文本以本轮打包记录为准）

## 1. 应用自身的 .NET 发布（`app/` 与 `cli/`，自包含）

`cli/` 与 `app/` 共用同一套 .NET 运行时与第三方包（`Fufucode.Cli` 引用 `Fufucode.Core`/`Fufucode.Pi`），下表对两者均适用。

| 组件 | 来源 | 版本 | 许可 | 是否分发 |
|---|---|---|---|---|
| 应用代码（`Fufucode.App`、`Fufucode.Cli`、`Fufucode.Core`、`Fufucode.Pi`、`Fufucode.App.Abstractions`） | 本项目 | — | 本项目的许可（由仓库根 LICENSE 约定，当前根目录无 LICENSE 文件，**待补充**） | 是 |
| .NET 运行时（`app/`、`cli/` 内自包含 native 与 BCL） | Microsoft（构建 SDK 10.0.401） | 10.0.12（实际 System.Private.CoreLib 产品版本） | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)（含部分 Apache-2.0 第三方可执行文件，运行时已含 `THIRD-PARTY-NOTICES.TXT`） | 是 |
| Avalonia 及子包（`Avalonia`、`Avalonia.Desktop`、`Avalonia.Themes.Fluent`、`Avalonia.Fonts.Inter`、`Avalonia.Headless`、`Avalonia.Skia`、`Avalonia.Win32`、`Avalonia.Win32.Automation`、`Avalonia.X11`、`Avalonia.FreeDesktop`、`Avalonia.FreeDesktop.AtSpi`、`Avalonia.Native`、`Avalonia.Remote.Protocol`、`Avalonia.HarfBuzz`、`Avalonia.Metal`、`Avalonia.MicroCom`、`Avalonia.OpenGL`、`Avalonia.Vulkan`、`Avalonia.Markup`、`Avalonia.Markup.Xaml`、`Avalonia.Controls`、`Avalonia.Dialogs`、`Avalonia.DesignerSupport`） | AvaloniaUI | 12.1.3 | MIT | 是 |
| `Avalonia.Angle.Windows.Natives`（`av_libglesv2.dll`） | AvaloniaUI | 2.1.27548.20260419 | MIT（ANGLE/Blink 原生许可随包分发） | 是 |
| `Avalonia.BuildServices` | AvaloniaUI | 11.3.2 | 仅构建时依赖，**不进入发布产物** | 否 |
| `CommunityToolkit.Mvvm` | .NET Foundation | 8.4.2 | MIT | 是 |
| `SkiaSharp` / `HarfBuzzSharp` 及 NativeAssets（Win32/macOS/Linux/WebAssembly） | Redth / Xamarin | 3.119.4 / 8.3.1.3 | MIT | 是（win-x64 发布只含 Win32 native assets：`libSkiaSharp.dll`、`libHarfBuzzSharp.dll`；其余为跨平台包声明） |
| `MicroCom.Runtime` | AvaloniaUI | 0.11.6 | MIT | 是 |
| `Tmds.DBus.Protocol` | tmds | 0.94.1 | MIT | 是 |
| `Microsoft.Data.Sqlite.Core` | Microsoft | 10.0.12 | MIT | 是 |
| `SQLitePCLRaw`（`bundle_e_sqlite3`、`core`、`lib.e_sqlite3`、`provider.e_sqlite3`，含 `e_sqlite3.dll`） | SQLitePCLRaw contributors | 2.1.12 | Apache-2.0（nuspec `<license type="expression">Apache-2.0`）；SQLite 本体为 Public Domain | 是 |
| `Microsoft.Web.WebView2`（E1 内置浏览器；`Microsoft.Web.WebView2.Core.dll` + `WebView2Loader.dll`） | Microsoft | 1.0.4258.31 | BSD-3-Clause（已核对包内 `LICENSE.txt`：允许源码/二进制再分发，须保留版权声明与免责声明；另含 `NOTICE.txt` 第三方声明） | 是 |

> 注：`dotnet publish --self-contained` 已将运行所需的全部程序集与 native 文件平铺到 `app/`、`cli/`；NuGet 层面的 `licenses` 字段在部分包中缺失（`project.assets.json` 不携带 license），上表以 nuspec/`LICENSE.txt` 可读字段为准。完整 NuGet 包清单见 `src/Fufucode.App/obj/project.assets.json` 的 `libraries` 节。

## 2. Pi 运行环境（`runtime/`）

### 2.1 Node 运行时（`runtime/node/node.exe`）

| 项目 | 值 |
|---|---|
| 版本 | v24.15.0 |
| 来源 | 开发机已安装的精确版本（`Get-Command node`），按字节复制；SHA256 前缀 `3331e1ffe19874215472217c`（与 `%ProgramFiles%\nodejs\node.exe` 同值） |
| 许可 | 从 Node 官方 v24.15.0 固定 tag 获取完整 LICENSE，含第三方声明，打包步骤复制到 runtime/node/LICENSE；SHA256 为 4573185d56580da2b890ba34a85a409257640f1c5632eade4300137266194d18 |
| 来源证据 | [Node.js v24.15.0 许可](https://github.com/nodejs/node/blob/v24.15.0/LICENSE)，git blob 403ea3c5717087b20e9cddaa2b4881256b6b4858；runtime/node/LICENSE.provenance.json 保存对应二进制及许可 SHA |

### 2.2 Pi 包与其依赖（`runtime/pi/`）

| 包 | 版本 | 许可 | 分发 |
|---|---|---|---|
| `@earendil-works/pi-coding-agent` | **1.0.0**（作者 Mario Zechner） | **MIT** | 是（`node_modules` 闭包；CLI 位于包内 `dist/bundle/cli.js`，不重复复制顶层 `dist/`） |

| 许可 | 包数 | 包（`runtime/pi/node_modules` 全树，含 3 处嵌套目录） |
|---|---|---|
| 0BSD | 1 | tslib |
| Apache-2.0 | 35 | @aws-sdk/client-bedrock-runtime、@aws-sdk/core、@aws-sdk/credential-provider-env、@aws-sdk/credential-provider-http、@aws-sdk/credential-provider-ini、@aws-sdk/credential-provider-login、@aws-sdk/credential-provider-node、@aws-sdk/credential-provider-process、@aws-sdk/credential-provider-sso、@aws-sdk/credential-provider-web-identity、@aws-sdk/eventstream-handler-node、@aws-sdk/middleware-eventstream、@aws-sdk/middleware-websocket、@aws-sdk/nested-clients、@aws-sdk/signature-v4-multi-region、@aws-sdk/token-providers、@aws-sdk/types、@aws-sdk/xml-builder、@aws/lambda-invoke-store、@google/genai、@silvia-odwyer/photon-node、@smithy/core、@smithy/credential-provider-imds、@smithy/fetch-http-handler、@smithy/node-http-handler、@smithy/signature-v4、@smithy/types、ecdsa-sig-formatter、gaxios、gcp-metadata、google-auth-library、google-logging-utils、grok-mermaid、long、openai |
| BSD-3-Clause | 13 | @protobufjs/aspromise、@protobufjs/base64、@protobufjs/codegen、@protobufjs/eventemitter、@protobufjs/fetch、@protobufjs/float、@protobufjs/path、@protobufjs/pool、@protobufjs/utf8、buffer-equal-constant-time、diff、highlight.js、protobufjs |
| BlueOak-1.0.0 | 2 | lru-cache、minimatch |
| ISC | 7 | graceful-fs、hosted-git-info、isexe、semver、signal-exit、which、yaml |
| MIT | 58 | @anthropic-ai/sdk、@babel/runtime、@earendil-works/chord、@earendil-works/pi-agent-core、@earendil-works/pi-ai、@earendil-works/pi-codemode、@earendil-works/pi-coding-agent、@earendil-works/pi-mcp、@earendil-works/pi-telemetry、@earendil-works/pi-tui、@esbuild/win32-x64、@stablelib/base64、@types/node、@types/retry、agent-base、balanced-match、base64-js、bignumber.js、bowser、brace-expansion、chalk、cross-spawn、data-uri-to-buffer、debug、esbuild、extend、fetch-blob、formdata-polyfill、get-east-asian-width、http-proxy-agent、https-proxy-agent、ignore、jiti、json-bigint、json-schema-to-ts、jwa、jws、marked、ms、node-domexception、node-fetch、p-retry、partial-json、path-key、proper-lockfile、proxy-agent-negotiate、quickjs-wasi、retry、safe-buffer、shebang-command、shebang-regex、standardwebhooks、ts-algebra、typebox、undici、undici-types、web-streams-polyfill、ws |
| Unlicense | 1 | fast-sha256 |

> 合计 117 个包。

> 上表为 1.3.0 时按包名去重的声明清单，不等同当前文本完整性验收。当前 `collect-notices.ps1` 按实际包实例检查根目录许可文本，Pi 的 `node_modules` 整体随包分发，已存在文本不重复复制。缺文本实例如实报告，不按 `license` 字段制造许可文件。
> 与 0.87.1 时代的旧清单差异：`tslib` 实为 **0BSD**（旧清单误记 MIT）；新增 `@aws-sdk/*`、`@smithy/*`、`@google/genai`、`@babel/runtime`、`@earendil-works/pi-*`、`quickjs-wasi`、`chalk`、`bowser` 等依赖；`buffer-equal-constant-time` 为 BSD-3-Clause（旧清单同时列于 MIT 与 BSD-3-Clause 两行，属重复条目，已消）。

## 3. 第三方参考仓库（不在本仓库内）

- 开发过程中只读克隆过 UniGetUI 作为界面设计参考；该克隆**从未进入本仓库、未进入发布物**。
- UniGetUI 自身的许可条款不适用于本仓库或本发布物，因为**未分发其任何二进制或源码**。
- 若后续决定集成 UniGetUI 的组件，须重新评估并追加许可条目。

## 4. 合规缺口（必须在正式发布前关闭）

| # | 缺口 | 影响 | 建议 |
|---|---|---|---|
| 1 | 仓库根无 `LICENSE` 文件 | 应用自身许可不明 | 由产品所有者明确授权方案，agent 不擅自选择许可 |
| 2 | Node 许可文本已补入打包步骤 | 固定 v24.15.0 官方正文，最终载荷哈希验收单列 | 不据此宣称其他第三方缺口全部关闭 |
| 3 | 部分 npm 包根目录未找到许可文本 | 整体复制 `node_modules` 已保留其中现存文本，但声明字段不能证明文本完整 | 依据 `collect-notices.ps1` 的 npm 实例报告逐包确认；不额外复制整树或伪造文本 |
| 4 | 部分运行时 NuGet 包无可收集许可文本；已收集文本是否入最终载荷须验证 | 缓存中的 nuspec 表达式不等同许可正文 | `collect-notices.ps1` 按 App/Cli assets 的实际运行时依赖和 packageFolders 收集；报告缺口，排除仅构建包及其他平台 native 包 |
| 5 | 无代码签名 | 用户侧信任与 SmartScreen 未解决 | 购买 EV 代码签名证书，对 `Fufucode.App.exe` 与安装器签名 |
| 6 | 安装器形态：已有 Inno Setup 用户级 EXE（1.2.0/1.3.0 已演练），**无 MSI/MSIX** | 企业分发/受控环境安装体验不完整 | M7 收尾阶段评估 MSI/MSIX |
| 7 | 参考仓库与发布物边界需持续声明 | 审计时可能被误认为已分发 | 已在本文第 3 节声明；发布清单须保留 "not included" 段落 |
| 8 | WebView2 **运行时**前置未声明（E1 内置浏览器） | 包内只有 SDK 托管程序集与 `WebView2Loader.dll`；目标机需另备 WebView2 Runtime（Evergreen，通常随 Edge 安装）。安装器既不检测也不打包 Fixed Version runtime | 安装时检测 WebView2 Runtime 是否可用，或在 README/发行说明中明确该前置；缺运行时时的应用行为需实测后记录 |

## 5. 分发边界声明

本发布物（`out/Fufucode.zip` 与 `out/Fufucode-<version>-setup.exe` 的 `{app}` 载荷）仅包含：

- `app/` —— 本项目 .NET 自包含发布（net10.0，Avalonia 12.1.3，含 WebView2 SDK 程序集）
- `cli/` —— 本项目 .NET 自包含发布 CLI（`Fufucode.Cli.exe` + `fufucode.exe`/`fufucode.cmd` PATH 入口）
- `extensions/fufucode-bridge/` —— 审批/消息桥（本项目代码，仅生产扩展：`index.ts`、`package.json`、`policy.ts`；dev fixtures 不入包）
- `runtime/node/` —— Node v24.15.0 可执行文件和对应官方 LICENSE
- `runtime/pi/` —— `@earendil-works/pi-coding-agent@1.0.0`（`node_modules` + `package.json` + `package-lock.json`；唯一 CLI 为 `node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js`）
- `Fufucode.cmd` / `Fufucode.ps1` —— 启动器（设置 `PI_CODING_AGENT_DIR` 与 `--session-dir`）
- `integrity.sha256.json` —— 发布载荷的 SHA256 清单
- `README.txt` —— 使用说明与未验证项声明

**不包含**：`reference/UniGetUI`、开发机全局 npm/pi、`src/` 源码、`tests/`、`logs/`、`tmp/`、dev fixtures（`broken-fixture`、`demo-extension`、`atria-widget-lab`）、`out/build` 中清单以外的构建中间文件。

## 6. 本轮许可文本工装验证

`scripts/packaging/collect-notices.ps1 -Destination <私有输出目录>` 从脚本位置推导仓库；读取 App/Cli `obj/project.assets.json` 的 `targets`、`libraries`、`packageFolders`。只读缓存，输出保留文本原始字节与 SHA256。报告不包含用户目录绝对路径。若有 win-x64 target 优先使用该 target，否则从框架 target 筛选 Windows runtimeTargets。

2026-10-08 实跑至 `tmp/production-notices-final-audit`：28 个运行时 NuGet 包，收集 10 份文本；121 个随包 npm 实例，NuGet/npm 合计 36 个实例未找到规定位置文本。NuGet 缺文本 21 个；npm 缺根目录文本 15 个，后者不等于已证明上游未授权。报告 `report.json` 保留逐包结果。此私有工装验收不代表文本已经进入最终安装包。

`test-collect-notices.ps1` 在自建 tmp fixture 验证运行时筛选、App/Cli 去重、复制字节一致、缺文本不制造文件、npm 不重复复制、越界路径拒绝，exit 0。没有修改 NuGet 缓存、Pi 源目录或正在归档的 out 载荷。
