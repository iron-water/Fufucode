# 主程序安装更新与插件共享依赖瘦身交付记录

- **日期**：2026-10-16
- **执行角色**：主控 M (`01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07`)
- **授权来源**：用户直接授权（线程 `01a12596-9397-7132-b8c9-095b38fa726f`）：“那就实现一下”，包含主程序正式更新、设置页市场真实包管理、更新失败回滚、共享依赖按需下载与依赖池合并。
- **状态**：全量实现完成，全量自测 0 失败，全量构建 0 警告 0 错误（/warnaserror）。

---

## 一、实测产物与体积事实对比

### 1. 独立插件分发包瘦身对比（去重复用宿主基础程序集）

| 插件标识 | 瘦身前体积 | 瘦身后体积 | 缩减率 | 物理载荷特征 |
|---|---:|---:|---:|---|
| `agent.browser-use` | 17,769,655 B (16.95 MiB) | **17,150 B (16.75 KiB)** | **99.90%** | 剥离宿主 Core/Pi/Avalonia/Sqlite 及多平台 runtimes，仅保留插件实现与 manifest |
| `agent.computer-use` | 17,777,793 B (16.95 MiB) | **25,758 B (25.15 KiB)** | **99.85%** | 仅含原生截屏/键鼠控制实现与契约 |
| `service.speech-synthesis` | 17,768,464 B (16.95 MiB) | **16,429 B (16.04 KiB)** | **99.91%** | 纯托管语音适配实现，无额外冗余依赖 |
| `service.remote-control` | 17,842,376 B (17.02 MiB) | **90,335 B (88.22 KiB)** | **99.49%** | 含 WebSocket/HTTP/PIN/QR 自包含通信 |
| `automation.cron` | 17,766,018 B (16.94 MiB) | **13,982 B (13.65 KiB)** | **99.92%** | 定时任务调度器独立实现 |
| `skin.aero` | 19,394,634 B (18.50 MiB) | **19,347 B (18.89 KiB)** | **99.90%** | 剥离 WebView2 间接传递引用，纯 XAML 样式 |
| `automation.auto-dev` | 17,817,814 B (16.99 MiB) | **11,377 B (11.11 KiB)** | **99.94%** | 纯托管自动化扩展包 |
| **七大插件合计** | **126,136,754 B (120.29 MiB)** | **194,378 B (189.82 KiB)** | **99.85%** | **总体积减少 120 MiB+，缩减至原体积千分之一** |

### 2. 主安装包与官方更新清单

- `out/Fufucode-Native-Setup.exe`：82,797,609 字节（78.96 MiB）
  - SHA-256：`5B6EC2CABD7A66421F4A83F9EDEDCC81A20118D2109C3D9E4C3A371AA53AE2C6`
- `out/FufucodeNative.zip`：116,535,152 字节（111.14 MiB）
- `out/feed.json`（官方更新清单文件）：
  - 自动输出真实版本 `1.4.0`、发布时间戳、更新说明、相对路径、SHA-256 与 `signaturePolicy: "Unsigned"`。

---

## 二、架构实现与接缝清单

### 1. 主程序正式更新链路 (`Fufucode.Core.Updates`)

- `AppUpdateModels.cs`：
  - `AppUpdateManifest`：声明版本、更新日志、安装包 URL、SHA-256、包大小、最低兼容版本及签名政策；
  - 明确未签名政策：`IsUnsigned == true` 时输出 `未签名更新包：由 SHA-256 哈希校验完整性，不伪造数字签名`；
  - `UpdateCheckResult`、`UpdateProgressInfo`、`UpdatePendingState`、`UpdateInterruptedState`、`UpdateApplyPlan`。
- `AppUpdateService.cs`（实现 `IAppUpdateService`）：
  - 彻底摆脱 `git` / `gh` / `node` 外部 CLI 依赖，支持本地文件路径、file URI 及 HTTP/HTTPS 远程源；
  - 下载流式分片，实时计算 SHA-256 哈希；一旦哈希不匹配立即删除暂存文件并抛出 `InvalidDataException`；
  - 在途安全检查：检测到后台写入任务正在运行时拒绝强杀并返回 `UpdateApplyPlan.Blocked(...)`；
  - 更新挂起与中断回滚：支持落盘 `pending-update.json`，支持下次启动捕获 `interrupted.json` 向用户解释中断原因，并在升级成功后自动清理。
- `SettingsPageViewModel.Updates.cs` 与 `SettingsPage.axaml`：
  - 在设置「关于」页面新增「软件更新」控制卡片：
    - 检查更新、版本说明、未签名合规政策说明；
    - 下载更新按钮、取消下载、下载进度条；
    - 立即安装并重启按钮（安全拉起独立安装器并退出主程序）；
    - 启动时自动检查更新（可配置开关）及中断回滚提示横幅。
- `MainWindow.Actions.cs`：
  - `CheckUpdatesAsync` 重构为直接调用 `AppUpdateService.CheckForUpdatesAsync`，摆脱 git 依赖。

### 2. 插件真实安装更新与共享依赖池 (`Fufucode.Core.Plugins`)

- `PluginDependencyStore.cs`：
  - 外部依赖按需拉取至 `<plugins>/.shared-packages/<packageId>/<version>/`；
  - 引用计数机制：多个插件引用同一版本共享依赖时仅部署一份；
  - 卸载时仅递减引用，引用归零时才物理删除物理文件；
  - 哈希校验 fail-closed：损坏或篡改的依赖拒绝入池并报错。
- `PluginPackageManager.cs`：
  - 新增 `UpdatePackage`：备份旧版本、停用旧版、部署新版并拉取新依赖；
  - **原子回滚保证**：若更新过程出现依赖缺失、损坏或激活失败，立即自动从备份恢复旧版文件并重新激活，绝不产生损坏态；
  - 安装入口 fail-closed：无有效物理载荷时直接报错，禁止无源虚构 manifest 并假标已安装。
- `SettingsPageViewModel.Features.cs`：
  - 修复 `PluginItemViewModel` 的 `partial` 关键字缺失；
  - `InstallOrUninstallPlugin` 与 `TogglePluginEnabled` 真实直通 `PluginPackageManager`，异常如实向 UI 呈现，绝不静默吞异常。

---

## 三、真实验证命令与结果证据

1. **强编译门禁**：
   ```powershell
   dotnet build Fufucode.sln --no-restore /warnaserror
   ```
   - 结果：**30 个工程全部编译通过，0 警告 · 0 错误**（用时 2.99 秒）。

2. **后端与更新全链路测试**：
   ```powershell
   dotnet run --project tests/Fufucode.App.Backend.Tests
   ```
   - 包含新增的 `PluginDependencyTests`（依赖池去重、引用计数、GC 回收、更新回滚）与 `AppUpdateServiceTests`（清单检查、哈希防篡改、安全避让、挂起及中断回滚解释）；
   - 结果：**全部通过：648 项断言通过，0 项失败**。

3. **应用全量无头自测**：
   ```powershell
   dotnet run --project src/Fufucode.App -- --selftest
   ```
   - 结果：**自测结束：0 项失败**（涵盖 Markdown、工作台、终端选择、皮肤切换、设置导航与插件收据全链路）。

4. **原生打包流水线验证**：
   ```powershell
   powershell -File scripts/packaging/package-native.ps1
   ```
   - 结果：
     - `out/Fufucode-Native-Setup.exe` 构建成功（82,797,609 字节）；
     - `out/FufucodeNative.zip` 归档成功（116,535,152 字节，451 项哈希校验全部通过）；
     - `out/feed.json` 自动生成；
     - 7 大独立插件瘦身完成，单包压缩后均在 11 KiB ~ 88 KiB 范围内。

---

## 四、未验证与留待项说明

1. **真实代码签名证书**：当前开发与打包环境未配置商业 Authenticode 代码签名证书，更新清单与 UI 明确标记为 `signaturePolicy: "Unsigned"` 并严格通过 SHA-256 哈希校验完整性，未伪造数字签名。
2. **公网 CDN 部署**：当前更新源基于本地离线/可配置 URL 运行，生产环境发布源 URL 留待上线部署时配置。
