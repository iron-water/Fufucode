# E2-T：终端输出操作与命令输入体验

推荐执行模型 `group/auto-deepseek-v4-1-flash`，难度L4。选择/滚动几何、Unicode、按键焦点与每终端状态关联，比普通功能页复杂；该B档模型成本低于S档，足以实现明确范围。备选 `group/auto-qmodel-38max`，仅原模型不可用才协调，不能自换。实现者不是昂贵验证lead，云模型验证如必要始终显式 `devin/swe-2`。

由M授权创建一个Codex Desktop隔离工作树会话，工作树是唯一写入位置，不回主仓库写代码。先完整阅读AGENTS、execution-contract、session-handoff-protocol、当前任务和floating-workbench用户语义。main产品基线bf7ad40ab9ce568f61da26e7a50d856645f09ac7；创建后的任务书提交只追加文档，若起点不是此SHA，核对它的祖先与src差异，不同步未来main/重置。先报告实际路径/分支/HEAD/起点/继承状态。不要求一小时完工，不在用户退出/按Escape时擅自宣告完成。

## 目标与当前缺口

真实源码已有ConPTY、VT有界屏幕/回滚、每tab输入草稿/滚动位置、停止确认，以及隐藏/移栏时保留会话。TerminalScreenControl只聚焦和转发字符，没有输出选择复制；Ctrl字母直接发控制码，没有清晰输出查找/返回底部入口；底部输入没有命令历史。此次交付一条完整终端使用路径：读旧输出 → 选择/复制或查找定位 → 回到底部 → 编辑/粘贴并显式运行命令 → 浏览当前tab输入历史 → 切tab/隐藏重挂保留状态。

不要求此包做新皮肤、浏览器或浮动外壳。页签/整辅助栏浮动由M的F1负责；这里只保持既有重挂/停放生命周期，不把关闭停靠页签当结束shell。主页面最底部极扁状态栏默认关闭，本终端自身工具栏不是该状态栏，不擅加全局状态栏/独立浮窗。通知三处投递不在本包。

## 唯一允许写入范围

- src/Fufucode.App/Views/TerminalDockView.axaml及同名.cs、Views/TerminalScreenControl.cs。
- src/Fufucode.App/ViewModels/TerminalDockViewModel.cs。
- 新src/Fufucode.App/Features/TerminalExperience/**，用于输出操作/选择/查找/输入历史等局部模型。
- src/Fufucode.App/SelfTest/TerminalSelfTest.cs，只加必要行为反例或修明确过时假设，解释旧假设；不放宽既有断言。
- 可选新增tests/Fufucode.App.Terminal.Tests/**独立无头测试工程；参考已有Library测试工程的真实Avalonia编译绑定及依赖隔离，不全局降警告。
- docs/records/E2-terminal-2026-10-12.md、忽略tmp证据。

禁止写MainWindow*、Settings*、Styles、共享快捷键/动作目录/模块注册、App.csproj/共享sln/自测注册入口、Core/Automation及控制CLI、ConPtyTerminalSession、TerminalBuffer现有VT解析器、Browser、共享progress/decisions/failure-registry、打包及reference。需要新增公共接线就交协调卡给M，不越权；本范围足够以已有终端入口完成局部体验。只读复用现有TerminalSnapshot，不重写终端模拟器/PTY或创建第二shell owner。

## 实施步骤与必须行为

1. 用真实代码确认现有滚动/键盘/IME/退出/重挂行为，保留工作区路径与实例身份；已有实现复用，不重做整个终端。
2. 输出选择复制：鼠标拖选、清晰复制入口、Ctrl+Shift+C；Ctrl+C有选择时复制，无选择时仍中断shell。中文宽字符、emoji代理对、宽字符续格、组合字符、跨行/空行与边界不能丢字/拆字/夹带ANSI。复制当时所选内容，输出重写/回滚截断不能把旧选择索引借给新文本。退出后仍可读取复制，不因shell结束禁用输出操作。
3. 粘贴进入当前tab命令草稿，不自动写入shell或发送；保留多行和Unicode，已有草稿以明确一致方式保留/合并，不静默截断。明确运行/Enter才走原发送入口。输入法预编辑/候选确认Enter不能提交；不重写ImeEnterGuard或系统快捷键。
4. 只查有界已缓冲输出，轻量文本查找、上/下匹配、没有结果反馈、可返回/关闭查找并恢复焦点；中文/大小写/跨tab输入状态准确。定位的是当前快照匹配，输出变化不拿旧匹配索引定位新文本；不无限缓存完整输出副本或引入每次render全量搜索。
5. 返回底部显式恢复FollowOutput；手动看旧输出不会被新输出抢滚动；每tab滚动/选择/查找/草稿保持隔离。搜索/选择不应向shell发送按键；关闭搜索不关终端或其它页签。
6. 每tab有界命令输入历史，上/下遍历保留原未发送草稿、重复项合理处理；只记录用户明确提交的非空命令，不保存原始输出/秘钥配置或上载，不擅自跨tab/跨项目共享。表面方向键仍传给shell，仅命令输入框按上下键浏览本地历史。
7. 320宽及明暗主题采用现有资源；工具按钮/上下匹配/返回入口可达，有正确Tooltip和AutomationProperties.Name。保持原close确认、隐藏不杀、重挂不重复订阅、关闭tab才Dispose的基线。

空白按最保守可用实现，并将选择规则/粘贴合并/搜索模式/历史界限写记录；不等用户确认。发现在许可范围外的真实缺陷主动协调，并继续独立可做项。

## 验收与证据

- dotnet build src/Fufucode.App/Fufucode.App.csproj --no-restore与必要方案构建：零警告零错误。
- Terminal既有定向检查及新增必要反例：真实TerminalSnapshot、实际控件绑定/按键/事件路径，覆盖宽字符/跨行、输出变动不借旧位置、按键复制与中断分流、粘贴只入草稿、输入历史草稿返回、两个tab隔离、搜索返回、退出仍复制、回滚/重挂不重复订阅；不可只测新helper返回同一helper预期。
- 改App源必须跑默认全量--selftest及实际已有终端场景，不禁用其它分组/降警告/放宽断言。构建与日志显式工作树workdir，临时证据标绝对路径。
- 真实ConPTY仅自有临时shell/目录下的无害输出与退出，不执行用户仓库命令或终止用户进程；VT解析器不改，不用mock称为真实PTY。
- 原生指针/真实clipboard/IME/DPI/像素须单独证据。当前M记录sky RPC未配置；本实现包不为补验另开昂贵lead/多次相同探针，不用直接PowerShell UIA替代computer-use技能证明原生通过。无头/编译绑定和进程证据准确区分；原生缺项如实留给M后续最低成本串行验收，不整条停工。

每个可验证增量独立提交并更新本任务记录。原继承修改保持未暂存、不覆盖、不夹带；不push/amend/改历史/清理或删worktree、不登录采购/改全局配置、不自行同步或整合main。不启动下一任务、其它用户chat或后台生产调度。

最后主动交完整卡：任务/软件/L等级/模型、真实树/分支/起点/最终HEAD、所有提交及文件范围、继承保护、实际命令/检查数量/日志、真实PTY与无头/原生分层、未验与原因、公共接线需求、状态及是否停止写代码。写本记录并在最终回复完整展示，明确停写。M只在终态卡与停写核实后独立审查，再决定收编/接线；整项原生验收不能自行标通过。
