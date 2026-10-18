# Seep 逆向工程工作台插件接入

日期：2026-10-16。状态：需求已提出，产品尚未实施。来源：用户在提问线程的直接要求。

## 用户原始意图

用户原文（2026-10-16）：

> 我打算把这个 lab 功能和 seep 工作台、MCP 工具链以及作战协议人格打包作为一个插件做到我现在正在开发的
> 项目 fufucode 里面，在插件市场里面启用。

即：把外部已有的 Seep Reverse Lab 工作台（Lab 状态机 + MCP 工具链 + 作战协议人格）
作为**一个可由用户在插件市场中自主安装/启用/停用的产品插件**接入 Fufucode。

## 现状核实（本次已实际读取，非推测）

### 外部资产（Seep Reverse Lab，位于 `C:\Tool\Reverse\Seep-Reverse-Lab`）

| 资产 | 实际路径 | 形态 |
|---|---|---|
| Lab 状态机（Pi 扩展版） | `Tool\prompts\extensions\security-audit-interceptor.ts`（23587 字节） | pi ExtensionAPI（`pi.on("input"/"context"/"before_provider_request")`） |
| Lab 状态机（中继版）★ | `Tool\scripts\compliance-relay\`（`relay.py` 33621B、`refusal.py` 17849B、`guard-prefixes.json`、`sensitive-rules.json`、84 项 unittest） | 纯 Python 标准库 HTTP 中继，**协议无关** |
| MCP 工具链 | `Tool\mcp\seep_mcp_server.py`（23 工具） | Python stdio MCP 服务 |
| 作战协议人格 | `~/.pi/agent/AGENTS.md`（9020 字节） | Markdown 指令文件 |
| 工作台根指令 | `CLAUDE.md`（5728B）、`DSH-PROFILE.md`（3867B）、`.mcp.json` | 目录级触发 |

**关键事实（修正早期误判）**：Lab 转译能力存在**两套独立实现**，其中 `compliance-relay` 是
**网络传输层**实现，不依赖任何 Agent 私有扩展 API，任意支持自定义 `baseUrl` 的客户端均可覆盖。
其内置：最长优先匹配、单趟不回扫、按协议分组的注入守卫（`guard-prefixes.json` 三协议白名单）、
拒答自动重试（命令式 → 评审清单式，实测 377 tokens 被丢弃 → 3324 字回答）。
转译规则由 `extract-rules.py` 从 TS 扩展单向提取，**TS 是唯一真值源**。

### Fufucode 现有接缝（已核实）

| 接缝 | 文件 | 可用性 |
|---|---|---|
| 插件目录真源 | `src/Fufucode.Core/Plugins/PluginMarketplace.cs` → `PluginMarketplaceCatalog.All` | 加条目即可 |
| 插件状态存储 | 同上 `PluginStateStore`（`plugins.json` 原子落盘，`IsInstalled`/`IsEnabled` 分离） | 直接复用 |
| 能力门禁 | `src/Fufucode.Core/Plugins/PluginCapability.cs`（`PluginCapability` 枚举 + `ProviderByCapability`） | 需加枚举项 |
| 工具注册表 | `src/Fufucode.Core/Plugins/PluginToolRegistry.cs`（`IPluginToolHandler` / `PluginToolRegistration`） | 直接复用 |
| 宿主桥样板 | `BrowserUsePlugin.cs` / `ComputerUsePlugin.cs` | 照抄模式 |
| App 侧门禁 | `src/Fufucode.App/Features/Plugins/PluginAccess.cs` | 需扩展 |
| 本地插件服务 | `src/Fufucode.App/Features/Plugins/LocalPluginService.cs`（73411 字节） | **未读，待核实** |

### 已确认的三个缺口

1. **人格注入无承载**：`PluginDefinition` 无 `InstructionsFragment` 之类字段；
   Fufucode 无「插件向 system prompt 注入片段」的机制。
2. **工具桥缺失**：seep 是 Python stdio MCP 服务，Fufucode 工具处理器是 C# 进程内调用，
   需新增 C# MCP 客户端桥（stdlib 级 JSON-RPC）。
3. **Lab 状态机的宿主落点未定**：`LocalPluginService.cs`（73KB）与 pi 扩展管理
   （`PiPluginLoadProof.cs`）**尚未通读**，接入细节不能凭推测确定。

## 必须遵守的既有约束

以下摘自 `native-kernel-plugin-marketplace.md`（权威需求），本任务不得违背：

- **不把 Pi 扩展安装器改名冒充统一市场**；区分「引擎自身扩展」与「Fufucode 产品插件」。
- 非必需能力**默认不安装**；未安装功能显示**真实未安装与安装入口，不能渲染假开关**。
- **安装 ≠ 启用**：只有 `IsAvailable`（已安装且已启用）才暴露工具。
- 卸载须撤销工具注册、终止子进程，**不留可调用的幽灵工具**；不得绕过统一审批。
- 在途会话依赖插件时不得直接卸载或静默换后端。

## 范围

### 纳入

1. 插件目录新增 Seep 工作台条目（`PluginCategory.ToolExtension`，默认未安装）。
2. 新增能力枚举与映射（拟 `PluginCapability.ReverseEngineering`）。
3. C# 侧 MCP 客户端桥 + 23 工具 → `PluginToolRegistration` 映射 + `IPluginToolHandler` 实现。
4. 人格注入承载字段与消费点（需先定位会话 system prompt 组装处）。
5. Lab 状态机的接入路径**择一并说明理由**（见下方决策点）。
6. 设置页/插件市场页的真实状态显示与门禁。

### 排除

- 不改 Seep 工作台的 Python/C# 源实现（`seep_mcp_server.py`、`relay.py` 等）本体。
- 不搬移 `Tool/mcp/Tool/`（其内部硬编码相对路径）。
- 不因本插件删除或弱化 Browser Use / Computer Use 现有能力与门禁。
- 不做公网插件商店、不自动下载执行外部包。
- 不在本任务内实施 Lab 状态机的 C# 全量重写（列为备选路径，需单独立项）。

## 待用户/主控决策的开放点

以下三项属产品方向，需明确后再实施，不得由执行方自行拍定：

1. **Lab 状态机接入路径**：
   - A. **中继路径**：Fufucode 模型请求 `baseUrl` 指向本地 `compliance-relay`，协议无关、
     零 C# 重写、跨后端统一；代价是引入本地常驻进程 + 请求绕行。
   - B. **pi 扩展路径**：仅在 pi 后端由 pi 自行加载 TS 扩展；零重写，但**切原生内核/其他后端即失效**。
   - C. **C# 重写路径**：在 Fufucode 输入管道用 C# 实现三个加工层，跨后端且无外部进程；
     代价是重写 18 条快捷口令 + 30 条敏感词 + 最长优先算法 + 三协议守卫。
2. **人格注入的作用域**：仅注入本插件启用的会话，还是全局。
3. **默认安装策略**：确认为「默认不安装」（与任务书一致）还是用户另有要求。

## 验收条件

- `dotnet build Fufucode.sln` → 0 警告 0 错误。
- 插件市场可真实安装/启用/停用/卸载，各状态在 UI 如实显示，未安装不渲染假开关。
- 未安装或未启用时：工具不进入调度表，直接调用被拒且原因指向未安装/未启用。
- 安装并启用后：23 工具全部注册且带参数 Schema，真实调用可返回结果。
- 卸载后：工具全部注销、子进程终止、无幽灵工具；重复卸载幂等。
- 未接线宿主（如工作台未部署）时**如实报告**，不编造工具结果。
- 定向测试正负双向覆盖，测试不得靠放宽断言制造通过。

## 文件所有权与工作树

按 `execution-contract.md` 执行：执行会话使用独立 worktree，不直接写 main。
共享组合根、设置页、发布、权威计划/进度/决策由主控 M 统一接线。
本任务建议新增路径（待主控确认）：

```
src/Fufucode.Core/Plugins/ReverseLab/     ← 插件实现
docs/records/<任务ID>-seep-plugin.md      ← 交付记录
```

## 未执行 / 未验证声明

- 本任务书基于**实际读取**上述文件形成，但**未通读** `LocalPluginService.cs`（73KB）、
  `PiPluginLoadProof.cs`、`PluginPackageScanner.cs`、`Contracts.cs`。
- 会话 system prompt 组装位置**尚未定位**。
- `PluginDefinition` 扩展字段的实际影响面**尚未评估**。
- 未运行构建、未运行任何测试。
- 未修改任何产品源码；本文件为计划，不代表任何功能已实施。
