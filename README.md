# Fufucode

面向 AI 编程 agent 的 Windows 桌面客户端，基于 C# / Avalonia 构建。

默认内核为 **FUMO**（纯 C# 实现，零外部运行时依赖），Pi、OMP、Claude Code、Codex、DeepSeek Harness 等可作为后端插件按需接入。

界面以会话为中心：流式时间线、工具卡片、审批门、内置浏览器、终端、文件预览与 Git 面板。

## 环境要求

- Windows 10+（x64）
- 开发构建需要 .NET 10 SDK；自包含安装包的最终用户无需任何依赖，精简安装包需要 .NET 10 Runtime
- 可选：Node、Bun、Python、Git Bash 或任意后端 CLI——Fufucode 直接使用已安装的，缺失时才提示下载

## 构建与运行

```bash
dotnet build Fufucode.sln -c Release
```

本地开发部署：

```bash
pwsh fucode/publish-local.ps1
```

## 打包

```bash
pwsh scripts/packaging/package-native.ps1 -AppVersion 1.4.0                     # 自包含
pwsh scripts/packaging/package-native.ps1 -AppVersion 1.4.0 -FrameworkDependent # 依赖 .NET 10
```

| 安装包 | 体积 | 依赖 |
|---|---:|---|
| `Fufucode-Native-Setup.exe` | ~19.5 MB | 无 |
| `Fufucode-Native-Slim-Setup.exe` | ~12.2 MB | .NET 10 Runtime |

## 目录结构

| 路径 | 内容 |
|---|---|
| `src/Fufucode.Core` | 会话、持久化、信任、diff、更新、插件宿主 |
| `src/Fufucode.App` | Avalonia UI 外壳 |
| `src/Fufucode.Cli` | 无头命令行 |
| `src/Fufucode.Plugins.*` | 后端与功能插件 |
| `tools/` | 原生启动器与 CLI 转发器 |
| `scripts/packaging/` | 打包与工具链脚本 |
| `tests/` | 可执行验证套件 |
| `android/` | 移动客户端 |
| `TestApp/`、`labs/` | 早期脚手架与历史实验，非产品入口 |

## CLI 速查

```bash
fufucode "<prompt>"           # 一次性对话
fufucode sessions list        # 会话列表
fufucode runtime status       # 运行时与后端状态
fufucode plugins status       # 插件状态
```

## 状态

活跃开发中。FUMO 为默认内核，后端插件按需安装。许可证信息见 `THIRD-PARTY-NOTICES.md`。
