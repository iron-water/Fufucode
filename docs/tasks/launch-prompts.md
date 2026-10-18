# 可直接发送给Codex/OMP的任务prompt

2026-10-14受管派工/正常返工续接必须在**实际发送的完整prompt**中附delivery-callback-protocol要点：直接人类授权原文及来源01a12596/userMessage01a13994-73c3、主控01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07、worker/task/run/base/revision绑定、由最终HEAD构成的稳定eventId；停写落卡后一次send_message_to_thread回传、最终唯一规范JSON卡、不等无必要回信。工具缺失/结果不明保卡两小时补扫，不盲重发。旧worker树未必有新文档，不能只说“读取主控协议”或只在heartbeat配置写规则。在途任务不为此被打断，下一正常续接携带。

2026-10-11 新开工顺序与范围改用 [开发拓扑 v2](development-topology-v2.md) 的 V1/C1/V2 等任务包。下文 D/O/U prompt 是历史范围参考，不再直接投入旧队列。首项 V1 只补已整合 D1 的原生验收缺口；新任务不得重新实现已接受模块。仍按本文件的模型、所有权和交付纪律生成具体 prompt。

新派prompt首行必须直接写`推荐主模型：group/<完整路由名>`，从当前canonical的group目录选择，备选也写完整group ID。下文任务段落是范围模板，M实际发布时须补上具体ID，不让用户另查档位表猜模型。打回保留原模型，测试首选免费devin/swe-2的规则不变。

日期：2026-10-10。每个代码块可独立复制发送。发送一项只授权该项，不自动启动其他任务、付费或公开部署。

M发布具体开工prompt时，首行写任务ID、综合难度、具体推荐模型与理由，按session-difficulty.md判断而非固定照搬默认表。打回prompt首行写“原Lx→修复Ly（差值），保留原主模型”；仅升高至少两级时提示可用更强子代理辅助限定实现，不自动换lead/派发。测试/验证仍最低成本。

完整开工入口见 [AI会话执行与交接协议](session-handoff-protocol.md)：只替换任务ID，模型自行读取本文件对应完整段落。下文历史绝对路径 `C:\Projects\Fufucode` 已搬迁；所有执行任务应在当前已准备的独立worktree工作，不能按旧路径切回主区。中途协调、模块待接线、交付修复及主会话下一步操作卡按协议执行。

任务ID对应[详细图](../面向人类/next-stage-roadmap.md)，路径/门禁以[执行约定](execution-contract.md)为准。先发D1给Codex；OMP可先选O1/O3/S1/M0。共享main仅一位源码写入lead，真正并行用受限临时worktree并由Codex收编。

## D1 → Codex：双后端最小闭环 [L4 很难]

派工难度：L4 很难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的D1行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 D1，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G0。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：复核实际Pi/OMP版本、CLI/RPC与已有能力；实现后端身份/能力表、适配、聊天创建选择、发送/停止/审批/持久化恢复。旧聊天默认Pi，后端创建后固定，不做跨后端转换。不重写Agent循环，不假设两种RPC相同。
允许范围与记录：共享Pi/Core会话、App/Integration和必要公共入口由你负责；保护其他任务目录。记录实际契约到docs/records/D1-backends.md。
验收：G1：两个后端分别真实创建/对话/停止/审批/关闭恢复；旧Pi数据不串线。computer-use验选择/审批；工具不可用明确未验证。缺失后端/模型错误/退出有真实恢复。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## D0 → Codex：发布环境与旧文案 [L3 较难]

派工难度：L3 较难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的D0行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 D0，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G0。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：核对production-delivery.md及最新交付/失败记录；只在状态变化/新证据支持时再测隔离环境，修已确认过时通知等文案。不修宿主系统、不碰用户其他VM。
允许范围与记录：scripts/packaging及明确文案、docs/records/D0-release.md；不改后端/他人模块。
验收：候选SHA准确；干净安装/启动/卸载实际结果。环境阻塞保留未通过，不把换cwd/PATH当干净机器，文案不重复全量。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## O1 → OMP：快捷键模块 [L2 常规]

派工难度：L2 常规；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的O1行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 O1，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G0。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按management-surfaces.md M1，实现真实动作列表/搜索、范围、冲突、编辑/禁用/恢复、保存。沿用默认绑定，中文组合和终端优先级不能被抢。
允许范围与记录：App/Features/Shortcuts、独立测试和docs/records/O1-shortcuts.md；共享动作注册表/MainWindow/Settings接线交Codex，不擅改。
验收：冲突/保存/恢复定向通过，Codex接线后原生键盘验证。未接共享入口不得称整项可用。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## O2 → OMP：存储管理 [L2 常规]

派工难度：L2 常规；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的O2行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 O2，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G0；可与O3独立。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按management-surfaces.md M2，实际占用可取消计量、目录/保留、清理预览/结果；仅默认清可重建缓存/过期日志，不做数据根搬迁。
允许范围与记录：App/Features/Storage、独立测试和docs/records/O2-storage.md，共享配置/导航交Codex。
验收：临时数据验证取消/活跃文件失败/清理实际变化；会话/源码/配置/凭据/资料/工作树保持，不清用户真实数据。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## O3 → OMP：资料库 [L3 较难]

派工难度：L3 较难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的O3行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 O3，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G0；聊天真实接线需G1。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按new-product-capabilities.md N2，实现收藏、引用或副本、来源/版本、标签、预览、全文检索、导入导出与显式聊天引用。先本地，不做云同步/付费嵌入，不默认上传全库。
允许范围与记录：App/Features/Library及独立测试/记录docs/records/O3-library.md；Core新模块路径由Codex明确，不改现有元数据/宿主。
验收：临时真实文件覆盖中文同名/重复/移动删除/变化/索引重建；删除条目默认不删源，引用可回溯，失败保留原件。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## S1 → OMP：皮肤样稿 [L2 常规]

派工难度：L2 常规；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的S1行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 S1，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G0。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：读docs/人类意见/前端风格.md，观察现有五区/Windows设置/辅助栏，做Fluent基线、Metro/Material 3可比较样稿，亮暗/缩放/控件各状态和主题资源方案。
允许范围与记录：docs/frontend-prototype/skins与docs/records/S1-skins.md；不改生产Styles/MainWindow。
验收：可打开样稿及实际渲染截图，明确HTML样稿非Avalonia产品。等用户选定再生产接入，不做Aero/Liquid Glass。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## M0 → OMP：手机原型 [L2 常规]

派工难度：L2 常规；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的M0行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 M0，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G0。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按new-product-capabilities.md N1，参考C:/Downloads/WorkBuddy.apk.1；先观察真实界面，不能只凭资源推测，无法运行标未验证。做列表/聊天工具/发送停止/审批附件/连接重连/返回/键盘窄屏原型。
允许范围与记录：docs/frontend-prototype/mobile与docs/records/M0-mobile.md；不改正式外壳或网络宿主。
验收：窄屏实际交互/错误状态；夹具标原型。桌面模拟不称真机；交D6，不启动正式远控。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## D2 → Codex：终端体验 [L4 很难]

派工难度：L4 很难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的D2行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 D2，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G1；参考O1动作契约。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按总排期和management-surfaces保留ConPTY/项目归属，补Shell/cwd、输入法/快捷键、选择粘贴、回滚查找字体、输出跟随/大输出。全屏程序不足先评估再决定成熟组件，不直接重写。
允许范围与记录：现有Terminal*、ConPtyTerminalSession、TerminalBuffer与必要集成，docs/records/D2-terminal.md。
验收：computer-use真实中文/组合键/鼠标选择/大小焦点/代表性TUI；临时项目退出残留与必要构建测试通过。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## D3 → Codex：浏览器基础 [L3 较难]

派工难度：L3 较难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的D3行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 D3，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G1。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按management-surfaces M4复用WebView2，补标签/profile、查找缩放历史、下载管理/Windows已知文件夹、弹窗权限/运行时诊断，保留文件预览。
允许范围与记录：BrowserView*、Infrastructure/Browser与必要接线，docs/records/D3-browser.md；不接Agent工具/CDP。
验收：本地测试站+computer-use验证原生输入焦点/DPI/停靠隐藏浮动、下载取消同名、权限拒绝/资源释放；不读取用户cookies。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## D4 → Codex：Git/工作树 [L3 较难]

派工难度：L3 较难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的D4行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 D4，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G1。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按management-surfaces M3，实际状态/差异分类、明确文件的暂存/取消暂存/提交、工作树列表/聊天关联/占用脏保护。提供固定差异快照契约给O4。
允许范围与记录：GitWorktreeService/工作区差异与必要入口，docs/records/D4-git.md。
验收：临时仓库验证中文路径/选择操作/安全移除/失败恢复；git worktree remove，不强删脏目录、不试用户仓库回滚。computer-use验选择差异。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## D5 → Codex：公共后台宿主 [L4 很难]

派工难度：L4 很难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的D5行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 D5，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G1。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按complete-feature-modules C1和new-product-capabilities N1，把会话生命周期脱离窗口，唯一写入宿主、项目/后端身份、快照事件补齐、命令身份/取消、审批有效性与关闭/恢复。先用户级进程，不默认系统服务。
允许范围与记录：现有Core/Integration与新宿主/IPC、必要入口；docs/records/D5-host.md记录真实操作契约供O6/D6。不做手机页/完整周期调度。
验收：G2：关窗后台/重开/崩溃、重复提交/同会话独占、旧审批/断线/补齐真实验证；computer-use验退出选择。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## O5 → OMP：插件完整管理 [L3 较难]

派工难度：L3 较难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的O5行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 O5，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G1。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按complete-feature-modules C2和D1能力，补来源/版本/兼容、pending/active、安装移除/更新回退；先本地固定版本，不造市场、不宣称全部跨后端兼容。
允许范围与记录：ExtensionsPage*、ExtensionsPageViewModel及明确分配独立模块/测试；docs/records/O5-plugins.md。Integration交Codex。
验收：真实扩展加载执行/禁用重启、缺失错误、更新失败恢复；授权撤销后接单独服务验证。不打印认证，不将待启用当已加载。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## O4 → OMP：本地代码审查 [L3 较难]

派工难度：L3 较难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的O4行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 O4，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G1+D4。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按complete-feature-modules C3和D1/D4契约，审查范围/快照、状态取消/错误、发现优先级/证据/有效行/处理/过时/导出，默认只读，不自动修复或发布评论。
允许范围与记录：App/Features/Review与明确分配独立实现/测试，docs/records/O4-review.md；不改底层Git/后端/MainWindow。
验收：临时真实差异/已知缺陷/变动后定位，真实低成本最小审查闭环；固定夹具不能证明审查质量，Codex原生验跳转。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## O6 → OMP：定时模块 [L3 较难]

派工难度：L3 较难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的O6行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 O6，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G2；前台/规则可提前。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按complete-feature-modules C1和D5契约，完善一次性、周期/时区夏令时/漏跑、记录恢复；公共宿主提交，不再起引擎。同任务不重叠，漏跑最多补一次，未知副作用待恢复。
允许范围与记录：App/Features/Schedules与Codex明确分配调度文件/测试，docs/records/O6-schedules.md；旧MainWindow.Schedules接线交Codex，不改D5。
验收：可控时钟规则+实际关窗/重开/睡眠/崩溃/审批；不声称工具恰好一次。后台接线必须过G2，computer-use由Codex验收。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## D6 → Codex：手机局域网远控 [L4 很难]

派工难度：L4 很难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的D6行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 D6，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G2+M0。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按new-product-capabilities N1与D5契约，正式PWA/适合现有技术手机端、局域网配对/撤销、流式会话工具/发送停止/审批附件/重连。共享唯一写入宿主，原请求/审批不得重放，不暴露原始RPC。
允许范围与记录：你先确定正式前端/服务目录，写docs/records/D6-mobile.md，非核心页面可给OMP明确路径；不做外网/推送/整机控制。
验收：真机键盘/返回/锁屏/切网/重连/同时操作/撤销与桌面computer-use；没真机标未验证，窄窗不能替代。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## O7 → OMP：图像生成 [L2 常规]

派工难度：L2 常规；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的O7行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 O7，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：O3；供应商适配可提前。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按new-product-capabilities N3及O3契约，复核已有引擎能力，接一种真实提供商生成/编辑、能力参数、任务取消/错误、产物历史/收藏；不覆盖原图、不盲目重复收费请求。
允许范围与记录：App/Features/Images与明确分配独立适配/测试，docs/records/O7-images.md；共享认证/外壳交Codex。
验收：真实生成编辑/下载收藏/失败未知状态；收费/凭据先获授权，使用最便宜且支持图像的显式模型，不能文本模型/示例图片冒充。不默认发起收费。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## S2 → Codex：皮肤生产接入 [L3 较难]

派工难度：L3 较难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的S2行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 S2，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：S1；选定样稿后接其皮肤。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按用户皮肤计划/S1统一主题资源与控件状态，保留Fluent；接已选Metro或Material。未选则先主题机制/Fluent，等待视觉选择但其他线继续。
允许范围与记录：生产Styles/WorkbenchAppearance与必要控件，docs/records/S2-skins.md；不与其他任务同时写共享Settings/MainWindow。
验收：computer-use亮暗/缩放DPI/悬停禁用错误/菜单、WebView2和终端；五区设置辅助栏保持，不称未选皮肤完成，不做Luna/Aero。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## V → Codex：单项原生验收 [L3 较难]

派工难度：L3 较难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的V行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 V，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：对应模块已真实接入。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：选择一个尚未原生验收的已完成增量，用新构建/隔离数据测试指针键盘/输入法/焦点/DPI/返回。每项开发后执行，不攒最后。
允许范围与记录：docs/records/V-native.md；缺陷交对应负责人或最小修复，避免改他人在写的文件。
验收：静态/回调/旧截图不替代原生。手机真机。工具停止/拒绝不可绕过；Escape不是暂停整条流水线，记录限制后继续其他独立工作。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## R → Codex：首轮集成与发布 [L4 很难]

派工难度：L4 很难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的R行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 R，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：各首轮出口/V；外部阻塞不伪装过门。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按production-delivery和最新实际脚本收编准确提交，必要全量构建/测试、配置/后端身份迁移、包内私有cwd/PATH/完整性、干净安装启动卸载，更新候选SHA和入口。
允许范围与记录：集成共享入口/packaging/发布物与权威记录，docs/records/R-release.md；保护用户改动，worktree先收编再按git移除。
验收：G3必须实际通过，未接线/原生/安装阻塞仍未通过。不能只看入口，不push/公开发行。真实模型只最低成本必要闭环。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## D8 → Codex：浏览器Agent与开发诊断 [L4 很难]

派工难度：L4 很难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的D8行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 D8，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G3+D3。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：分两步：统一浏览服务给Pi/OMP实际打开/观察/点击输入/截图结果，再按来源控制CDP、控制台网络/元素/标注。复用profile/tab，不造两份，不给网页宿主任意权限。
允许范围与记录：浏览器服务/必要工具桥与共享接线，docs/records/D8-browser-agent.md。
验收：computer-use本地站焦点/DPI与Agent观察结果，真实低成本闭环；用户自己登录，不打印cookies，全CDP不默认开放。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## O8 → OMP：PR审查 [L2 常规]

派工难度：L2 常规；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的O8行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 O8，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G3+O4。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按complete-feature-modules C3接已认证gh读取PR/精确head差异，发现映射当前行，head变化标过时。先只读，发布评论单独明确授权，不合并推送。
允许范围与记录：Review与明确分配GitHub读取适配/测试，docs/records/O8-pr-review.md，不改底层Git/宿主。
验收：实际head/行位置/权限失败取消；低成本最小闭环，Codex最终验跳转，外部登录阻塞如实。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## D9 → Codex：手机外网与推送 [L4 很难]

派工难度：L4 很难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的D9行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 D9，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G3+D6真机闭环。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按new-product-capabilities N1先评估用户已有私有网络通路，外网连接/撤销/重连，再单独推送及拒绝权限可用状态。
允许范围与记录：既有远控模块及必要配置/传输，docs/records/D9-mobile-remote.md。
验收：实际跨网真机/桌面computer-use；不默认采购/部署公网/开放端口/改系统。缺外网条件标具体阻塞，不用局域网冒充。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```

## S3 → Codex：Luna/Aero [L3 较难]

派工难度：L3 较难；理由与建议会话主模型档位见docs/tasks/session-difficulty.md的S3行。测试/试跑仍使用最低成本可用模型。

```text
在 C:\Projects\Fufucode 执行 S3，直接完成任务，不仅返回方案。先读 AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、docs/面向人类/next-stage-roadmap.md，以及本任务引用的三类任务书/记录（位于docs/tasks或docs/records）。
依赖：G3+S2。核对实际交付；未交付时先做非依赖部分，明确阻塞，不伪造接口或越门标完成。
目标：按用户皮肤计划先Luna全状态/授权明确素材，再Aero视觉近似和合成可行性，沿用主题接口；受限样稿素材可交OMP，Liquid Glass暂缓。
允许范围与记录：主题/素材和必要窗口外观，docs/records/S3-skins.md，不改业务外壳。
验收：computer-use真实DPI/WebView2终端覆盖/亮暗/错误禁用；视觉近似如实。收费素材生成先说明成本授权，不宣称未实现玻璃效果。
成本/纪律：模型测试、验证lead与试跑显式最低成本可用模型，首选devin/swe-2；不可用记录原因选最低成本替代，强模型验证须理由/预算/同意。普通构建/无模型测试不需模型参数。保护用户改动和既有基线，reference只读；不改另一任务在写的文件，不push、不改写历史，不擅自登录/付费/部署。
每个可验证增量小步提交并记录命令、结果、未验证项和下一步；新增缺陷登记failure-registry。OMP受限任务更新自己的任务记录，权威进度/决策与共享接线由Codex收编。依赖/外部阻塞不拖住独立工作，未实际通过不标通过。任务收尾时按docs/tasks/session-handoff-protocol.md主动生成完整交付卡，写入本任务记录并在最终回复展示，不等待用户催发或填写；部分/阻塞及修复重新交付如实标状态，不自行开始下个任务。
```
# U：自动开发工作台新增入口（2026-10-10）

先读 [U0–U3任务契约与U1任务包](automation-workbench.md)。U0研究/HTML先供用户讨论；U1可独立实施，U2真实适配不越G1/D5/G2。不要直接照示例HTML连接模型或把演示状态接成生产事实。模型推荐、原会话打回与显式免费试跑规则沿用本文件及session-difficulty。
