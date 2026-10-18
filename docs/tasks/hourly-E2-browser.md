# E2-B：浏览器基础与浮动生命周期

推荐模型`group/auto-deepseek-v4-1-flash`，难度L4。M已定架构边界/宿主约束，低成本B档执行明确App浏览器范围；涉及多个原生controller、父HWND及初始化竞态，比普通页面复杂。备选`group/auto-hy4-preview-f`仅原模型不可用或M明确扩大难度时协调，不能自换。模型测试/试跑如必要显式`devin/swe-2`；本包正常行为验证不用云模型。

M授权在一个新的Codex Desktop隔离工作树执行；唯一写入位置为创建后的实际树。产品冻结main6244c9157e9c6d18d4e6d16eed59e7ff1a2cd65b，后续只有任务书/记录提交可作起点。先读AGENTS、AI_DEVELOPMENT_PLAN、execution-contract、session-handoff-protocol、floating-workbench与本书；核对真实cwd/HEAD/祖先/src差异/继承改动。不得回主区写代码、同步未来main、重置/改写历史。发现起点不含冻结产品或任务书先报协调卡，不盲目复制或重发创建。

## 完整交付路径

目前浏览器已能地址导航、后退前进/刷新停止、单页本地预览与模块停用/状态恢复。此包做一条实际浏览路径：打开两个独立页签 → 切换/关闭/重开 → 地址和历史不串页 → 页面查找/缩放/复制地址/外部打开/开发工具 → 功能页浮动及整辅助栏浮动后往返停靠 → 退出/模块停用释放、恢复可用。当前Codex浏览器高级Agent观察操作另属E8，不把本包称为全部能力看齐。

先修F1-BROWSER-PARENT-1：现有BrowserViewHost脱离后异步Release仅在VisualRoot为空时执行，同批挂到另一Window可能保留旧父HWND。官方本机WebView2 SDK XML声明controller.ParentWindow支持重挂。必须核实真实parent、更换父窗口、Bounds/visibility/position通知；同时处理environment/controller创建期间跨窗口或Dispose造成的旧回调。不能只移动Control或只看IsReady就称真实跨窗成功。

## 唯一允许修改路径

- src/Fufucode.App/Views/BrowserView.axaml、BrowserView.axaml.cs、BrowserViewModel.cs、BrowserViewHost.cs、BrowserView.SelfTest.cs。
- src/Fufucode.App/Infrastructure/Browser/**；复用现有地址白名单/文件预览/profile路径，避免更换存储根。
- 新src/Fufucode.App/Features/BrowserExperience/**用于页签/查找/缩放等局部状态。
- src/Fufucode.App/SelfTest/NativeBrowserModuleSelfTest.cs；已有Program的原生模块工装可扩充行为，不改其入口或SelfTestRunner。可新增tests/Fufucode.App.Browser.Tests/**独立工程，依赖隔离参照Library，不降全局警告。
- docs/records/E2-browser-2026-10-12.md、本树忽略tmp证据。

禁止MainWindow*、Settings*、Styles/共享外观、公共动作/快捷键/模块注册、App/Program/项目文件/共享sln/SelfTestRunner、Core/Automation/真实harness、终端、全局progress/decisions/failure-registry、打包及reference。共享动作/设置接线由M处理；缺接线可交有效模块，不越权。M同期实施通知迁移与共享外壳，文件不并行。

## 必须行为与设计边界

1. 页签稳定ID/独立VM及导航历史、标题、地址草稿、查找/缩放；切换不会回写另一tab。关闭只释放对应原生controller，留下最后一tab的清晰空页；单tab与其他tab状态不丢。弹出新页面请求进入明确新tab，禁止静默覆盖原tab或另造宿主。
2. 隐藏/页签停放保留当前页状态与实例；独立窗与主窗间移动必须实际更新controller父句柄。优先保留已有controller以保DOM/历史；如运行时确需重建，明确哪些瞬态不可保留并记录真实证据，不宣称无损。环境/控制器初始化等待期间的切tab、改父、停用、关闭不能产生孤儿HWND或旧事件覆盖新tab。
3. 页内查找有query/前后/无匹配/关闭返回，缩放有增减/100%/有界值，复制当前真实地址和显式外部打开、开发工具入口有真实实现和不可用理由；320DIP与浅/深主题下入口可达，更多操作可折叠菜单。单纯按钮接空函数不算实现。
4. 保留自由导航与本地预览地址白名单、草稿保真，坏URL/路径不存在/超大或不支持文件给可返回的状态，不暴露任意本机host对象。默认不自动下载、不自动登录/提交表单；本包不新增下载管理、自动点击网页或供应商AI工具桥，原拦截策略保持。外部打开/DevTools只能用户操作触发，测试使用隔离夹具。
5. 原BrowserView.ViewModel/HostForTesting/CaptureState/RestoreState、WorkbenchModulePreferences入口仍需兼容已有调用；新增版本化多tab状态，旧v1能恢复，未支持版本保留诚实降级。若旧断言假设新版本“必不支持”已过时，解释为何并改成真实未支持版本，保留旧配置/坏数据反例；禁止降低恢复/释放/不串tab断言。
6. 页面侧可提供动作/键盘匹配接缝（独立回退明确）；交M准确动作ID/方法/作用域/禁用原因与默认建议。不得私改共享表或宣称默认硬编码等于可配置；浮动窗口最终按M同一公共绑定。

## 必要验证与证据

- 每一可验证增量小步提交。App及solution构建0警告0错误；默认App --selftest全量0失败，独立页签/生命周期/地址状态真实控件反例通过。不要并行编译和运行同输出DLL，Windows锁会造成MSB3026。
- 阅读已有NativeBrowserModuleSelfTest及Program入口，扩充真实WebView2、本地HTML/临时loopback站点验证：两个tab不同内容/历史、实际parent HWND变化、跨窗DOM标记与原页历史、隐藏重开/Dispose/初始化期间关闭，所有数据仅临时根，不改真实profile/登录态。工装驱动App API/SDK的证据单列，不能冒称原生用户指针/IME/DPI。
- 原生人工操作如需computer-use，先读完整技能并重新获取真实唯一窗口，测试lead最低成本；目前sky RPC通道未配置，缺工具如实blocked，不用UIA错误窗口/截图当原生通过、不升级昂贵测试lead。此单项阻塞不得停实现和可用SDK测试。
- 实際父句柄/页面URL与DOM值/前后controller身份/订阅释放均须保留证据；IsReady或Mock事件本身不证明网页在正确窗口。未能运行的SDK/native用例不能报PASS。错误/取消/失败后可继续返回既有页。
- 写本任务记录：真实范围/继承保护、命令/结果/日志、红绿问题、未验与原因、共享接线请求。默认不push、不删除/清理树、不接管未知进程、不付费/公开部署。模型probe如必要显式免费devin/swe-2，普通dotnet/SDK无模型调用不启动测试lead。

完成主动交完整卡（任务/软件/L4/模型、树/HEAD/起点、所有提交、真实验证、未验、接口与待M接线、停写确认），并停写等待M独立审核。失败修复保留同chat/模型/树，不能自动开始下一任务。
