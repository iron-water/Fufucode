# F1 第一增量：通用浮动页签与整辅助栏

M在实际main单写，起点31dbbf4；不占新的执行槽、不动受管E2 ba55或继承用户文件。按floating-workbench用户最新语义增量扩展，不推翻Windows设置页与左右辅助栏基线，不使用Opus额度、不移植macOS、不回溯改P2固定候选。

实际main实现SHA4275373；第一增量默认App全量840项ok/0失败、exit0，29条F1定向检查均包含在全量中，最终solution0警告0错误。E2模块随后收编及其公共动作另有独立集成记录，不把后续提交回填成这个增量的原始冻结版本。

实际功能：页签右键“浮动此页”、栏标题右键“浮动整栏”；复用同一DockRegionView及原Control，不复制业务VM。浮动窗口支持原栏/左/右/底停靠、页签顺序/选择/固定、添加功能页、独立置顶、隐藏后原窗口重开。X关闭是隐藏展示，终端tab/草稿不结束；真正主窗口退出统一关闭展示窗口并走原业务释放。整栏浮动后原栏释放空间；页签与整栏都可再停靠。浮动键盘路由使用该窗口当前焦点与共享绑定表，IME组合不吞动作。

同一WorkbenchLayoutState新增Floating容器列表，含稳定ID、原区/插入位置、顺序/选择、隐藏/置顶、DIP宽高及像素坐标。旧配置缺省空列表；恢复过滤未知/重复页面与窗口ID，尺寸有界，窗口重开按当前显示器工作区夹取位置。实际UI偏好保存/正常关闭/新MainWindow重开已定向检查；不是仅JSON字段测试。自测独立临时数据根，绝不写真实用户偏好。现有外观主题、accent资源、字号/页签密度带入浮动窗，系统原生标题栏未重做。

关闭与恢复期间两项新缺陷先复现后修：

- F1-OWNER-CLOSE-1：将所有Closing都当用户X隐藏，Avalonia先询问owned窗口，M主窗口Closing根本不发生。tmp/m-f1-close-red.log记录ownerVisible=True/ownerClosingEvents=0。只拦WindowClosing，OwnerWindowClosing/应用退出放行；忠实退出断言绿后ownerVisible=False/closingEvents=2，业务异步关闭未被绕过。
- F1-RESTORE-OWNER-1：构造期恢复浮动布局时owner尚未显示，却Show(owner)，全量出现Cannot show window with non-visible owner。保留tmp/m-f1-selftest-full.log失败证据；构造期先挂内容，主窗口Opened才显示，新增新窗口恢复真实页归属反例。没有删配置、吞异常或放宽可见断言。

命令全部显式workdir主仓库，无模型请求：

- dotnet build src/Fufucode.App/Fufucode.App.csproj --no-restore -v minimal：0警告0错误，tmp/m-f1-final-build.log。
- dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest --floating-workbench-only：29项PASS/0失败，tmp/m-f1-final-target.log。包含真实菜单、多个Window/Control移挂、关闭重开、整栏移动、终端原tab与草稿、辅助对话原控件、可选审查模块释放/重建、默认关闭状态栏、主退出和持久恢复。
- 默认App全量：0失败、exit0，tmp/m-f1-selftest-green.log，保留首次失败日志；最终solution构建0警告0错误见tmp/m-f1-final-solution.log。

边界：这些是真实Avalonia控件的无头验证，不能冒充原生指针/系统剪贴板/IME/DPI/多显示器或终端运行中跨窗口证据。BrowserView仍会在脱离视觉树重建WebView2控制器；同一业务页控件不等于原生DOM瞬态、登录/导航无损，必须按E2-B独立实测。主窗口浮层只遮蔽主窗口WebView，不误遮独立浮动浏览器。

进一步源码核对发现BrowserViewHost延迟Release仅在回调时VisualRoot仍为空才释放；同批移挂到另一个Window可能跳过Release，既有controller仍持有旧父HWND。这是必须处理的F1-BROWSER-PARENT-1风险，当前不能声称真实浏览器浮动已经可用。本机SDK XML明确ParentWindow可重挂，下一浏览器增量需真实父句柄更新/初始化竞态与跨窗页面恢复验证；没有COM/native实测，不将阅读源码标为缺陷已修。

F1尚未整项完成：旧专用浮窗与通知/用量入口迁移、三渠道和未读页签提示、旧floating-window.json版本化迁移、拖出窗口自动浮动、原生焦点/快捷键/浏览器与跨DPI仍待下一增量。暂时保留旧浮窗实现与历史断言，满足“新机制验证后才撤旧入口”；不继续扩展旧形态。主页面极扁状态栏默认关闭不变，用量入口不依赖它。浏览器/皮肤/手机等独立事项继续按拓扑推进。
