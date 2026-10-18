# 生产安装包符号分离、App/CLI 运行库共享与真实插件迁移交付记录

- **日期**：2026-10-16
- **执行角色**：主控 M (`01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07`)
- **前置依据**：根据用户最新直接授权及独立核查（`docs/records/payload-size-purpose-audit-2026-10-16.md`），重点落实：
  1. 将 101.82 MiB 的调试符号（PDB）彻底分离至独立符号归档 `FufucodeNative-Symbols.zip`，不随面向用户安装包发布；
  2. 移出 SDK 开发说明文档 XML（如 `WebView2.Core.xml` 0.584 MiB）；
  3. 彻底解决 App 与 CLI 间 198 份同路径同内容文件的重复打包（消除 80.78 MiB 重复占用），让两者共享单份 self-contained 运行底座；
  4. 真实落地 AutoDev 与 AeroSkin 插件实现（工具调度与皮肤贡献）；
  5. 严守“免另装 .NET 运行时”的承诺，保证用户开箱即用。

---

## 一、核心产物瘦身前后实测数据对比

| 产物 / 指标 | 优化前实测 (a82fec7) | 优化后实测 (当前 HEAD) | 缩减率 / 优化幅度 | 关键改进细节 |
|---|---:|---:|---:|---|
| **安装包 `Fufucode-Native-Setup.exe`** | 82,797,609 B (78.96 MiB) | **38,622,577 B (36.83 MiB)** | **-53.35% (减小 42.13 MiB)** | PDB 符号剥离 + CLI 重复文件消除，Setup 体积减半 |
| **发布 ZIP `FufucodeNative.zip`** | 116,535,152 B (111.14 MiB) | **51,630,669 B (49.24 MB)** | **-55.70% (减小 61.90 MiB)** | 仅含真实运行依赖，247 项清单文件完整性哈希全绿 |
| **解压工作目录 `build-native/`** | 313,130,014 B (298.62 MiB) | **121,260,378 B (115.65 MiB)** | **-61.27% (消除 182.97 MiB)** | 逼近理论数学去重下界 114.75 MiB，且 100% 经实测验证 |
| **其中 `cli/` 目录体积** | 85,180,853 B (81.23 MiB) | **24,196 B (23.63 KiB)** | **-99.97% (消除 81.21 MiB)** | 共享 `app/` 运行库，仅保留 22KB 原生转发器与批处理脚本 |
| **独立符号归档 `FufucodeNative-Symbols.zip`** | 无（直接混入发布包） | **27,126,486 B (25.87 MiB)** | **新增独立符号包** | 完整保留 101.46 MiB PDB 调试符号供开发者与崩溃诊断使用 |

---

## 二、架构改造与工程落地事实

### 1. PDB 符号分离与 SDK 开发文档剥离
- 在 `scripts/packaging/package-native.ps1` 打包流程中，在生成清单与归档前，遍历抽取 `$packageRoot` 内所有 `*.pdb` 调试符号文件；
- 符号文件保留相对目录结构并压缩至独立 `out/FufucodeNative-Symbols.zip`（25.87 MiB）；
- 清除 `Microsoft.Web.WebView2.Core.xml`（0.584 MiB）等纯开发说明文件；
- 保证面向用户的默认发布包仅包含运行时必需二进制。

### 2. App 与 CLI 共享原生运行库布局
- **共享运行时底座**：`dotnet publish` 将 `Fufucode.App` 与 `Fufucode.Cli` 发布至同一共享目录 `app/`；
- 两者共享单份 .NET 10.0 CoreCLR、BCL 类库（`System.*`）、`Fufucode.Core` 及原生图形库，彻底消除 198 份同路径同内容文件的重复；
- **轻量 PATH 转发器**（`tools/fufucode-cli-forwarder/`）：
  - 编写并编译 22 KiB 原生 C 转发器 `fufucode.exe`，继承当前控制台标准 I/O 句柄，透明唤起 `..\app\Fufucode.Cli.exe` 并传递全部命令行参数；
  - 彻底避免传统 `.cmd` 脚本在捕获 Ctrl+C 时弹出 `Terminate batch job (Y/N)?` 的交互缺陷；
  - `cli/` 目录增加同名 `.cmd` 兼容脚本，确保 Windows CMD、PowerShell、Git Bash 及终端中直接键入 `fufucode` 均无缝工作；
  - 根目录 `Fufucode.cmd` 与 `Fufucode.ps1` 同步更新指向 `app\Fufucode.Cli.exe`。

### 3. AutoDev 与 AeroSkin 插件真实现迁移
- **`src/Fufucode.Plugins.AutoDev/`**：
  - 新增 `AutoDevToolHandler.cs`（实现 `IPluginToolHandler`），对外暴露 `autodev_status`、`autodev_plan`、`autodev_advance` 三大真实工具契约；
  - 在 `PluginCapability` 中正式注册 `AutoDevelopment` 枚举，并与 `PluginCapabilityRegistry` 双向映射；
  - `AutoDevPluginPackage` 真实实现 `CreateToolHandler`。
- **`src/Fufucode.Plugins.AeroSkin/`**：
  - 将 Aero 皮肤资源（`AeroTokens.axaml`、`AeroCaption.axaml`、`AeroStyles.axaml`、`SkinShellStyles.axaml`）纳入插件工程 `Themes/`；
  - `AeroSkinPluginPackage` 真实实现 `CreateSkinContribution`，提供 `aero` 皮肤定义与资源 URI。

---

## 三、实测验证结果

1. **全解决方案强编译门禁**：
   ```powershell
   dotnet build Fufucode.sln --no-restore /warnaserror
   ```
   - 30 个工程全部通过编译，**0 警告 · 0 错误**。

2. **后端与插件能力测试**：
   ```powershell
   dotnet run --project tests/Fufucode.App.Backend.Tests
   ```
   - **全部通过：662 项断言全绿**（包含 AutoDev 工具动态注册/调用验证、AeroSkin 皮肤贡献与生命周期断言）。

3. **发布包双入口实机验证**：
   - `out/build-native/cli/fufucode.exe --help`：原生转发器响应正常，命令行菜单完整输出；
   - `out/build-native/cli/fufucode.cmd settings get`：命令行脚本读取设置正常；
   - `out/build-native/Fufucode.cmd settings get`：根目录入口读取设置正常；
   - `out/build-native/integrity-native.sha256.json`：247 个文件校验全通过。

4. **更新清单自动同步**：
   - `out/feed.json` 自动更新为最新 `Fufucode-Native-Setup.exe`（38,622,577 字节，SHA-256 `4ABCF9EE18A67A8307017C241FC5A300C8DC3A48648FB2BAC1324E3A3D32999F`）。
