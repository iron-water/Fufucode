# 插件化审计与产品原则

> 用户后续已明确拆分例外：工作面板组及文档提取、系统通知、用量统计、储存诊断、高级界面定制保留内置。当前实施以[最新拆分范围与预览纠偏](../tasks/plugin-extraction-scope-2026-10-16.md)为准，下表是候选研究，不是把例外一并拆走的授权。文件预览必须是工作面板默认能力，不独占功能tab。

日期：2026-10-16；观察main HEAD `bd8a24498980457584d779f973e2d5d03f98a7c6`。用户要求无论大小功能均考虑插件化，并要求主控记住。本文为源码架构研究及建议，不是这些模块已拆包的证明，不修改生产行为。目录路径均相对仓库根。

## 结论与证据

已有插件目录/状态/能力门禁，尚不是所有内置功能的真实包管理器。`Core/Plugins/PluginMarketplace.cs`的Install/Uninstall修改plugins.json状态；`PluginToolRegistry`按门禁暴露工具；BrowserUsePlugin与ComputerUsePlugin实现仍在Core默认编译范围。App直接引用WebView2，Core直接引用Fufucode.Pi；`Views/WorkbenchPageRegistry.cs`明确Built-in registrations only，可选页面的SetEnabled只释放/重建控件，不移除程序集。不要把独立目录、类名带Plugin或隐藏页面当成已拆包。

真正插件可以与主仓同源开发；关键是独立产物、依赖边界与安装生命周期，不要求独立Git仓库，也不要求每个插件单独进程。第三方/高权限/不可信扩展优先进程隔离；AssemblyLoadContext不是安全沙箱。

## 候选清单

下表“当前”均指实现编译在主App/Core或其项目引用中；是抽样核对具体实现的架构判断，不是产品功能验收。P0优先兑现已明确的插件承诺，P1成熟可选模块，P2渐进边界；优先级不意味着用户同意默认删除。

| 优先 | 功能/推荐边界 | 源码证据 | 拆分与默认建议 |
|---|---|---|---|
| P0 | Browser Use AI浏览器控制 | Core/Plugins/BrowserUsePlugin.cs；App/Features/BrowserExperience/WebView2BrowserUseBridge.cs；Views/MainWindow.BrowserSettings.cs直接new | 独立工具插件；默认不装；依赖浏览器/CDP能力提供者，不强绑唯一引擎 |
| P0 | Computer Use电脑操控 | Core/Plugins/ComputerUsePlugin.cs、ComputerUseWin32.cs、ComputerUseInputWin32.cs | 独立高权限插件；默认不装；权限按实际动作检查，卸载停止输入任务 |
| P0 | Pi后端及其扩展兼容层 | Core/PiChatSessionAdapter.cs、Core.csproj→Pi.csproj；App/Integration/PiLocalPluginHost.cs | 将Pi专属协议、桥接与运行时供应放后端插件；保留四后端默认启用的既定策略，安装状态与外部运行时就绪状态不可混淆 |
| P0 | OMP/DSH/Codex及后续外部后端适配 | Core/Backend/BackendEngines.cs、BackendRegistry.cs；Automation/Harnesses/BatchAdapters.cs | 单后端可独立版本/修复/依赖；共享执行契约留核心，不把所有外部CLI一起带入原生包；新增后端遵循同一规则 |
| P1 | 内嵌浏览器页面与WebView2实现 | App/Views/BrowserView*、BrowserViewHost.cs；App.csproj WebView2引用 | 浏览器面板插件+引擎提供者；与AI控制分包。默认安装集合由M明确记录，不能把“AI控制默认不装”偷换为用户明确要求移除浏览器页 |
| P1 | 手机远控桌面服务/配对/二维码/Web资产 | Core/RemoteControl/*；App/Features/RemoteControl/*；SettingsPageViewModel.Features.cs | 独立远控插件，Android App仍是另一个客户端产物；安装桌面端不隐含启动监听，卸载撤销配对/停止服务。此次研究可列入但不计前轮不含远控的包大小 |
| P1 | 语音朗读 | App/Infrastructure/ReplySpeechService.cs；Views/MainWindow.Speech.cs | SAPI提供者插件，按需安装；当前会启动PowerShell/SAPI，不能统称零外部进程；未来STT/TTS引擎独立，不假称已有录音识别 |
| P1 | 资料库面板/索引/检索 | App/Features/Library/* | 可选资料库插件；聊天中通用附件/引用协议留核心，卸载不删原件/引用历史 |
| P1 | 文档格式提取器 | Library/LibraryTextExtractor.cs | 现有文本/Markdown/代码提取可作基础提供者；后续PDF/OCR/Office拆独立插件。当前二进制格式返回Unsupported，不宣称已有这些解析器 |
| P1 | AI本地代码审查面板/执行/导出 | App/Features/LocalReview/*；Core/Features/LocalReview/* | review插件；通用Git快照和自动集成证据基础留核心；不能让卸载审查面板破坏自动开发的必要审核 |
| P1 | 交互终端面板与体验增强 | App/Views/TerminalDockView*、TerminalScreenControl.cs；Infrastructure/ConPtyTerminalSession.cs；Features/TerminalExperience/* | 终端UI插件；原生agent的基本命令执行/取消能力留核心。历史搜索/主题等先放同包，避免每个快捷键碎成插件 |
| P1 | 非基线皮肤与资产 | App/Features/Skins/SkinCatalog.cs、Aero/*、WorkBuddyIconSet.cs；Styles/Skins/* | Metro/M3/Aero/Luna/WorkBuddy可独立皮肤包；默认基线、动态资源契约、安全回退留核心；卸载选中皮肤先回退，保持已验收外壳/辅助栏 |
| P1 | 用户定时任务面板/任务模板 | App/Infrastructure/WorkbenchSchedules.cs；Views/MainWindow.Schedules.cs | 用户任务扩展可插件；自动开发自身可靠补漏/持久计时不依赖可卸载UI，不能卸载后丢后台运行状态 |
| P2 | 文件浏览/高级预览面板 | App/Views/WorkspacePanelView*；Infrastructure/Browser/BrowserFilePreview.cs | 可选面板与格式预览提供者；基础读写/路径安全留核心，不把文件工具一并卸掉；Web预览依赖浏览器能力显式声明 |
| P2 | Git变更/差异查看面板 | App/Views/MainWindow.Workspace.cs、WorkspacePanelView*；Core/GitDiffSnapshot*、DiffQuery.cs | 面板/增强可插件；worktree/准确HEAD/受控整合/安全Git原语是默认自动开发基础，不整体剥掉 |
| P2 | Windows系统通知、声音等投递提供者 | App/Views/MainWindow.NotificationDelivery.cs；Infrastructure/NotificationDeliveryPreferences.cs | 系统投递插件；应用内审批、错误、阻塞通知留核心，卸载投递器不能吞审批 |
| P2 | 用量详情/统计展示 | App/ViewModels/UsageDetailsViewModel.cs；Views/UsageDetailsView*；Core/SessionUsageQuery.cs | 统计面板与导出可插件；预算核算/执行限额/账本留核心，不能通过卸载逃避限额 |
| P2 | 高级外观定制/动作栏/状态栏组件 | App/Views/MainWindow.Customization.cs；ViewModels/WorkbenchCustomizationViewModel.cs | 额外组件/配置包可扩展；基础布局、焦点、快捷键路由、无障碍与设置骨架留核心 |
| P2 | 存储诊断/高级报表/清理策略 | App/Features/Storage/*；Core/DiagnosticExport.cs | 高级工具可插件；基本存储位置、必要保留清理、诊断与安全路径检查留核心；不可卸载恢复保护 |
| P1，用户明确可插件 | 自动开发整体：页面/规划/调度/模板/专属后台 | App/Features/Automation/*；Core/Automation/* | 用户补充“自动开发可以作为插件”。完整功能可独立第一方包，不只允许换模板；通用宿主/存储/权限契约留核心，自动开发专属DAG/协调/审核集成实现归插件。默认安装/启用是单独产品策略，不能因此前默认工作流就拒绝拆包 |

未找到实际实现的图像生成、云电脑、站点发布、密码管理等，不仅凭Codex菜单推定Fufucode已有；未来若做应按提供者插件设计。快捷键值、字号、亮暗、返回按钮、基本复制粘贴等属于设置/基础交互，不值得为插件数量而拆包。

## 最小核心与扩展契约

保留：应用外壳/停靠/导航、基础聊天和会话数据、原生C#内核及通用工具执行契约、项目身份/权限审批、凭据安全接口、插件目录/安装器/宿主、通用持久事件与恢复接口。自动开发专属DAG、协调器、审核集成策略与状态schema可以归插件，由宿主提供版本化存储/迁移服务，不再将整套自动开发视为不可拆核心。模型API通用协议可内置，供应商特有认证/协议另作为提供者扩展候选，不为省很少代码让首次原生聊天不可用。

用户追加决定：自动开发允许作为完整插件。安装时注册项目主控入口和专属后台服务；停用/卸载前先暂停新派工、收敛在途写入、保存状态，不能丢掉工作树、交付、消息与项目恢复数据。未安装时主程序可完成普通原生聊天；安装后仍按已定对话驱动默认流程工作。是否在首次安装中预装自动开发包，是独立默认清单事项，本轮用户未要求默认移除，不自行推断。

扩展点：面板、设置章节、命令/快捷键贡献、agent工具、执行后端、皮肤/资源、解析器/预览器、通知投递、计划策略。均有稳定ID/版本/宿主兼容范围/依赖能力/权限；UI按已安装包注册，不由MainWindow逐个new具体实现。宿主保持可用性和返回语义。

安装事务：取得包（支持本地离线包及市场来源）→验证来源/完整性/兼容性/依赖与权限→暂存→原子激活→可读回执。不能修改状态后下载失败仍显示已安装。更新失败回滚；卸载先停任务、注销工具/面板/资源，清载荷，数据保留或显式选择；遇锁定可待重启，准确说明状态。签名策略须区分官方受信与用户明确允许本地包，不发明已有签名基础设施。

设置存储失败必须返回实际错误，不能沿用当前SaveLocked吞异常再给成功UI。运行中更新需处理旧调用与版本绑定；禁用不自动删除用户成果。Native与现有后端共用能力抽象，禁止插件经UI另开无审计执行通道。

## 推荐实施次序与验收

1. M确认默认安装清单和共享边界，记录用户插件化优先原则；不擅自扩大成所有功能默认删除。
2. 先完成真实包生命周期与贡献注册接口，用Browser Use/Computer Use兑现默认不装。保留既有门禁作为权限一层，不冒称它就是包管理。
3. 后端适配与运行依赖拆包；兼容旧会话和既定默认启用策略；不要求Node存在才能进入原生模式。
4. 浏览器实现、皮肤、语音等低共享候选渐进迁移；资料库/审查/终端按完整纵向包迁移。不要一次全面重写造成当前默认工作流停摆。
5. 每个包独立证明默认载荷不含实现与专属依赖、未安装时无对象初始化/工具暴露/监听、真实安装后可用、禁用/卸载确实生效、离线/坏包/版本不兼容/更新失败恢复、运行中卸载与历史数据兼容。检查包实际内容，不按UI安装标签验收。

同仓源码保留是正常开发方式；用户要求的是可选发布/真实安装，不是从仓库删除功能。拆包也不保证每项都大幅省空间，程序集/资源实际发布与压缩体积必须重新测量。本轮只研究并更新规则，未改构建、未卸载任何现有模块，未运行产品测试。
