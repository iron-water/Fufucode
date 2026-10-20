# PI-Desktop 插件市场对照与本机接入建议

日期：2026-10-17
调研对象：`plugins.aiuo.net` 市场目录、GitHub 备份仓库 `AIUO-Net/pi-desktop-plugins` 的 `catalog.json`、`vastsa/PI-Desktop` 的插件开发文档
本机基线：`C:\Projects\Fucode\Fufucode` 当前 main

本文是架构与产品对照研究，不修改生产行为，也未安装或运行任何 PI 插件。市场条目标注为 community / unverified，以下能力判断只依据目录元数据与本机源码，未做逐包审计。

## 结论

1. **不能直接安装 `.piplug`。** PI-Desktop 插件运行在 Node 进程 + 沙箱 Electron 窗口，通过 `pi.*` 与 `window.pluginBridge` 接口工作。Fufucode 当前默认内核是纯 C# FUMO，默认包明确不携带 Node；直接兼容需要一个 Node + Electron 兼容宿主，成本高且违背零外部运行时方向。
2. **可行路径是原生复刻。** Fufucode 已有 `IPluginPackage`、`PluginPageContribution`、`IPluginToolHandler`、皮肤贡献与依赖声明，足以把高价值插件做成真正的 C# 插件包。2026-10-18 更新已把页面贡献接入宿主：`PluginWorkspacePageHost` 会把已安装且启用的插件页面注册进停靠区，终端与自动开发已作为真实插件页面运行。当前缺口只剩贡献元数据（停靠区域、默认可见性、页签顺序）。
3. **最值得首批接入的是工作面板类小工具**，不是大而全的 AI 能力：
   - 左栏：会话来源/时间过滤、自动会话标题、会话归档检索。
   - 右栏：Git 历史/Blame、提交与推送、订阅额度、日志查看、剪贴板历史、待办。
   - Agent 工具：读图、SSH 远程读写执行。
4. **与现有能力重复的不做**：浏览器控制、电脑操控、终端、侧边聊天、用量统计、远程控制、打开方式、主题包、自动开发/任务编排。重复实现只增加维护面。

## 本机左右辅助栏现状

左右栏不是固定面板，而是同一个可停靠工作台：

| 区域 | 默认页 | 可打开的其他页 |
|---|---|---|
| 左栏 | 会话管理、文件 | 浏览器、资料库、本地审查、自动开发、终端、侧边聊天、通知、用量 |
| 右栏 | 变更、浏览器 | 同上，可拖拽、浮动整栏、固定页签、隐藏 |
| 底栏 | 终端 | 同上 |

关键源码：

- 停靠与页签：[DockRegionView.cs](../src/Fufucode.App/Views/DockRegionView.cs)
- 默认布局：[WorkbenchLayoutState.cs](../src/Fufucode.App/Infrastructure/WorkbenchLayoutState.cs)
- 页面注册：[WorkbenchPageRegistry.cs](../src/Fufucode.App/Views/WorkbenchPageRegistry.cs)
- 内置页面装配：[MainWindow.Workspace.cs](../src/Fufucode.App/Views/MainWindow.Workspace.cs)
- 插件包契约：[IPluginPackage.cs](../src/Fufucode.Core/Plugins/IPluginPackage.cs)
- 插件市场目录：[PluginMarketplace.cs](../src/Fufucode.Core/Plugins/PluginMarketplace.cs)

`WorkbenchPageRegistry` 已有 `RegisterPlugin` / `Unregister`，`PluginWorkspacePageHost` 消费 `IPluginPackage.CreatePageContribution` 并把插件页面接入停靠区；`workspace.terminal` 与自动开发插件已按此路径真实运行。宿主不再静态引用这些页面实现。因此现在可以直接做具体插件，剩余的前置工作只有贡献元数据与权限声明。

## 对照总表

判断口径：

- **A 直接可做**：本机已有数据源或 UI 基础，纯本地，做成插件即可。
- **B 需补能力**：需要新增宿主能力、外部进程或网络凭据。
- **C 已有重叠**：本机已有等价能力，不建议重复。
- **D 不适合**：平台绑定、生态绑定、低价值或与产品方向冲突。

### 左栏（会话与项目）

| PI-Desktop 插件 | 功能 | 本机对应/缺口 | 判断 | 优先级 |
|---|---|---|---|---|
| `local.pi-session-filter` | 按来源、时间过滤会话，支持按项目保存过滤集 | 左栏目前只有会话列表与搜索，没有来源/时间过滤 | **A** | P0 |
| `net.aiuo.session-title` | 首轮完成后自动生成简短会话标题 | 本机没有自动标题生成；左栏标题依赖手动重命名 | **A**（需一次模型补全能力） | P0 |
| `com.github.yy568821795-max.session-recorder` | 会话与工具调用 JSONL 增量归档、脱敏、导出、有限搜索 | 本机有会话文件和 Markdown 导出，没有独立归档/检索面板 | **A** | P1 |
| `io.github.muzimu217.session-import` | 从 ZCode、WorkBuddy、Claude Code、Codex、OpenCode、Pi 导入会话 | 多后端已存在，但会话导入没有统一入口 | **B**（各来源格式与映射需逐项验证） | P1 |
| `axc00.project-knowledge` | 项目知识库，按项目附带笔记并在保存前确认 | 资料库与 AGENTS 已有，但缺少“项目内知识收件箱”闭环 | **B** | P1 |
| `cn.star.user-profile` | 维护 `USER.md` / `MEMORY.md` 并注入系统提示 | 已有全局记忆目录，项目级用户画像未产品化 | **B** | P2 |
| `cn.star.skill-learning` | 把完成的工作沉淀为 `SKILL.md` | 本机已有 skills 目录与加载，缺少“从会话沉淀技能”的入口 | **B** | P2 |
| `local.pi-markdown` / `local.mi-note` | Markdown 笔记 / 小米云笔记 | 资料库可承接本地 Markdown；小米云为外部账号服务 | **C / D** | — |
| `adion.panel.anywhere`、`pi.agents-anywhere` | 手机远程访问与控制 | 本机已有手机远程控制服务插件 | **C** | — |
| `io.github.xinzhihong.group-rooms` | 共享房间与多人会话 | 无对应产品面，且需要服务器与账号体系 | **D** | — |

### 右栏（工作面板）

| PI-Desktop 插件 | 功能 | 本机对应/缺口 | 判断 | 优先级 |
|---|---|---|---|---|
| `pi.gitlens` | Git 状态、历史、差异、Blame、分支、提交、Stash | `变更` 页只有工作区变更与差异，没有历史、Blame、Stash | **A** | P0 |
| `io.github.liushunqiu.pi-idea-git` | IDEA 风格提交/推送 | 本机有 Git 基础动作，但没有面板内提交/推送闭环 | **A** | P1 |
| `io.github.czp0312.subscription-quota` | Anthropic/OpenAI/Copilot/OpenRouter 剩余额度 | `用量` 页统计本会话 token，不显示订阅额度 | **A**（网络读取 + 凭据） | P0 |
| `pi.log-viewer` | 大日志流式分页、跟随、搜索高亮、过滤、固定行 | 本机有诊断日志路径，无日志查看器 | **A** | P0 |
| `pi.clipboard-history` | 30 天剪贴板文本历史，可恢复，可选图片 | 无剪贴板历史 | **A**（Win32 剪贴板监听） | P1 |
| `pi.todo` | 待办列表、四象限、到期提醒、Agent 工具 | 无独立待办页；计划任务不能替代 | **A** | P1 |
| `local.diff-view` | 会话内变更历史、回复卡、运行中变更覆盖层 | 本机已有变更摘要与会话变更卡，功能大部分重叠 | **C**（只借鉴运行中覆盖层） | P2 |
| `pi.file-manager` | 目录树、预览、PDF、Git diff、外部打开 | `文件` 页已具备主干 | **C** | — |
| `io.github.hufangyuan460-blip.windows-terminal` | ConPTY Windows 终端、多标签、CJK | 本机终端已实现 ConPTY | **C** | — |
| `io.github.ldzhouquan.fast-browser` | 浏览器 Agent 驱动工作面板 | 本机 Browser Use 插件 + WebView2 已覆盖 | **C** | — |
| `local.pi-canvas` | React 交互画布、TSX/JSX 读取 | 需要 Node/React 运行时，与原生方向冲突 | **D** | — |
| `cainiao0502` 游戏、`scratch-calc` | 面板小游戏、草稿计算器 | 与开发工作台定位无关 | **D / P2** | — |
| `pi.super-domain-man` | DNS 与 SSL 管理 | 与产品无关 | **D** | — |
| `hawchou.mumu-player` | MuMu 模拟器管理 | 与产品无关 | **D** | — |
| `cn.star.jianying-assistant` | 自然语言生成剪映草稿 | 依赖剪映与电脑操控，垂直场景 | **D**（可后续作为外部工具） | — |

### 跨栏与 Agent 工具

| PI-Desktop 插件 | 功能 | 本机对应/缺口 | 判断 | 优先级 |
|---|---|---|---|---|
| `io.github.muzimu217.vision-kit` | `read_image`、`image_info`、`list_images` 多模态工具 | 本机原生 Agent 工具表没有专用读图工具 | **A** | P0 |
| `local.remote-tools` | SSH 读、写、编辑、执行 | 无 SSH 工具 | **A**（凭据与主机密钥策略需设计） | P1 |
| `pi.prompt-enhancement` | 把输入框草稿改写为更清晰的提示词 | 无“改写草稿”动作；需一次模型补全 | **A**（需补全能力） | P1 |
| `pi.prompt-switcher` | 本地 `.md` 提示词管理与首轮应用 | 无提示词库面板 | **B** | P2 |
| `io.github.cydione.prompt-injector` | 系统提示按模型绑定、首轮/整会话注入 | 与后端扩展、AGENTS 规则部分重叠 | **B** | P2 |
| `io.github.muzimu217.deps-audit` | 对暂存清单跑 osv-scanner 并列出漏洞 | 无依赖审计工具；需外部扫描器 | **B** | P1 |
| `io.github.muzimu217.usage-dashboard`、`pi.token-insights`、`pi.session-usage`、`io.github.new-beginner.session-usage` | 用量看板、热力图、会话 token | 本机 `用量` 页已覆盖 | **C** | — |
| `pi.session-orchestrator`、`io.github.tonkic.agent-governance` | 持久 Agent 会话编排、任务状态、工作树协作 | 本机 `自动开发` 与交付协议已覆盖主干 | **C** | — |
| `in.memcode.memory` | Memcode 记忆服务 | 外部服务，非本地能力 | **D** | — |
| `io.github.gzxingr.genui` | 渲染 `dsh-ui` JSON 组件 | 生态绑定，需要独立渲染器 | **D** | — |
| `pi.scratch-calc`、`io.github.catdford.color-picker` | 计算器、取色器 | 可用但不是工作台核心 | **P2** | — |

### 主题与外观

| PI-Desktop 插件 | 判断 | 说明 |
|---|---|---|
| `pi.theme.studio`、`io.github.muzimu217.theme-forge` | **B** | 本机“高级界面定制”按用户决定保留内置；可借鉴 token 编辑器，不必做成市场插件 |
| `theme-dracula`、`nexus-scenic`、`miku-theme`、`craftdesk`、`sax-design`、`theme.opencode` | **C** | 本机已有皮肤包机制与 Aero 示例，缺的是主题内容而不是能力 |

### 后端与供应商桥

| PI-Desktop 插件 | 判断 | 说明 |
|---|---|---|
| `com.opencode2pi`、`pi.clinepass`、`local.pi-antigravity.desktop`、`local.pi-commandcode-desktop`、`local.pi-desktop-workbuddy`、`community.workbuddy-connect`、`io.github.chengzhi-c.pi-desktop-wtq-pool` | **B，谨慎** | 技术上可做 provider 插件，但多为第三方账号/订阅桥接，存在条款与凭据风险；本机已有五后端与统一模型认证，不建议优先接 |
| `cn.star.grok-enhance` | **D** | 针对 Grok 的执行纪律扩展，与 FUMO 原生内核无对应关系 |
| `io.github.w1ndys.pi-open-git-web`、`pi-open-in-vscode`、`io.github.eureka-0x01.open-with` | **C** | 本机已有“在资源管理器中打开”“打开方式” |

## 建议首批接入（8 项）

按“左栏/右栏可见、纯本地、可独立卸载”排序：

1. **会话过滤**（左栏）：按后端来源、项目、最近活动时间过滤；过滤集跟项目保存。只读会话元数据，风险最低。
2. **自动会话标题**（左栏）：首轮结束后用当前后端做一次低成本补全，写入会话元数据；失败保留原标题，不阻塞回合。
3. **Git 历史/Blame**（右栏 `变更` 扩展）：复用现有 `GitDiffSnapshot` 与 `DiffQuery`，补 history、blame、stash、分支切换的只读视图。
4. **订阅额度**（右栏 `用量` 扩展）：按供应商读取额度，凭据走现有安全存储；拿不到凭据时只显示“未配置”，不猜。
5. **日志查看器**（右栏）：打开本机诊断日志，支持 tail、分页、搜索高亮、过滤。只读用户选定文件。
6. **剪贴板历史**（右栏）：本地保存 30 天文本，支持搜索与回填；默认不记录图片，不向网络发送。
7. **待办**（右栏）：本地任务清单 + 到期提醒 + 一个 Agent 工具；与计划任务分开，不混用调度器。
8. **读图工具**（Agent 工具）：`read_image` / `image_info` / `list_images`，输出多模态内容；只读用户或项目内图片。

第二批：SSH 工具、依赖审计、会话归档检索、提示词改写、提交/推送面板。

## 接入前必须补的宿主能力

1. **页面贡献元数据**：接线已于 2026-10-18 完成。剩余工作是让 `PluginPageContribution` 声明停靠区域（左/右/底）、默认可见性、图标与页签顺序；当前页面能注册，但由用户布局决定落点。
2. **插件页生命周期**：安装后注册页面，停用/卸载时停止后台任务、注销工具、释放控件；资源占用时明确“待重启”，不假装已卸载。
3. **能力与权限扩展**：现有 `PluginCapability` 只有浏览器、电脑操控、逆向、自动开发。新增插件需要 `fs.read`、`fs.write`、`net.fetch`、`clipboard`、`process.spawn` 等声明式权限，并在宿主侧按调用检查，不能只靠 UI 勾选。
4. **一次性模型补全**：自动标题、提示词改写需要一个受控的“单次补全”接口，复用当前后端与用户模型，不走插件自建通道。
5. **外部依赖部署**：`PluginDependencyDeclaration` 已有模型，可承接 osv-scanner、SSH 等外部工具；仍需校验来源、版本与哈希。

## 不建议做的事

- 不做 `.piplug` 直接兼容层。它会把 Node/Electron 重新带回默认包，破坏当前零外部依赖基线。
- 不照搬社区供应商桥接。账号共享、订阅转接类插件权限大、条款不清，且与现有模型认证体系冲突。
- 不重复实现已有能力：浏览器控制、电脑操控、终端、侧边聊天、用量统计、远程控制、打开方式、自动开发编排。
- 不把每个小工具都做成独立进程。纯本地只读工具优先同进程插件；只有高权限、不可信或需要独立运行时的才隔离。

## 未验证项

- 市场目录为社区提交并标注 unverified；本文只读元数据，未审计任何插件源码。
- 未安装、未运行任何 PI 插件，未验证 `.piplug` 包的实际接口行为。
- 页面贡献接线已按 2026-10-18 源码与终端/自动开发插件核实；插件页面的真实安装、停用、卸载行为未在本轮重新运行验证。
- 订阅额度各供应商接口、SSH 凭据策略、会话导入格式映射均未实测。
