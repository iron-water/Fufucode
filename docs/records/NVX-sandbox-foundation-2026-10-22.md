# NVX 沙箱基础接入记录

日期：2026-10-22
状态：模块交付待接线
分支：feature/nvx-sandbox

## 改动

- 在 `ToolchainCatalog` 注册可选 `nvx` 工具链，安装目录为 `runtime/nvx`，不进入默认 Core 载荷。
- 新增 `NvxHostProbe`，只读检测 Windows WHP API 与 hypervisor 状态。
- 新增 `sandbox.nvx` 插件市场条目和独立 `Fufucode.Plugins.Nvx` 项目/manifest。
- 新增通用 `ISandboxExecutionService`、执行请求/结果模型。
- 新增 NVXS outer record、增量解析器、NVXC application frame 编解码和命名管道传输客户端基础。
- 新增 `fufucode runtime probe nvx [--json]`。
- 新增 7 项 NVX 协议定向断言。

## 实际验证

- `dotnet build Fufucode.sln --no-restore`：45 个项目，0 errors，0 warnings。
- `dotnet run --project tests/Fufucode.Core.Tests/Fufucode.Core.Tests.csproj --no-build -- --nvx-only`：7 项 PASS。
- `dotnet run --project src/Fufucode.Cli/Fufucode.Cli.csproj -- runtime probe nvx --json`：按预期返回退出码 3；WHP API 可加载但当前未检测到 hypervisor，`nvx-rs` 未安装。
- `git diff --check`：通过。

## 未验证项

- 未安装微软 NVX release 载荷。
- 未启动 OpenVMM/WHP 微虚拟机。
- 未执行真实 guest `EXEC`、MAPS、CANCEL、网络出口策略和挂载生命周期。
- `NvxSandboxToolHandler` 仍是明确拒绝执行的接线骨架；没有改变现有宿主 `run_command` 默认路径。

## 公共接线

- 由主控决定何时将 feature 分支合入 main。
- 后续实现需要新增 NVX runtime 下载/校验清单、OpenVMM 启动生命周期、guest filesystem/mapping 策略与执行服务实现。
- 当前 feature 未推送，未修改 main。

## 停写

源码与验证已停止写入；未启动持久后台进程。
