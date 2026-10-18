# 失败登记册（failure-registry）

### RC-AUDIT-20261017 ｜ P1/P2 ｜ fixed-targeted ｜ 手机远控鉴权、连接与草稿缺陷
- WebSocket令牌过期仍推送、未认证连接积压事件、无限发送队列、跨站配对/DNS重绑定、启停竞争、HTTP半包处理已修复。
- 手机配对旧回调、重连在途标记、发送失败丢草稿、Web停止假成功、真实审批与历史接线已修复。
- 证据与未验证边界见 `remote-control-audit-2026-10-17.md`；定向回环130断言、Android31单元用例通过，不代表手机真机验收。App最终强构建被既有 TimelineEntryViewModels.cs:245 的CS1717阻挡，本轮未修改该文件。

### E7-NATIVE-RIGHT-INPUT-1 ｜ P2 ｜ open ｜ 原生右键验收用了不支持的参数
- 0ed8首卡6ee5b51将三次mouseButton:"right"无弹菜单登记为产品失败；M核停写会话实际调用与computer-use api.md，正确字段为mouse_button。先修验收调用并补原生复现，当前不证明产品右键回归。原free devin会话有限返工，原结果保留，见M-E7-native-review。

### E7-NATIVE-TEXT-FOCUS-1 ｜ P2 ｜ open ｜ 原生mock终端文本注入缺首字符，原因待归因
- 原截图显示echo→cho，M已看；工具click后立即type_text未单独观察focus。原树固定产品7deef34，待原验收者正确焦点后逐键/注入/粘贴及普通TextBox对照。不是IME/ConPTY证据，不忽略失败、不盲改产品。见原终端截图与M-E7-native-review。

### E7-SKIN-SEARCH-1 ｜ P2 ｜ verified ｜ 新皮肤入口无法用“皮肤”搜索到
- M公共接线后，设置分类/子页/外观搜索词沿用旧主题清单。真实新控件入口红例`tmp/m-cycle59-search-red.log`失败；同步三处词表后定向与全量验证，保持原导航/搜索语义。属于新增接线中的修复，不伪称旧发布版已有皮肤功能。

### E7-VALIDATION-BUILD-LOCK-1 ｜ P2 ｜ verified ｜ M并发构建撞上自己的全量自测DLL锁
- `tmp/m-cycle59-search-red-build.log`明确10警告/2错误，锁定者.NET Host PID50936是本轮自有全量自测。未冒称通过/杀未知进程；原自测自然结束后串行构建`tmp/m-cycle59-search-red-build-serial.log`0警告0错误。后续最终构建/定向/全量串行，不重复这一并发方式。属于验证安排失误，不归因产品。

### E3-APPLY-PAGE-RESET-1 ｜ P2 ｜ verified ｜ 插件安全应用重接后离开扩展页看不到结果
- 第57轮四项真实Pi红例`tmp/m-cycle57-page-red.log`：应用/更新/坏候选恢复/回退均重建MainViewModel默认Chat，原插件控件离树。上轮重新导航只是工装修正，并未修产品。本轮最小传当前AppPage，重接绑定前保页；39实际插件/33取消与切页/App全量1388零失败、solution0警告错误。维护中新导航不被旧页覆盖、预取消/拒绝信任保页、关闭不复活；移除工装补救后两尺寸帧M已看。仅此连续性缺陷verified，原生/当前源安装/完整E3仍待；详见M-E3-plugin-page-continuity记录。

### E3-RETURNED-REGISTRATION-RECOVERY-1 ｜ P2 ｜ verified ｜ 已返回却提交失败的登记缺少有限结束出口
- 第56轮原真实Pi记录缺结构化上下文/明确对账入口两个红，`tmp/m-cycle56-red.log`保留。原方法全生命周期OS持有锁、原返回/结构化身份/退出关联、当前未加载/实际空闲排他维护及目录CAS共同限定单token结束；文件/原回执/新选择/pending保持，独立对账历史不伪造应用成功。新47机制/编译页面及34真实Pi检查通过。初次像素两红为工装未重新打开真实扩展页，按实际挂树修而非放宽断言；修后两尺寸M已看。仅正常已返回登记出口verified，未知/取消抛错/旧无证据、跨进程崩溃、原生/当前源安装和生产整项未验。见M-E3-returned-registration-reconciliation记录。

### E3-ORIGINAL-EXIT-ASSOCIATION-1 ｜ P2 ｜ verified ｜ 原请求无法核对已观察的旧RPC退出
- 第55轮真实Pi安全重启原返回记录有token/context却无退出关联，新实测一FAIL见`tmp/m-cycle55-exit-red.log`。Core保真实关闭后的退出观察，宿主固定原coordinator并按请求前结构化generation关联，服务二次核对后写可选事实；旧schema缺省不伪造/改写，错代次拒绝。7 Core/12新增App规则与19真实Pi（含二次重启/坏候选恢复）证明限定增量。原RPC退出不证明全部持有者/后台副作用终止，不解锁原请求保护；详见M-E3-original-process-exit记录。

### PI-CONCURRENT-SHUTDOWN-1 ｜ P1 ｜ verified ｜ 重复Stop提前完成并允许Dispose关闭仍使用的句柄
- 第54轮自有Node在stdin关闭后仍活着，重复Stop却完成；混合Dispose使首个Stop抛InvalidOperationException，正常/强制退出合计五红，`tmp/m-cycle54-shutdown-red.log`保留。改共享Stop/Dispose终态Task，缓存真实generation/PID/退出码；26屏障检查含16线程池调用、原请求失败传播/多次释放，Pi全量48通过。仅此RPC生命周期缺陷verified，单进程退出不是原插件所有持有者/副作用终止凭证，不解锁InFlight释放。详见M-Pi-shutdown-barrier记录。

### E3-CALLBACK-COMMIT-GAP-1 ｜ P1 ｜ verified ｜ 宿主返回后目录提交失败无法持久核对返回事实
- 第53轮实际目录独占锁使CompleteAsync失败，宿主已返回成功、调用1次、原InFlight/pending保留且无回执；原产品缺独立返回记录，`tmp/m-cycle53-journal-red.log`一FAIL。新原子不可覆盖记录在目录提交前绑定原token/version/time/宿主上下文；重开可分别看原返回与当前Unknown，原目录字节不改、不认证目录成功、不重发或解除保护。24新机制/两尺寸编译页检查通过，15真实Pi另核原返回身份；只此观测缺口verified，不是进程终止、安全释放或生产验收。详见M-E3-callback-journal记录。

### E3-INFLIGHT-NO-RECEIPT-1 ｜ P2 ｜ verified ｜ 未收到回执的在途请求无法按原标识只读核对
- 第52轮真实未返回宿主回调有持久InFlight token/version/time且无回执，原编译页只有版本保护提示、没有请求列表，`tmp/m-cycle52-entry-red.log`一FAIL。新增同页原身份投影及精确token只读观察，保目录字节/选定/pending/回执/保护、无重发或Abandon。同版本换请求迟到/登记结束Stale/未知宿主/篡改及两尺寸绑定与像素检查通过；此条仅只读可用性缺口verified，实际异常退出后的安全释放仍pending。证据与最终门见M-E3-inflight-inspection记录。

### E3-RECEIPT-PIXEL-1 ｜ P2 ｜ verified ｜ 回执核对结果延迟滚动后移出可视区域
- 第50轮compiled绑定/viewport几何通过、真实Pi只读核对通过；M查看最终`receipt-page.png`仅原回执详情可见，未捕获新结果面板，强制无头tick亦如此。未确认是捕获时序还是可视缺陷，不能标像素/原生验收通过；保图与`tmp/m-cycle50-frame.log`，后续实际渲染/原生核查，记录M-E3-receipt-inspection。
- 第51轮稳定后强断言在原产品一FAIL：Offset505→428、结果Y504→581越过视口底555，Extent/Viewport不变；内部触发源未证实。只把核对按钮/结果移到历史/详情之前，保原详情滚动访问与不改写契约；18回执断言、最小窗口及实际两PNG检查通过。本条仅无头像素缺陷verified，系统原生输入仍未验。红绿证据 `tmp/m-cycle51-scroll-red.log`、`tmp/m-cycle51-offset2.log`、`tmp/m-cycle51-final.log`，详见M-E3-receipt-viewport记录。

### E3-RECEIPT-INSPECTION-ENTRY-1 ｜ P2 ｜ verified ｜ 原unknown回执不可在插件页查看核对
- 既有catalog已保原token/版本/结果，页面没展示。M真实编译页独立反例`tmp/m-cycle50-receipt-red.log`一FAIL。同模块添加回执列表/原详情与精确token只读当前加载观察，不改unknown/选定/pending、不重发/解除保护；14默认机制/绑定及2新增真实Pi目录字节不变检查通过。原生/残留在途明确释放/本源安装另验，见M-E3-receipt-inspection记录。

### E3-LATE-CANCEL-COMMIT-1 ｜ P1 ｜ verified ｜ 真实恢复完成后仍提交已取消候选
- M在真实Pi Resume已返回、启用选定提交前加入只暂停时序的接缝；`tmp/m-cycle49-late-red.log`四FAIL：晚取消仍发布/仍live/无恢复，晚关闭仍发布。宿主提交前检查取消/disposed、释放未提交候选，沿原异常链还原Manager配置；原强断言及明确恢复绿，27取消/关闭/信任检查与原12安全切换通过。仅该提交前边界verified，提交后取消/unknown核对、原生/本源安装另验；见M-E3-late-cancellation记录。

### E3-CANCEL-RESTART-1 ｜ P1 ｜ verified ｜ 取消仍释放旧进程并留下候选/无限进度
- M真实Pi反例`tmp/m-cycle48-cancel-red2.log`：预取消连接/配置、初始化取消配置/进度、关闭中取消配置共五FAIL；末尾信任夹具错误单独保留，不冒称产品回归。宿主可取消入口门、异常恢复原运行列表、外壳明确恢复出口及Dispose/Apply同门修复；最终18取消/关闭/真实信任检查、原12Pi安全切换通过。验收只该边界，晚成功关闭/回执核对/原生输入/本源安装仍待验，见M-E3-cancellation记录。

### E3-STARTUP-OFFLINE-1 ｜ P2 ｜ verified ｜ 启动遮罩挡住本地插件入口
- 原入口只有会话成功后的扩展页；无项目/启动失败时无法管理本地副本。M先加独立产品按钮断言，`tmp/m-cycle47-startup-red.log` 两FAIL。启动页复用原控件/VM/目录，明确返回、不创建mock或重试引擎；29新启动+23原共享、12真实Pi外壳与App1255零失败通过。验收限该增量，原生选择器/IME/DPI与当前源安装仍未验，见M-E3-startup-offline记录。

### E3-RESTORED-CHAT-WIRING-1 ｜ P1 ｜ verified ｜ 失败插件恢复后外壳仍指向退出的Chat
- M真实MainWindow+Pi路径红 `tmp/m-cycle46-shell-real-red.log`：宿主已恢复实际旧版本、目录确认Active通过，但UI MainViewModel.Session.Connection不是Connected。原ApplyExtensions失败分支未Attach恢复后的会话。新增重接真实连接，原八检查最终全绿 `tmp/m-cycle46-shell-real-final.log`；原生IME/DPI与长任务取消未验证。

### R1-SANDBOX-OCCUPANCY-1 ｜ P1 ｜ open ｜ 新版候选安装门被未知Sandbox实例占用
- R1-REVALIDATE终态4097328已停写；本轮新1.4.4 setup安装/启动/卸载未执行。M只读核对WindowsSandboxServer PID15712/出生10:37:58，父22708消失，未证归属；wsb list空不能代替空闲证明，不停止未知实例。defer/not_integrated，原qf只在槽释放后有限续做这一门，保新/旧工件。包内三轮1203零失败与CLI门不冒称安装通过；独立E3继续。

### E3-APPLY-LABEL-1 ｜ P2 ｜ verified ｜ 文字应用按钮误用28DIP图标样式
- M查看本轮全量生成的扩展页真实无头截图，应用所选版本仅显示首字；WorkbenchStyles的toolButton固定28DIP。共享外壳按完整文字测量的新反例`tmp/m-cycle44-label-red.log`一FAIL。改用既有subtle文字按钮样式，不动共享主题基线；最终同反例与23条共享断言通过，App1226零失败，M查看最终渲染确认全标签；截图不冒称系统原生输入验收。

### E3-PICKER-LIFECYCLE-1 ｜ P1 ｜ verified ｜ 文件选择异常与关闭后迟到结果
- M共享接线实际红反例：目录/入口IO异常逃出异步命令、选择器迟到后仍写已释放VM。`tmp/m-cycle44-local-plugins-red.log`中三FAIL；增量修异常/取消诊断、序号与释放检查。增加换源迟到、互斥与取消检查，最终22集成零失败。原生系统对话框/IME未验证。

### E3-PREVIEW-VISIBILITY-1 ｜ P2 ｜ verified ｜ 失败预览未通知绑定且换源保留旧成功
- 首红日志第四FAIL：HasPreviewError/HasPreview派生属性未通知控件。邻接`tmp/m-cycle44-adjacent-red.log`一FAIL：源修改后CanImport/HasPreview仍为旧成功。修派生通知、输入修改失效和捕获输入/预览序号；同断言最终通过，没有放宽。见M-E3-offline-integration记录。

### M-WRITER-LEASE-TIMEOUT-1 ｜ P2 ｜ open ｜ 长验证轮初始main租约超时
- 第43继续轮最后查询初始进程exit1/Lease expired，尾部不能保证整轮持续持锁。main仍只有本轮cc44eee/40a3f41，索引空、继承demo状态保持，重新独占锁成功。没有据此回滚/改写提交。后续长轮须提交/编辑前检查活跃租约/续期，最后SHA白名单守卫不能冒充锁；补录保监督缺陷，不影响已实跑产品门但不宣称并发绝对保证。

### E3-RECEIPT-IDENTITY-1 ｜ P1 ｜ verified ｜ 迟到版本1失败回执被写成版本2
- 源ff6bab6，M服务156及原九反例通过，新增tmp/m-e3-receipt/results.log一FAIL：Apply1待回调，Apply2确认后旧1失败，LastApplyReceipt.Token为旧1但VersionId=2.0，污染归属。回执必须原token/固定version绑定；不以目录选定正确代替回执保真。原L3线程修复、未集成。

### C5-REFRESH-FOCUS-1 ｜ P1 ｜ verified ｜ 周期只读刷新禁用按钮清除键盘焦点
- M真实控件反例先BringIntoView再Focus，tmp/m-continue43-focus-red2.log：初始焦点PASS，await RefreshAsync后IsFocused FAIL。RefreshAsync设置IsBusy导致动作CanExecute禁用；手工和2秒timer同入口。第一次工装未滚动按钮而初始焦点就失败，保留red.log但不作为此因果证明。M产品修复和原生/包内边界另验。

### R1-PACKAGED-SELFTEST-STABILITY-1 ｜ P1 ｜ open ｜ 当前包内全量自测多次按键启动超时
- 源固定4de5151，R1交付a99693c：八串行实跑3绿5红，不同步骤focused=False、busy=False、enabled=True后ExecutionTask启动等待超时。原卡/三报告保红，不挑绿；M确认源码Focus紧接KeyPress仅作线索，未确认根因。公共SelfTest/App修复归M，worker包装允许范围不越权。详情M-R1-refresh-review-2026-10-13。

### R1-HOST-PAYLOAD-1 ｜ P1 ｜ open ｜ 新候选缺自动工作台执行宿主载荷
- M流式核对两ZIP15542文件，均无HarnessHost/Hosting/Control载荷；package只publish App/Cli。包内模拟自测不证明后台宿主在干净机器可启动。公共部署/运行发现由M收口，候选未宣称全自动可用；OMP运行时也仍缺。不能把这项推回受限worker擅改src。

2026-10-13继续轮纠正R1-HOST-PAYLOAD-1范围：Hosting/Control是Core里的命名空间，相关代码已在载荷Core程序集，独立DLL缺失不是缺陷；先前按名字判断有误。实际没有公开BackgroundHost启动入口，新CLI host serve/status/stop已实现并由源码及self-contained发布物各11检查、官方9测试证明。新候选需包含此增量后再验，原固定包不追溯改变；H1桌面会话/OMP托管仍未完成，旧条目保留纠正而不假称宿主全范围交付。

2026-10-13继续轮验证补录：C5-REFRESH-FOCUS-1产品读观察不再置IsBusy；正确可见焦点原红1→最终定向0失败，SQLite观察/切库三反例追加，最终同一self-contained Release clean PATH/新cwd-data连续3轮各1203项0失败。首修复第二轮切库2红已保，非挑绿。E3八finding最终服务166/忠实10全绿，模块accept/pending_wiring，不是公共功能完成。

2026-10-13第42轮：源200a36a，M独立Release重建0警告错误、服务125 PASS、原五断言不改全PASS。E3-VERSION-COMPAT-1/PENDING-REMOVE-1/REMOVE-FAILURE-1/STALE-APPLY-1在原反例范围verified；下文旧open段保留历史，新邻接缺口使模块仍rework/not_integrated，不能将原红删掉或称整项接受。

### E3-SUPERSEDED-INFLIGHT-1 ｜ P1 ｜ verified ｜ 被新决定取代的宿主回调仍在途却失去文件保护
- 源200a36a：Apply1回调等待，Apply2成功清token后移除1成功并删入口，第一回调未完成。真实一FAIL，tmp/m-e3-adjacent/results.log；保护所有在途候选或严格串行拒绝竞争，只保护最新pending不足。

### E3-CANCELLED-COMPLETION-1 ｜ P1 ｜ verified ｜ 宿主返回成功时调用方已取消，目录留下永久令牌
- 源200a36a：Apply回调取消token后返回Success=true，CompleteAsync吞取消报false；结果StaleOperation，自己的OperationToken与pending未清。两FAIL，日志同上；区分取消/结果未知与新操作取代，保原身份供核对，不自动重发。

### E3-FIXED-CONTENT-1 ｜ P1 ｜ verified ｜ 托管固定副本正文被修改仍按旧摘要应用
- 源200a36a：导入后改托管index.js，ApplySuccess且实际调用宿主。真实一FAIL，日志同上；应用/回退前核对内容/入口/路径声明，不更新旧摘要或执行损坏副本。

### E3-VERSION-COMPAT-1 ｜ P1 ｜ verified ｜ 固定版本共享最后导入的宿主兼容声明
- 源21eebc5：版本1仅支持1.1.0、版本2仅支持2.0.0；导入2后1在1.1.0被拒，改宿主到2.0.0却进入Apply回调。M两条真实FAIL，tmp/m-e3-review/results.log；兼容须固定到每个版本，回退不借新版本门。原L3线程修复，未集成。

### E3-PENDING-REMOVE-1 ｜ P1 ｜ verified ｜ 指定待移除一个版本却删除全部版本
- 源21eebc5：同插件1/2，快照未知明确移除1，恢复空快照ResolvePendingAsync连未请求的2也删。一条FAIL；只读快照核对不得自动删除，pending限定原目标。证据同上，仅临时副本。

### E3-REMOVE-FAILURE-1 ｜ P1 ｜ verified ｜ 文件锁导致删除失败仍报成功且丢目录记录
- 源21eebc5：入口被FileShare.None独占，移除返回Success=true、记录消失、文件仍在。一条FAIL；不得吞删除异常或先丢可恢复目录事实，失败/部分失败如实报告。

### E3-STALE-APPLY-1 ｜ P1 ｜ verified ｜ 旧宿主应用完成覆盖更新的版本决定
- 源21eebc5：挂起Apply1，Apply2先成功，再完成1，选定版本倒退1。一条FAIL；跨await绑定操作身份/版本及完成门，或明确串行竞争拒绝，短目录锁不足。宿主副作用/移除边界邻接补验。

### E3-OBS-1 ｜ P2 ｜ open ｜ 插件树全量自测一次focused=False
- worker卡忠实保存首次失败和重跑通过，M未重跑该全量且不把“无头抖动/非本模块引入”作为已确认根因。既有按键稳定性问题继续由M公共测试所有权处理；R1独立串行在核查同类现象，不放宽断言。

### R1-REFRESH-INTERRUPTED-1 ｜ P2 ｜ open ｜ 候选包执行会话被中断且无正式交付卡
- 第37轮08:51 UTC：监督API恢复，原恢复turn01a1343f再次interrupted/notLoaded，结束时间缺失/原因未知。后台包装日志止于Archiving、无已观察exit文件，不当正式交付或包通过。08:55 UTC候选相关dotnet/ISCC/App/Sandbox/pwsh包装进程0，HEAD保持d7d452f，五个包装脚本及LICENSE未提交内容保留。原会话原模型第二次明确继续，新turn01a1347a inProgress；要求保存后台PID出生与exit、复用完整门证据，不从零重跑/多起publish。仍open，无模型/系统原因推断。
- 2026-10-13 07:49 UTC监督wait证实原turn01a133b9-cdb4-7731-a62e-f1636034a7c6为interrupted/notLoaded，结束时间Unix1791870316，非completed交付，原因未确定；不存在可接受卡。原cb9f HEAD仍d7d452f，只有runtime/node/LICENSE未提交状态，numstat为空，不判为产品缺陷，不丢弃/夹带。07:52 UTC只读进程查询无cb9f/App-dotnet测试/WindowsSandbox/WSB匹配进程；原会话原group/auto-qfmodel已明确恢复，新turn01a1343f inProgress。保持open直到正式停写交付核实，恢复启动不代表任务或安装验收完成。

### C5-STALE-INPUT-1 ｜ P1 ｜ verified ｜ 外部待办变化导致正在编辑且尚未再保存的输入丢失
- `tmp/m-c5-stale-red.log`真实反例：保存原回答、再编辑Unicode多行回答，另一个Core客户端回答同待办；刷新把SelectedDecision置空，其回调清空输入，“保存当前输入”也无法保留最新文本。旧已保存草稿仍在，但最新输入实际丢失。修复保留原待办上下文/版本/token的临时只读输入，不重新绑定可执行旧决定；明确保存、丢弃临时缓存和scope切换独立验证。此前绿色没有覆盖这一边界，不能当全部恢复问题已验。
- 最终源码标准solution0警告0错误，App1198/C5 194全部0失败；新增9项实际控件/SQLite/文件与事件反例通过，证据 `tmp/m-c5-stale-final-{build,app}.log`。只关闭本项，其他旧根因项保持open。

### C5-RESOLUTION-AVAILABILITY-TEST-1 ｜ P2 ｜ open ｜ 对账表单CanExecute检查一次为false而随后真实提交成功
- 同红日志中旧“compiled native reconciliation inputs enable…”失败，随后旧证据拒绝、新核查、焦点Enter及实际Interrupted均通过。检查前正在运行两秒观察，可能与IsBusy窗口相关，但原失败未捕获瞬时诊断，根因尚未确认；检查改为等待真实空闲并追加busy/理由/依据诊断，严格CanExecute断言不放宽。后续绿不自动关闭本项。

2026-10-13 第32轮最终：公共草稿/原回执接线主线App1189/C5 185全部0失败；Control511、Hosting267、Core340、Pi21、持久化192通过，标准solution0警告0错误。旧池化fixture修复仅关闭工装连接池，严格字节断言保持；按键偶发和PID复用根因项仍open。证据见M-C5-persistence-integration-2026-10-13。

### C5-PERSIST-POOL-TEST-1 ｜ P2 ｜ verified ｜ 旧库字节保真工装的池化SQLite句柄在Dispose后仍占文件
- tmp/m-c5-persist-wire-pool-red.log保存新增旧库兼容用例实际IOException；构造无authority表的临时fixture使用默认Pooling=true，逻辑Dispose仍保底层写句柄，File.ReadAllBytes不能按原共享模式读取。改工装临时连接Pooling=false，物理关闭后仍严格比较客户端连接/拒绝提交前后原库字节，不修改产品共享规则或跳过断言。

2026-10-13 第32轮：原157d03b停写/范围核对后，M独立192项及原九反例全部0失败，新增opaque身份/null版本两项verified，限定合格存储模块，不当跨重启UI已接线。实际证据见M-C5-persistence-review-2026-10-13与tmp/m-c5-persist-round3-*，此前红日志保留。

### C5-RESOLUTION-PATH-1 ｜ P1 ｜ verified ｜ SQLite区分大小写的路径等式拒绝合法Windows原运行
- tmp/m-c5-reconcile-worktree-red.log实际全量新门后FAIL；原harness日志存大写规范worktree，线程保原大小写，SQL字符串等式使合法取消run查不到。只读fixture元数据确认两者同Windows路径。改双方完整绝对路径规范化后按OS路径语义比较，task/thread/stop出生绑定不放宽，真实错树副本仍必须拒绝；最终App1168/C5 164全量0失败、solution0警告错误。

### C5-PERSIST-OPAQUE-ID-1 ｜ P1 ｜ verified ｜ 修剪不透明项目与权威身份使不同scope恢复同一草稿
- tmp/m-c5-persist-extra-red.log真实三FAIL：不同project/authority原输入含合法首尾空格被Trim为同scope，实际保存后另一原scope读到草稿。原任务Ordinal身份必须完整保真，长度检查用原长度；原四修复verified保持，新增项由原L3会话限定修复。

### C5-PERSIST-NULL-VERSION-1 ｜ P1 ｜ verified ｜ 无版本知识草稿保存成功后整个scope读取失败
- 同独立日志第四FAIL：TodoVersion=null的合法草稿Save成功，但Load返回InvalidFileContent“todoVersion必须整数或null”。本轮原生知识提案可以是nullable版本，须统一缺省/null读写保真，不能强迫改0/禁止合法null；再次写入与scope中其他稿保持。未收编该模块。

### C5-KEY-SEQUENCE-TEST-1 ｜ P2 ｜ open ｜ 无头焦点按键偶发未触发实际命令，根因未完全确认
- tmp/m-c5-reconcile-command-red.log保留PressDecision等待新ExecutionTask超时；原工装只KeyPress无释放，补完整按键序列后首全量通过，但tmp/m-c5-reconcile-command-final-red.log另一全量Creation/重组Save再超时，故不能把“未释放”推断当已确认根因。补明确Activate目标Window再Focus按钮，Creation及Decision同样给busy/enabled/focus/active/目标身份诊断；必须实际新Task和严格旧断言，不直接Execute替代原键盘证据。最终绿色也不自动关闭根因项。

### C5-CREATE-BUSY-TEST-1 ｜ P2 ｜ verified ｜ 表单有效性检查恰逢正常观察而把CanExecute=false当绑定失败
- tmp/m-c5-reconcile-first-red.log为真实定向1 FAIL：Unicode输入已正确进入规范且随后真实按钮成功，检查CanExecute时产品IsBusy正在两秒观察，正确禁用提交。仅在该有效表单检查前等待真实空闲；Unicode原文与完整规范可提交两条件保持，不关闭观察/延长固定睡眠或修改产品门。最终App1168/C5 164全量0失败；另一个偶发按键根因项仍open。

2026-10-13 C5-PERSIST第二轮：M核对原会话停写/干净/HEAD5b546f2后，独立166断言及原五反例全部通过。下列四项verified仅指合格模块修复，未接原生UI或标C5整项验收。旧失败证据与rework决定保留，现accept/pending_wiring。

### C5-PERSIST-SCOPE-1 ｜ P2 ｜ verified ｜ 合法Unicode分隔符导致不同存储scope的文件名碰撞
- M独立链接交付0ebc1fa源码，tmp/m-c5-persist-counterexamples.log真实FAIL：两个不同元组(a∤C:\X,C:\Y,z)/(a,C:\X,C:\Y∤z)拥有相同ScopeHash。内部scope复核仍拒绝错配，没有跨库泄露证据；独立存储失败已证明。原会话L3返工，无收编。

### C5-PERSIST-PATH-1 ｜ P2 ｜ verified ｜ 驱动器相对和根相对路径被当完整绝对库路径
- 同独立探针证明C:relative.db及\relative.db被TryCreate接受并按cwd/当前盘补全，违反原任务书。须展开前判完整绝对性；完整合法路径等价不放宽。原会话修复。

### C5-PERSIST-FINGERPRINT-1 ｜ P1 ｜ verified ｜ 草稿指纹丢字段及候选元素边界导致静默丢修改
- 同探针真实Save/Find：合法␟文字跨Reason/Evidence移动、候选[a,b单个ID]变为[a和b两个ID]均返回成功而保留旧稿；已传真实generation。两项FAIL，须无歧义比较，禁止以禁合法内容/放宽断言制造绿。

### C5-PERSIST-GENERATION-1 ｜ P1 ｜ verified ｜ 写入可省略预期generation而绕过冲突门
- 同探针Save(scope,draft)未传generation却成功，原任务书每次写入须匹配预期值；原独立135断言没有覆盖该默认参数。全写入口须显式版本或明示拒绝缺项，模块待接线本身不是打回理由。

### C5-LIVE-BACKUP-TEST-1 ｜ P2 ｜ verified ｜ UI线程同步备份正在观察的数据库出现SQLITE_BUSY
- tmp/m-c5-stop-backup-red.log保存真实App全量异常：CheckHeldContractMutations内BackupDatabase抛Sqlite错误5。工装改Task.Run真实在线备份，仅对BUSY/LOCKED有时限重试，让UI观察事务完成；每份备份仍须证明原run和副本均Running，再核对原有三项严格事务拒绝。取消出生身份反例沿用该真实备份，不改产品状态或关闭刷新。最终App1133/C5 129零失败，原拒绝门通过。

### C5-STOP-TEST-1 ｜ P2 ｜ verified ｜ 原生工装把空闲标志误当按钮命令已完成
- tmp/m-c5-stop-cycle-red.log保留实际全量创建环拒绝断言FAIL；随后定向授权出现Sequence contains no elements。产品CanRefresh明确拒绝观察中重叠调用，工装PressDecision/Save只等IsBusy=false，可能在真正命令执行/完成前核查。改为捕获原AsyncRelayCommand.ExecutionTask，实际Enter后必须出现新命令Task并await它；直接VM命令边界等观察结束。保留两秒刷新和所有原断言，不以延时/停用观察制造绿。最终App1133/C5 129零失败。

### C5-STOP-START-TEST-1 ｜ P2 ｜ fixed ｜ 宿主异步启动前工装把尚不存在的run当已登记
- tmp/m-c5-stop-start-red.log显示StartAsync返回后立刻Runs.Get抛KeyNotFoundException；宿主后台尚未Claim。Until只对“尚不存在”继续等待，仍必须观测真实Running与writer PID；不把queued/未知视为启动通过。nullable探针引用已明确确认后读取，构建不降警告。

### C5-TERMINAL-PID-REUSE-1 ｜ P2 ｜ open ｜ 已完成运行的PID复用会保守阻断下一规范绑定
- tmp/m-c5-stop-ownership-red.log为本轮真实Control全量故障，IntegrationChecks182、旧control-BEC28F…记录writer33156；出生时间已不同的现存PID被RunReconciler判Unverifiable，绑定合法后续任务拒绝。当前实现与既有Hosting“复用必须不可证明”的断言一致，未放宽为完成/死进程；现场PID33156/11480后续只读查询均已退出，不能补造失败时证据。本轮保留门与日志，最终全量在隔离临时根复跑，绿不关闭该长期边界。需要单独明确terminal provenance及子写入者存活的受控对账路径；旧C3最早无诊断项也不因此关闭。

### C5-MUTATION-WRITER-1 ｜ P1 ｜ verified ｜ 同线程/同树额外写入者未阻断规范修订、知识采用及重组批准
- tmp/m-c5-create-mutation-red.log用实际held HarnessExecutor子进程时的SQLite备份分别检查define/adopt/restructure，三个真实FAIL证明仅查control绑定run会漏掉额外writer。共同追加writer门接规范替换与知识阻断（重组沿用）；检查真实同线程/同worktree Dispatching/Running/Unknown及其他queued/started请求，但仍允许明确废弃尚未启动的该规范原受管队列。严格反例及既有Control采用队列回归待最终验证，不手工伪造状态/杀未知进程。
- 最终App1105/C5 101、Control511/Hosting267均exit0；原三个反例通过，合法原未启动队列废弃保留。

### C5-CREATE-OPAQUE-ID-1 ｜ P2 ｜ verified ｜ 修订合法历史规范ID时错误修剪身份
- 新创建工装用Core原生登记带两端空格的合法ID，原UI Spec()统一Trim使修订请求指向另一个ID而被Core拒绝；tmp/m-c5-create-opaque-red.log真实1 FAIL。新增规范仍规范化输入；明确选中既有规范时，提交从原对象捕获精确ID，不按输入文字重建身份。未放宽Core期望版本检查或复制旧历史，原反例待最终回归确认。
- 最终App1105/C5 101中精确旧ID变为版本2且无trim后新对象，原断言通过。

### C5-STALE-TEST-1 ｜ P2 ｜ verified ｜ 过期决定用例等待布局时被正常刷新抢先更新
- 首轮高级重组用例在改变外部规范后才等待 Paint/查找旧按钮，正常两秒观察先删除 approve，测试抛 `Sequence contains no matching element`，不是产品绕过旧版本门。改为聚焦后、Enter 前注入外部状态变化，实际按键提交仍须拒绝旧请求；不禁用观察，不放宽断言。最终验证状态见 M-C5-decisions-2026-10-12。

### C5-REFRESH-TEST-1 ｜ P2 ｜ verified ｜ 测试调用忙碌的刷新后误以为读到最新提案
- tmp/m-c5-advanced-refresh-red.log 保存实际全量异常：选择 secondFact 时后台观察正在读取，RefreshAsync 正确按 CanRefresh 拒绝重叠，测试却立即从旧投影找新提案。测试等待真实 IsBusy 结束后发起一次刷新，再选择真实新对象；按钮验证同样等待可用，保留正常两秒观察，不改业务门或把缺项视为通过。最终全量回填于 M-C5-decisions-2026-10-12。

### C5-UNBOUND-WRITER-1 ｜ P1 ｜ verified ｜ 控制绑定外同线程写入者未阻断审核正文
- tmp/m-c5-active-red.log实际harness hold期间，旧ControlReviewWriterStopped只查control_writer_bindings，正文仍可见。统一纳入实际同线程/同worktree Dispatching/Running/Unknown及queued/started请求，接正文、直接审核、集成；同hold反例红绿，App1045/Control511/Hosting267通过，无手工篡改运行状态或杀未知进程。

### C5-PORT-1 ｜ P2 ｜ verified ｜ 部分按钮与曲线端口差1像素
- tmp/m-c5-port-red.log实际Arrange反例；坐标补明确1px边框，原严格误差小于1px断言通过161/161，没有放宽到2px或仅检查理论布局。

### C5-REFRESH-LAYOUT-1 ｜ P2 ｜ verified ｜ 无变化刷新重建画布造成坐标/焦点不稳
- tmp/m-c5-layout-refresh-red.log全量两项FAIL，实际按钮Bounds变0。仅图投影变化重绘，原节点/坐标/端口断言通过，未插入补偿等待掩盖反复重建。

### C5-REFRESH-DRAFT-1 ｜ P1 ｜ verified ｜ 清空绑定列表导致选择和理由草稿丢失
- tmp/m-c5-draft-red.log编译绑定反例FAIL；按身份/token/context/可用性复用稳定项与移动差异，不Clear全列表，图节点和输入文本保持。App全量1045中原反例通过；新身份仍清旧选择，不借旧核查审批新版本。

> 用途：全项目唯一的问题收敛池。三个入口——①验证套件失败（自动）②走查发现（agent 代用户体验）③用户一句话口述（协调环节编号）。状态机：`open → fixing → fixed → verified`（或 `regressed` 回炉）。
> 规则：修复必须连带沉淀一个探针场景或自测断言；本册只减不增。基线 main@6db4788，T0 体检 2026-10-05。

## T0 全量体检结果总览（2026-10-05）

| 验证组 | 结果 |
|---|---|
| `dotnet build Fufucode.sln` | ✅ 0 警告 0 错误（43.7s；一处 genruntimeconfig.cache 重试为瞬时文件占用，自愈） |
| App `--selftest`（mock） | ✅ 0 项失败 |
| `Pi.Tests` | ❌ 16 项协议检查 PASS 后崩溃于真实 Pi 段 → R-003 |
| `Core.Tests` | ❌ 崩溃于 CliUiTests，272 项主体未执行 → R-001 / R-002 |
| `pi-probe`（带 -NodeExe） | ✅ 9 PASS / 0 FAIL / 1 SKIP（SKIP=未给对照根，预期） |
| `bridge-verify` | ✅ 8/8（遗留 R-005 家务项） |
| `demo-verify` | ✅ 全 PASS（fixture-atria / gated / broken） |

**T0 历史说明**：当时 Pi.Tests 与 Core.Tests 均未跑完全量，不能引用"272 项通过"的历史数字。修复后的实际数字见下表。

## 修复后全量复跑（2026-10-05）

按 R-001 → R-003 → R-002 完成修复；首次 Core 全量在末尾暴露同源 R-003 裸启动，补齐隔离后重新全量运行。最终代码基线 `87e1e28`。

| 验证组 | 实际结果 | 证据 |
|---|---|---|
| `dotnet build Fufucode.sln --no-restore -v quiet` | 0 警告 / 0 错误，exit 0 | 最终构建耗时 7.27s |
| `Pi.Tests` 全量（无筛选） | 19 PASS / 0 FAIL / 0 SKIP，exit 0 | `tmp/registry-Pi-tests-2026-10-05.log` |
| `Core.Tests` 全量（无筛选、未设置跳过 UI） | 312 PASS / 0 FAIL / 1 SKIP，exit 0 | `tmp/registry-Core-tests-2026-10-05.log` |

数字按完整日志的 PASS/FAIL/SKIP 行计数，并核对进程退出码。唯一 SKIP 是 Atria 真实模型验收未启用/未提供所需凭据；普通真实模型对话开关也未开启，不把无模型 RPC 冒烟当作云模型验收。首次 Core 310 PASS 后崩溃（exit -532462766）的日志保留在 `tmp/registry-Core-tests-before-isolation-2026-10-05.log`，不计入通过结果。本轮未重新运行 App `--selftest`、pi-probe、bridge-verify 或 demo-verify 独立套件。

## 登记项

### R-010 ｜ P2 ｜ verified ｜ Windows PowerShell 5.1 工装给 Pi 输入添加 BOM
- 干净包 Pi get_state 超时；本机正式安装布局重现 Pi 返回 Unexpected token BOM，原因在工装 StreamWriter 编码，不在 Pi RPC。
- 仅验收进程 Console.InputEncoding 使用无 BOM UTF-8；同一正式布局 get_state 830 ms 返回成功、exit 0。最终 Sandbox 组合验收仍在 R-009 跟踪。

### R-011 ｜ P2 ｜ verified ｜ 朗读自然结束时输入读取挂起
- 新 SAPI 真实验收开始/暂停/继续/停止通过，自然结束超时；Console.In 同步包装阻塞 ReadLineAsync。
- 改为原始输入流的 StreamReader 异步读取，收到 done 关闭输入；真实静音 SAPI 五项全部通过。

### R-012 ｜ P2 ｜ verified ｜ 默认全量自测遗漏新工作台套件
- Workbench/Tabs/R05 仅 -only 分支可达；默认 --selftest 不包含。
- 默认入口纳入三组；实际默认全量 609 条通过、0 失败、exit 0，不减断言或放宽条件。

### R-009 ｜ P2 ｜ open ｜ 干净安装的 SYSTEM 会话无法验证原生窗口启动
- Sandbox 非交互 SYSTEM 安装 exit 0、16,538 文件哈希一致，但 MainWindowHandle 未建立；不算完整验收通过。
- 已取得独立 VM 的 WDAGUtilityAccount 交互会话，正在重跑真实安装、窗口启动、Pi 与卸载。尚未完成时保持 open。
- 当前阶段更新：旧包曾取得交互会话并完成安装/窗口检查，但未完成卸载；当前最终包验收受 R-015 客户端崩溃阻塞，不能沿用旧阶段证据。

### R-008 ｜ P2 ｜ verified ｜ Windows 设置文件短时占用导致原子替换失败
- 全量两次在不同设置写入场景复现 UnauthorizedAccessException；目标非只读，不据此归因杀软或改 ACL。
- 保持原子覆盖与成功后内存提交；Windows 共享冲突及非只读拒绝访问最多重试四次，总等待 500 ms。永久拒绝仍抛错并清理临时文件。
- 新增真实文件锁释放后持久化成功、只读失败且内存/磁盘不变测试；Core 全量 322 PASS、0 FAIL、0 SKIP，包含真实云模型。

### R-007 ｜ P2 ｜ verified ｜ CLI 预览依赖隐藏旧导航及 JSON 深度不匹配
- Core 全量暴露：旧 NavChat/NavExtensions/NavSettings 位于隐藏 LegacyCaptionActions，Invoke 被拒绝。
- 修复：nav.* 白名单映射产品共享动作注册表，不造可见按钮或绕过能力门；返回真实控件树和独立 navigation 能力元数据。
- 新控件树最多 40 个视觉层级，节点和 children 数组各占 JSON 层；生产导出/消费按 128 层匹配，保留 4000 节点/40 视觉层上限与未截断检查。
- 验证：CLI UI 定向 13 PASS / 0 FAIL，真实设置、扩展、会话页、PNG、拒绝覆盖及拒绝业务调用均通过。Core 全量继续复跑。

### R-006 ｜ P2 ｜ verified ｜ 定制输入区后抽屉避让坐标错误
- 2026-10-08 全量 B 基线复现：D6 抽屉底覆盖 composer；旧计算手动相加中间父容器位置。
- 修复：按 ComposerCard.TranslatePoint 获取真实相对页面坐标；隐藏的头/输入区不产生避让。
- 回归：D6 严格不遮挡检查，实际从无底栏 752 DIP 到展开底栏 507 DIP，与输入区上沿一致；App 全量 374 检查、0 失败。
- 旧套件另有过时容器、宽度、caption 与审查入口断言，逐条分类见 production-delivery-2026-10-08.md，不记作产品回归。

### R-001 ｜ P1 ｜ verified ｜ 无头 App 启动崩溃：nav.chat 重复注册
- 层：App（五区左栏）/ 影响：A7 无头 UI 控制面整体不可用，CliUiTests 连带崩溃。
- 复现：`Fufucode.Cli.exe ui snapshot --ui-app src\Fufucode.App\bin\Debug\net10.0\Fufucode.App.exe` → exit 2，stderr `An item with the same key has already been added. Key: nav.chat`。
- 证据：`tmp/cli-ui-499ae08463444b9883f06e2e22e0f212 space!/report.json`。
- 初判：Wave5-R1 左栏重构中新旧导航体系重复注册 `nav.chat` automation 项。
- 实际根因：`HeadlessUiRunner` 把图标轨与侧栏所有 AppPage 按钮建成同键字典。修复 `e074707`：只选择三个具名导航按钮。
- 沉淀断言：`CliUiTests` 的 `R-001 navigation automation IDs are unique across icon rail and sidebar`，要求三个 ID 各出现一次；定向 UI 12 PASS，最终 Core 全量亦通过。

### R-002 ｜ P2 ｜ verified ｜ CliUiTests 对无效输出无防护，崩掉整个套件
- 层：测试工装 / 影响：次生缺陷——把 R-001 的失败放大为未处理 `JsonReaderException`（CliUiTests.cs:49），掩盖全套件。
- 修复方向：解析前检查子进程 exitCode 与 stderr；非零/空输出 → 显式 FAIL 并附诊断，不抛未处理异常。
- 修复 `7f9f508`：所有成功响应解析统一先检查 exitCode/stderr/空输出，捕获畸形 JSON；UI 组预期失败输出显式 FAIL，保留诊断与报告，设置进程 exitCode=1 后返回，使 Core 后续组继续执行。
- 沉淀断言：`R-002 failed, empty and malformed UI output returns diagnostics without throwing`，覆盖非零退出（即使 stdout 是合法 JSON）、空 stdout 和畸形 JSON，检查退出码与 stderr 诊断；最终 Core 全量通过。

### R-003 ｜ P1 ｜ verified ｜ Pi.Tests 真实段无隔离，读入用户全局扩展后崩溃
- 层：测试隔离 / 影响：Pi.Tests 无法在本机完成；任何裸 Pi 冒烟同样暴露。
- 根因：Program.cs:78 裸查询只传 `--mode rpc --no-session`，未带 `--no-extensions` 也未设隔离 `PI_CODING_AGENT_DIR`；Pi 1.0.0 自动发现用户全局配置中的 `C:\AI\Harness\Pi\fix\pi-tool-visibility\extensions\tool-visibility.ts`，该扩展调用 `pi.registerToolRenderer`（1.0.0 无此 API）→ Pi exit 1 → 未处理 `PiRpcException`。
- 修复方向：冒烟参数对齐产品路径（`--no-extensions` + 隔离 PI_CODING_AGENT_DIR）。
- 修复 `025108d`：Pi.Tests 使用唯一临时目录、私有 `PI_CODING_AGENT_DIR` 和 `--no-extensions`；`87e1e28` 同步修复 Core 末尾同源裸 Pi 冒烟。
- 沉淀断言：`R-003 isolated real Pi ignores a failing auto-discovered extension`；私有自动发现目录放置加载即抛错的 `.ts` 扩展，真实模型列表/状态查询成功且无扩展报错。最终 Pi 全量 19 PASS，Core 全量 312 PASS。
- 附带记录：用户全局 tool-visibility 扩展与 Pi 1.0.0 扩展 API 不兼容，需在用户侧另行更新（不入本册修复范围）。

### R-004 ｜ P3 ｜ verified ｜ 探针工装两处缺陷
- (a) 场景 FAIL 时 `run-probe.ps1` 退出码仍 0——门禁不硬失败，编排器无法依赖退出码；(b) SDK source-loader 子进程不继承 `-NodeExe`，PATH 无裸 `node` 时该场景报 `null !== 0` 假失败（本次 FAIL 08 即此因，带参复跑 9 PASS 转绿）。
- 2026-10-08 当前实现传递 LASTEXITCODE、source-loader 使用 process.execPath；本轮 9 PASS/0 FAIL/1 SKIP。相同版本的非法对照触发 1 FAIL、exit 1，未声称跨版本验证通过。

### R-005 ｜ P3 ｜ verified ｜ bridge-verify 结果文件落点漂移
- 结果写到仓库根 `bridge-verify-results.json`，历史记录位置为 `scripts/bridge-verify/`。已顺手归位。
- 2026-10-08 按 import.meta.url 固定脚本目录；all 实跑 8 PASS/0 FAIL，exit 0。

## 已知债并入（原散落记录，统一编号追踪）

### R-014 ｜ P2 ｜ verified ｜ 打包验收误认 RPC 错误响应成功
- 工装仅匹配响应 ID，没有检查 type/command/success；同时共享 List 并发枚举存在竞态，stderr 未持续读取，非零退出未使整体失败。
- 修复：ConcurrentQueue 快照、结构化响应校验、持续 stderr 排空、单值退出码和失败门禁。`pwsh -File scripts/packaging/test-verifier.ps1` exit 0，负向失败/错命令/错类型响应均拒绝；最终包实跑另记交付记录。
- 去掉重复 Pi dist 后缺少目标目录创建，正式重建直接报错；补目录创建，不依赖旧产物残留。无 CLI 的 Sandbox 路径补真实 LogonCommand。

### R-015 ｜ P2 ｜ open ｜ Windows Sandbox 客户端崩溃阻塞交互安装验收
- Application Event 1000 三次记录 WindowsSandboxRemoteSession.exe 0.8.107.0 在 Microsoft.UI.Xaml.dll 3.2.0.0 中异常 0xc000027b，交互登录 90 秒不能就绪。证据 tmp/production-sandbox-platform-failure.json。
- 只停止本轮自建 Sandbox ID；不重置系统组件、不碰用户正在安装 Windows 的其他 VM。最终安装/启动/卸载门禁仍 open，换目录 clean PATH 不能替代干净 Windows 环境。

### R-016 ｜ P2 ｜ verified ｜ Compress-Archive 的文件共享方式中断候选包归档
- 当前包在 libHarfBuzzSharp.pdb 报被其他进程使用。后来共享读取成功；没有确定当时占用者，不归因杀软或用户应用。
- 改为共享读取 ZIP，复制过程中核对每文件 SHA-256 和清单本身；不一致拒绝结果，临时输出失败时清理，已有有效 ZIP 不先删除。
- 定向测试模拟持有写句柄但允许读取：归档通过；改变源内容后拒绝归档、原 ZIP SHA 不变、无 partial 遗留。真实 prepared payload 15,480 文件 hash 全部一致并生成 ZIP，exit 0；最终带 notices 的包另记结果。

### R-017 ｜ P2 ｜ open ｜ 发布物许可声明仍有核查缺口
- Node v24.15.0 官方完整 LICENSE 已取得并接入打包；运行时 NuGet 实际文本收集10份。36个依赖未在已核查位置找到许可文本，不能伪造或以表达式替代正文。
- 产品自身根 LICENSE 方案需所有者明确，agent不擅自选择授权；当前为本机候选包，不宣称公开再分发合规已完成。详见 labs/packaging/licenses.md 和最终 notices/report.json。

### R-018 ｜ P2 ｜ verified ｜ 图片附件自测依赖开发仓库工作目录
- 最终包移到 Temp 并使用私有空项目 cwd 后，自测在 WorkbenchCustomizationSelfTest:61 读取 assets/icon/icon-small-32.png 报 DirectoryNotFoundException。产品本身已嵌入该图标，失败来自旧工装的源码 cwd 假设。
- 改为读取产品同一 avares 嵌入资源并写入私有图片 fixture；不放宽文本/PDF/图片区分、去重、发送和失败恢复断言。不复制源码目录来掩盖依赖。
- 在仓库外 cwd 实跑修正后的默认全量：630 ok/0失败/exit0，tmp/production-portable-full.log；solution 0警告/0错误，tmp/production-final-build-v3.log。安装包据此重建，旧 V2 包不能作为最终自测通过证据。

### R-019 ｜ P3 ｜ verified ｜ clean-start 工装与自测截图写入载荷目录
- 独立 integrity Verify 原15,493文件：missing=0、changed=0，但发现21个工装创建的 logs/data/Node缓存等额外文件。不是发布物丢文件，不删除或忽略额外文件来制造通过。
- 修复默认布局为私有测试根下 package/，logs/project/data/home/tmp 为同级；App cwd 明确设为私有 project，不继承仓库。最终新布局实跑后再关闭。
- V3分离工装后默认全量630 ok/0失败，但载荷仍新增app/selftest-window.png；15,493原文件无缺失/变更。SelfTestRunner改用唯一临时根/evidence，仓库外全量0失败、构建0警告/0错误；V4严格校验待执行，不删除图片、不豁免额外文件。
- V4实跑关闭：私有cwd默认全量630 ok/0失败/exit0；Pi正常exit0、残留0。随后严格完整性checked=15493、missing=0、changed=0、extra=0，tmp/production-final-integrity-v4.log。没有删除自测证据或载荷额外文件。

### R-013 ｜ P2 ｜ verified ｜ 显式会话 ID 与请求项目错配
- RealSessionHost 恢复显式会话时未核对 ProjectId，可能把源会话与另一工作目录的宿主组合。
- 修复：启动 Pi 前核对元数据项目身份，不匹配则明确拒绝；工作树分支使用子项目和子会话对应参数。solution 构建 0 警告/0 错误，工作树真实 Pi 定向 9 PASS。

- R-101 ｜ P2 ｜ F4 遗留：1.3.0 发布包"探针未重跑/静默 lnk 存疑/跨版本数据兼容未做/GUI 性能未测"四项未验证（详见 docs/archive/progress/progress-f.md F4 节）。
- R-101 本轮逐项复核：当前 Pi 探针 9 PASS/0 FAIL/1 SKIP 已重跑；静默安装快捷方式的实际 target/cwd 检查已加入工装但待最终干净安装；Fufucode 旧新版数据升级兼容仍未验证（不能用 Pi 双向 fixture SKIP 代替）；GUI 性能仍未测。该历史债未整体关闭。
- R-102 ｜ P1 ｜ I3 决策稿：契约缺口① P0 立项待设计评审；缺口③后置（docs/design/multiagent-collab-mvp-spec.md §7）。

### R-020 ｜ P2 ｜ verified ｜ D1 OMP 协议差异与身份恢复
- Pi 的 get_commands/clear_queue/agent_settled/session-id 不能直接用于 OMP 18.8.5；OMP prompt ACK 后查询 idle 也可能提前宣称结束。
- 按真实协议适配命令发现、原子停止队列、prompt_result/session_settled；单独持久化 backend/engine ID 并分目录，旧记录默认 Pi，缺失 OMP 不替换 Pi。
- D1 真实 Pi 1.1.0/OMP 18.8.5 定向 28 PASS/exit0；既有持久化/审批定向 63 PASS/exit0；详见 docs/records/D1-backends.md。未宣称任意版本兼容或全仓回归。

### R-021 ｜ P3 ｜ fixed ｜ D1 原生 OMP 界面身份与空 widget 通知
- 原生 OMP 新建/允许审批过程中发现空状态仍写 Pi，以及无 widgetLines 的 setWidget clear 暴露原始 JSON。
- 空状态消费统一后端身份；无内容 OMP widget clear 不显示。原生再建 OMP 已确认“真实 OMP 会话”；clear 分支代码核对，未单独强制重现并截屏，因此保留 fixed 而非整体 verified。

### R-022 ｜ P3 ｜ verified ｜ 审批超时工装假定双端时钟同步
- M 收编 D1 后 Core 全量在 real bridge timeout 断言失败；实际 timeout/批准结果.txt 不存在，失败来自审批待清理状态与文件结果被合成一个同步断言。
- 官方受管 Pi rpc-mode 的定时器在输出请求前启动，桌面接收后独立启动到期任务；agent_settled 也不应清除其他并发审批。旧假设“引擎结束即桌面定时器已清理”不成立，不归因 D1 产品回归。
- 保留结束时立即验证文件写入决定，另有界等待最多 5 秒验证桌面审批关闭并再次检查文件；不会把拒绝后的写入或永久残留审批视为通过。新增 --bridge-only；主区定向实际通过，完整重跑结果见 M-D1-acceptance 记录。

### R-023 ｜ P1 ｜ verified ｜ O3 文本资料包再导入静默丢内容
- M独立诊断：SessionSnippet31字符导出再导入，Imported=1但预览0字符。ImportBundle传Path而文本路由消费Text。原90项未覆盖；O3未收编，交原会话修复并补文本类型/内容/哈希保真断言。证据见M-O3-review-2026-10-10.md。

### R-024 ｜ P2 ｜ verified ｜ O3 页面临时脱离销毁VM导致重挂失效
- M真实无头视图移出重挂后服务2条而视图仍1条。OnDetachedFromVisualTree释放VM，重挂从DataContext重新使用已释放对象。与工作台换栏生命周期不兼容；原会话修复并补视图重挂测试，最终Detach仍须释放。证据同上。

### R-025 ｜ P3 ｜ verified ｜ O3 测试工程降级程序集冲突警告
- 交付Library.Tests用MSBuildWarningsAsMessages降级MSB3277；未降级的M独立引用工程实际报WebView2 WPF/WindowsBase冲突。需移除未用传递引用并取消警告降级，不将隐藏警告作为零警告验收。仅测试工程边界，不归因产品App构建。
- 2026-10-10 M修复轮复核关闭R-023/024/025：正式测试工程真实剥离引用后重建0警告0错误，130断言通过；M旧反例文本31→31且重挂2→2。收编及公共接线见M-O3-integration-2026-10-10.md；原生与聊天门禁仍未通过，不代表O3整项验收。

### R-026 ｜ P2 ｜ verified ｜ O1 错误配置结构被视为合法并覆盖
- M独立复现bindings为数组时LoadError=null、Writable=true，后续保存覆盖原文件。应严格解析并拒绝覆盖，保持合法配置能力；O1未收编。证据见M-O1-review-2026-10-10.md。

### R-027 ｜ P2 ｜ verified ｜ O1 录制器保留已释放修饰键
- Ctrl按下后释放，再按K且当前KeyModifiers=None，实际仍Captured Ctrl+K。当前修饰状态须权威，补释放状态与页面事件路由测试；原93断言未覆盖。

### R-028 ｜ P3 ｜ verified ｜ O1 候选与全表冲突规则不一致
- Chat改绑Terminal已用Ctrl+K，Status称冲突，但Conflicts=0、行无冲突。DetectFor未按范围过滤而Detect按范围分桶；统一规则和提示，不静默覆盖。
- 2026-10-10 M复核R-026/027/028修复：143定向通过、原反例分别拒绝保存且保留原文、无幽灵Ctrl、跨范围提示一致。公共接线与未验项见M-O1-integration-2026-10-10.md；原生M1未关闭。

### R-029 ｜ P2 ｜ open ｜ OMP创建工作树内存不足并留下空检出
- Windows1455发生于git commit，随后checkout建索引失败；现场提交空间余量约1.17GiB。三项新worktree只有注册而缺失2020文件。M核实最新目录仅.git、无用户内容后以单worker restore补全，git status干净、HEAD8cf3921；可直接进入启动OMP。系统压力未解除、OMP自动快照未重验，另两个失败目录保留。证据见M-worktree-recovery-2026-10-10.md。

### R-030 ｜ P1 ｜ verified ｜ O2缓存清理跟随junction删除数据根外文件
- M唯一临时夹具：缓存内junction指向外部哨兵；预览计量0字节，实际删除外部文件1项。清理递归不检查重解析点，字符串前缀检查不足。O2未收编，返回原OMP修复；证据M-O2-review-2026-10-10.md。
- 2026-10-10修复轮HEAD282bd8c：201通过、已有链接反例已修，但预览后普通nested子目录换junction仍删除外部同名victim.txt。快照执行只查文件自身，遗漏父链；R-030保持open，详见M-O2-repair-review记录。R-031旧反例已通过，未收编前不宣称产品整项已验。

### R-031 ｜ P2 ｜ verified ｜ O2旧预览删除新写入的未过期日志
- M先预览30天前日志，再写入并更新时间到现在，执行旧预览仍删除该文件。执行需重新核对保留期限与过时预览身份，不能扩大已确认范围；证据同上。
- 2026-10-10第三轮M独立关闭R-030/031：212定向通过、已有链接/预览后父目录链接/更新日志三个稳定反例全部保留受保护文件且删除0；已收编设置入口。不是恶意并发路径替换或原生/WebView2/ACL整项验收，证据M-O2-integration-2026-10-10.md。

### R-032 ｜ P3 ｜ open ｜ 桌面worktree工具无法识别搬迁后的当前仓库
- create_worktree(ref=7349405,name=unattended-s1)返回Not a git repository，而显式在C:/Projects/Fucode/Fufucode执行git worktree list与git worktree add成功。工具绑定旧cwd尚未修复；M绕过工具在真实仓库创建独立S1树，没有修改App配置或删除旧目录。见M-unattended-queue-2026-10-10.md；保留open，不以绕过当修复。

### R-033 ｜ P2 ｜ open ｜ OMP Gemini路由拒绝工具调用AUTO枚举
- 受管S1的magpie/group/auto-gemini-3-8-flash启动后HTTP400，供应商报unknown variant AUTO，接受auto/any/none。OMP退出1，worktree无改动，队列未生成交付。同任务恢复至DeepSeek开工备选，不修改全局配置、不把回退称修复，供应商协议仍open。证据tmp/agent-queue/s1-01/stderr.log和M-unattended-queue记录。

### R-034 ｜ P3 ｜ verified ｜ 旧受管队列正文关键词可能把非交付入队
- 旧Worker仅exit0且stdout包含“交付卡”即入队，否定句“没有交付卡”或被引用卡片同样匹配；这是传输误判，不代表M已接受产品。新版只取终态assistant的结构化delivery.card并校验身份/枚举/提交；agent-events.tests的prompt-only反例和35项验证通过，M审核也改为显式decision，不靠文字分流。已在途旧Worker不强制重启，其后续交付须legacy人工核查。

### R-035 ｜ P2 ｜ verified ｜ gate仅凭05.json存在跳过审核且未校验receipt身份
- 410009f直接把任意05.json视作已审；receipt/state/事件缺少联合身份核对。现校验明确review.decided身份/HEAD/decision，读取卡前核对writer/HEAD，Review再次绑定received/receipt/state；坏事件/receipt与HEAD夹具拒绝，未造成真实错误接受。证据M-CLI-event-gate-handoff-2026-10-10.md。

### R-036 ｜ P2 ｜ verified ｜ gate崩溃可能丢审核日志并让新实例与旧reviewer并行
- 原实现到reviewer退出才写内存日志，仅逐run claim无法阻止另run在重启后启动。改持续文件复制、全队列dispatching冻结及协作main-writer lease，失败不重试；本地失败transport/未知claim重启反例通过。全机未知用户写入者不受该协作lease约束，未声称系统级隔离。

### R-037 ｜ P2 ｜ open ｜ 事件审核器尚非完整无人值守集成与派工M
- Dispatch仅审核。共享接线/集成/依赖派工与原会话自动回修没有生产受测执行入口，已关闭Desktop不能承担后续职责。保持显式open；真实原会话修复由当前CLI M自主调度，不冒称门房自动完成。详见接任记录。

### R-038 ｜ P2 ｜ verified ｜ 固定M初始化主动审核积压，绕过事件投递
- devin/swe-2固定线程初始化在读取协议后自行审核BAD/GOOD，两份决定虽经Review入口合法落盘，但没有门房dispatch claim；不能计入事件闭环通过。失败夹具保留于C:/Users/mio/AppData/Local/Temp/fufucode-gate-e2e-4d8f0da90ac94b16b7b647dd91b75842，bootstrap.stdout.jsonl可追溯。初始化改为read-only并明确禁止队列/Review；新夹具先初始化再派工，待真实固定线程投递复验。生产保持暂停，不恢复heartbeat。

### R-039 ｜ P3 ｜ verified ｜ 免费执行者最终JSON夹带前言被协议拒收
- 新固定线程夹具bad-01已提交真实3186f724，退出0，但最终回复JSON前附“交付事实”，严格协议正确拒收为failed_or_missing_card，不生成delivery.received。保留原会话，用新bad-card运行补交纯JSON，不改写旧日志、不放宽解析。证据同固定M记录中的新夹具；这是执行者格式失误，不是可放宽的验收失败。
- 复验：read-only初始化正常等待事件；bad-card原会话补交成功，门房固定thread独立rework，原会话bad-02提交aecbd869后同thread接受。三个投递都有decided claim，重放没有第四次模型调用，validation.json保存脚本指纹及日志SHA。只关闭本次可复现机制反例，不宣称任意模型永不违约。

### R-040 ｜ P1 ｜ open ｜ Scheduler控制台宿主退出而running快照未收敛
- 2026-10-10跨harness试跑发现旧生产gate38616与新fixture driver35280均不存在，任务结果0xC000013A，快照仍running/initializing。不能继续宣称仅Scheduler父进程即足够持久；未证实终止来源。生产先暂停；隔离夹具改wscript隐藏宿主持有独立PowerShell，继续实际多轮与控制台中断反例验证，不接管用户会话、不重发旧未知bootstrap。

### R-041 ｜ P2 ｜ verified ｜ Codex workspace-write无法提交worktree索引
- 隔离B原生spawn_agent两个devin/swe-2子代理已返回标记，但lead写modules/b.txt后git index.lock Permission denied，未提交，坏卡正确拒收。执行/集成改明确danger-full-access（当前授权本身为该模式），初始化仍read-only、审核仍workspace-write；按允许路径核对与协作锁限制实际派工，原线程恢复且继承仅本任务文件，不借机夹带真实用户改动。

### R-042 ｜ P2 ｜ verified ｜ Move-Item Force更新JSON造成读者短暂找不到绑定
- 多线程夹具driver于18:40:45UTC读取manager.json失败，紧邻gate更新同文件；文件随后正常存在。原Save-AgentJson使用Move-Item -Force，替换存在删目的文件间隙。改IO.File.Move(overwrite=true)同目录原子替换，需并发读写压力与通用流程复验；生产仍未按新代码恢复。
- 复验：3000次原子写/243588次并发读均成功；通用框架A/B并行、同线程四次审核、三次集成及C依赖续派已完成，无绑定丢失。R-041仅关闭执行授权模式导致的夹具提交失败，审核仍workspace-write；不声称修复Codex沙箱本身。详见M-cross-harness-flow记录。

### R-043 ｜ P2 ｜ verified ｜ 集成保护遗漏暂存状态及未提交接线
- 代码独立复核发现仅比用户文件SHA不足：未提交接线可能通过工作区行为测试但未进入main；用户文件内容不变而暂存归属改变也未被识别。改为集成前拒绝任务范围已有用户改动、持久化整体index差异SHA、完成时校验其不变及本任务范围无未提交内容。实际生产guard函数在独立临时Git仓库验证中文/空格保护哈希、暂存漂移、未提交接线拒收；证据fufucode-protection-b512e2496b38451594c99b54fa91b5ee/validation.json。最终源码全流程仍另验，未拿此局部断言冒称生产闭环。

### R-044 ｜ P2 ｜ verified ｜ 强制结束worker后状态可能永远running
- 增加受管worker独立PID绑定及原生Process.Exited事件，不用定时PID轮询。事件触发或启动对账确认worker消失时重读正常终态；仍running/starting才保存原状态、orphaned_worker与supervisor.failed，不生成delivery.received、不自动重派或重建worktree，独立任务可继续。真实一次性原生dummy进程强制结束反例通过（无模型）：fufucode-crashflow-fc2969cbc3ce4e5b8eeed9092a2e63bc/validation.json。测试首轮读取state比07事件写入早，已修等待两份实际持久证据，未放宽断言。

### R-045 ｜ P2 ｜ verified ｜ Windows复用已退出writer PID误阻塞审核
- 最终夹具B实际Codex进程47080于19:16:03退出，19:16:38同PID成为mcp-server-git的Python进程；原仅检查Get-Process会误判writer仍活跃。未结束该未知进程。新增writer/agent实际创建时间保存；判断需匹配创建时间，旧记录仅在现进程创建晚于已完成时间时确认复用，其他不明确情况保守阻塞。真实复用反例与当前/错创建时间对照通过，活跃writer拒审等全套gate反例仍通过。自动日期解析保留ticks，不能把DateTime转当前文化字符串再parse丢掉小数。证据0cce162ba3ce4dcc8b241c2082c6234b/tmp/flow/pid-reuse.json、gate-3bbd2362ac2a438495061dae39ba977e。新冻结源码夹具8cb1e76e16e6417e8768b87ed3e30873继续全流程验证；不把此前源码指纹冒充当前证明。

### R-046 ｜ P3 ｜ open ｜ 免费模型多工具初始化超过300秒
- 8cb1e76e16e6417e8768b87ed3e30873只读初始化实际保存thread并读完协议，在最终确认前300秒watchdog超时，状态needs_attention、日志保留，无自动重发。900秒独立新夹具初始化成功，并开始同版源码完整闭环。另一个只读无工具显式devin探针遭本地ws426后实际HTTP回退完成，不能据该错误归因整个停顿；未修改全局网络/模型配置。免费模型延迟及初始化时限仍如实记录，原未知thread不擅重启。

### R-047 ｜ P2 ｜ open ｜ OMP devin请求断开及原生审核迟滞
- eac87a94279f4d939313b566bce865f7的OMP执行者在read helper后多次stopReason=error、usage=0，socket connection was closed unexpectedly，10分钟watchdog后delivery.invalid，无误收/验收/重复派工。父会话思考high，未产生原生子代理。独立无工具/minimal同devin探针实际agent_end、OMP-FREE-PROBE、exit0；这不证明高思考是根因，也不保证工具请求健康。机制OMP执行显式minimal（仅测试；模型仍devin，生产无此自动变更），独立新夹具887b9a1c406a47e09fa2bc13a0a35bfa继续。0c夹具原生M最终30分钟超时needs_attention，全部原日志保留、没有重发。

### R-048 ｜ P2 ｜ verified ｜ 收到卡时worker尚活跃导致无后续文件事件
- 独立复核确认worker自身写delivery/state后才退出；门房拒绝活跃writer正确，但纯FS事件可能再无通知。门房订阅已确认身份的原生Process.Exited，退出后补扫；句柄缓存按PID+创建时间，不能误用复用PID的旧已退出句柄。实际一次性dummy反例只在退出前写卡，退出后不再改任何JSON，门房由Exited收到request，零模型调用；gate-exit-72c2c05beed34b6ea14645b91203a34f/validation.json。活跃writer/坏卡/HEAD/不明投递等既有反例仍通过。

### R-049 ｜ P2 ｜ verified ｜ 单个确定失败worker中断其他独立任务
- 原workflow对不可补schema的worker失败throw全局，违背单项外部阻塞不停止独立工作。改为本任务持久blocked记录并不自动重试，supervisor_error/orphaned本任务隔离；共享M不明状态仍全局禁止投递，依赖此任务的下游仍不解锁。格式修复前worker短暂仍存活改继续等Exited而非全局throw。须在最终同版全流程中复验，不以这一局部修复宣称生产已闭环。
- 实际无模型临时Git夹具隔离失败A、通过通用执行器继续派发独立B，A未重新投递；B本地客户端失败同样持久化而不冒称交付。证据：C:/Users/mio/AppData/Local/Temp/fufucode-isolation-071d8992e4d341069a3e8e018cc1008c/validation.json。此本地客户端不是模型闭环替代品。

### R-050 ｜ P2 ｜ verified ｜ 执行者声称子代理已返回但没有收回证据
- 冻结源码夹具887b9a1c406a47e09fa2bc13a0a35bfa的B1/C1真实spawn两个devin/swe-2原生子代理，但流中仍pending_init，交付卡自行声称完成。固定M独立核对保存流后明确rework，不因模块待接线打回、不认可自述。B2原Codex线程实际重新spawn并wait_agent收回两份结果后接受并真实集成；C2同一保存线程回修后交付，审核继续。B2证明wait_agent可用，不能把B1自述工具不可用当平台事实。
- C2同样独立接受并实际集成；正式六次审核、六个完成且父线程实际收回的子代理及重放零调用，最终源码证明通过，详见M-cross-harness-flow最新结论。

### R-051 ｜ P3 ｜ verified ｜ 测试断言假定OMP yield固定marker键
- 当前真实子代理yield成功结构为data.result，旧断言只读data.marker误报失败。按实际结构读取result/marker，并进一步要求父保存会话async-result的结构化job身份、completed状态及输出JSON与对应子日志一致；Codex也要求父流completed返回内容。没有修改夹具交付、降低子代理完成要求或放宽生产协议。修正后完整AssertWorkflow、真正Dispatch重放及空待审零调用通过。

### R-052 ｜ P2 ｜ verified ｜ Windows审核沙箱可写Temp却拒绝真实主区
- 原生产审核正常完成但门房输入被Access denied，无Review，needs_attention不重发。无模型native sandbox对照实际Temp成功、主区失败，显式可写root仍失败；原session确认workspace-write。受管审核改当前授权danger-full-access，初始化仍只读，不改全局配置/ACL。独立devin固定M真Review及真实主区模型apply_patch写读通过。详见M-review-write-recovery-2026-10-10.md；不声称修复Codex底层沙箱。

### R-053 ｜ P2 ｜ verified ｜ D4快照的子目录/删除跟踪身份与错误新鲜度不正确
2026-10-11 04:04UTC 状态更新：verified。原会话修复 HEAD `ef18d702` 经固定 M 独立审核、外层真实 Git/.NET 行为验证及单写入集成完成；main 记录 `57cedd5`。证据 `tmp/agent-flow/verify-D4-SNAPSHOT-791deb5bb655448bad77f3995c5b6191.log` 与对应 integration JSON。下面保留首次发现时状态，不代表当前未修复。
- 原629cf375，M源码发现后root独立临时.NET/Git实测三反例：子目录未跟踪文件读SUB却标根同名已跟踪状态；tracked文件删除后标未跟踪；越界错误条目IsFreshAsync=true。源worktree未改、临时index未变。明确rework经Review入口落盘，原模型/线程/worktree恢复；未接受或集成，行为复验后才能关闭。

### R-054 ｜ P2 ｜ verified ｜ 正常审核未落决定的needs_attention无法本地对账
- 旧Reconcile只允许inflight/needs_attention绑定和dispatching claim，实际正常退出使绑定ready/claim needs_attention，无法对账。新增仅在原日志身份/terminal、HEAD/无活跃writer及明确Review决定一致时本地decided恢复；正常完成无决定仍拒绝自动重发，错误task/非终态state反例拒绝，对账及重放额外模型请求0。生产原审核已独立复核后以同入口恢复，非正文关键词判定。

### R-055 ｜ P3 ｜ verified ｜ HTML 浏览器验证入口与 CLI 调用签名不匹配
- Playwright CLI 拒绝 file: URL；改为仅 127.0.0.1 的本地静态服务，HTML 本身仍可直接浏览器打开。CLI run-code 接收 async (page)，不是 async ({page})；Windows npx.cmd 传多行单引号脚本解析失败，改为显式单行参数。初次 favicon 404 用内联空 favicon 修复。修复后真实浏览器 14 项交互通过、console 零错误/警告，证据 output/playwright/automation-browser-checks.txt。均为测试入口问题，未更改生产门房或降低验收条件。

### R-056 ｜ P2 ｜ verified ｜ U0原型误做整套工作台而非流程tab
- 用户纠正：功能区侧边栏单个tab，功能围绕动画开发流程图。已移除复制的全应用外壳、任务列表主入口与独立右栏，改为节点/连线闭环、节点上下文和项目依赖子图，契约同步。19项真实浏览器检查涵盖动画实际移动、暂停、闭环、打回、门禁及窄宽度。证据 output/playwright/automation-flow-checks.txt 与U0记录；原生接线未因此宣称完成。

### R-057 ｜ P2 ｜ verified ｜ U0把流程步骤画成线程且缺少部分打回层级
- 用户纠正节点是对话线程、内部多任务与部分修复、大层级分组。现已9线程/50任务/51端口连线，Thread→Task→DeliveryPart独立身份与状态，修复轮次仅改变被退回部分，不影响已集成部分和其他任务。24项真实浏览器检查与后续图数据核验通过。旧CLI运行未迁移，新产品协议实现仍待U1/U2；不能以原型示例冒充生产支持。

### R-058 ｜ P2 ｜ verified ｜ 控制台审批失败的JSON回写改变批准文件hash
- 代码审查发现语义相同的JSON回写会改变BOM/换行，从而破坏原生审批绑定。已改为原始字节备份和恢复，失败保持暂停；临时夹具含BOM/CRLF的计划及原审批字节完全一致，11项测试通过，另实际执行原生Pause/Stop/Approve核对SHA。未对真实批准计划执行破坏性测试。初次浏览器测试的相对request URL改为绝对本机URL后通过，见CLI接入记录。

### R-059 ｜ P2 ｜ verified ｜ 候选项目计划未启用导致已完成单任务后无新派发
- 48ade61已审批完整规划并真实派发U1/O6-RULES；ec82454记录进程。此前把 manual 界面状态称作用户明确暂停错误，2026-10-11 用户澄清只是要求手动能力；按 R-064 修正与恢复。

### R-060 ｜ P2 ｜ verified ｜ PID出生时间JSON已为DateTime却再次Parse丢精度
- 精确停止改为DateTime对象直接比较UTC，字符串才Parse；隔离原生控制复验真实终止自身worker，保留原树，不杀未知进程。

### R-061 ｜ P2 ｜ open ｜ 生产固定M原生进程内存分配失败
- 2026-10-11 07:21UTC，原Codex返回-1073740791，stderr明确1GiB分配失败；原session无匹配完成终态。已持久needs_attention，不自动重发或伪造决定。根因及恢复仍待对账；不把Cloudflare警告当成此次失败原因。

### R-062 ｜ P3 ｜ verified ｜ 专家JSON带精确Markdown外壳导致解析失败
- 初次免费探针xisx9u8k冻结失败claim；只规范化完整的json代码围栏，正文混入或字段/角色错误仍拒绝。新隔离探针x0jefan_四真实会话及重放通过，不重发旧不明claim，不放宽结构断言。

### R-063 ｜ P3 ｜ verified ｜ PowerShell参数Decision被同名对象变量覆盖
- 新提案Apply隔离测试发现大小写不敏感的变量碰撞，改为approvedDecision。真实原生批准/两项Update/依赖重映射/保门禁/重放通过。

### R-064 ｜ P2 ｜ verified ｜ 手动编辑模式错误绑定暂停并误归因用户
- mode=manual 原本隐式 Pause，导致手动能力被混同停工。已解除绑定；明确 Pause 仍有效，14 项测试覆盖模式无调度副作用与真实暂停入口。按用户指令恢复生产监督，原 M 分配失败经显式、限一次、无执行证据的原线程恢复授权；未知投递仍冻结。隔离证据 Temp/fufucode-manager-recovery-4fc166b27e584676bb859ea672c6907a。

### R-065 ｜ P2 ｜ verified ｜ 主agent提交命令在租约失败后继续执行
- 8be7ba3命令使用默认非终止错误，租约Open失败后仍提交命名文件。当时M只审核；现场未见源文件冲突，不把它说成通过单写入验证。后续所有主区事务显式 ErrorActionPreference=Stop，本轮跨工具修改/提交由独立可核实持有进程保有main-writer租约，失败即停止。原审计 tmp/agent-flow/supervision-resume-audit.json 保留，不重写历史。

### R-066 ｜ P2 ｜ verified ｜ 有效打回决定与恢复claim不一致导致未续接
- U1已通过Review落有效rework，但旧恢复claim仍resume_authorized，workflow要求decided因而未派回修。新增本地对账仅接受原显式恢复claim、有效一致事件/决定、停止writer、真实HEAD及ready的固定M；不改review或重发模型。隔离恢复与重放测试通过；生产08:51UTC已自动派原会话U1第二轮，08:54UTC重新交付bc86f5a6。

### R-067 ｜ P2 ｜ verified ｜ 验证者输出解析失败导致门房永久等待
- 已知needs_attention验证claim无result时原gate一直continue。现把原失败记录交M独立补验，既不标验证通过，也不重发验证模型。隔离测试验证确实进入审核投递且不生成成功报告；状态不明dispatching仍保持冻结。

### R-068 ｜ P2 ｜ verified ｜ 运行中筛选漏M且无漏事件补扫
- 运行中视图原来只含worker；已含inflight固定M，并按实际review/integration活动标状态。原事件等待无超时，新增默认30秒纯本地对账；空闲不调用模型。真实浏览器已显示M集成线程；文件事件与1秒隔离补扫测试通过。

### R-069 ｜ P2 ｜ verified ｜ 主agent诊断写入干扰并行集成继承保护
- M0集成期间浏览器新快照和两份新测试修改触发严格保护。只把本轮明确生成的快照移至Temp并核对hash，测试增量备份后临时移出，按原生Reconcile复跑范围/继承/独立验证，成功记录M0集成c5c3d5e，零模型重做。之后在取得主写入租约时恢复本轮测试修改。未知/继承内容未删除或重置，未放宽保护；后续浏览器CLI在独立Temp工作目录运行。

### R-070 ｜ P2 ｜ implemented ｜ 控制台将未暂停和历史 inflight 误显示为运行
- API 加入 M PID/出生身份核对；UI 只有确认活进程才展示运行，不将未暂停等同调度在线。重启或断线显示未确认/上次快照；旧集成不再把当前集成动作显示为完成。两项新 API 行为及隔离 DOM 检查通过，真实浏览器工具受当前禁止审批策略阻塞。生产后端尚未重载新增 API，页面对缺失字段按未确认处理。

### R-071 ｜ P2 ｜ open ｜ 修复耗尽后缺少自动修复协调闭环
- U3 第三轮 0945b845707a4e83b9bcd035b7504768 有明确 rework，blocked-U3.json 记录次数耗尽，未见后续解决证据。不能无限重试或假验收；应产生唯一的 M 修复归属/范围调整事件，保持原模型/工作树/会话约束。此次只完成审计、阻塞展示及设计，未恢复该任务或扩张次数。

### R-072 ｜ P2 ｜ verified ｜ 半自动表单校验与操作保存时序
- 隔离真实浏览器发现 HTML pattern 的短横线未按 Unicode v 模式转义；已修正。模式保存未完成即打开表单会持有旧 revision，服务端正确拒绝但体验不清晰；现在保存中阻止开启新表单，保留 CAS 拒绝旧决定。真实创建、批准、交付、打回及重开验证通过，无模型调用。

### R-073 ｜ P2 ｜ verified ｜ 通用适配的完成后重放与 OMP 身份读取
- 初轮进程夹具发现任务接受后线程转 Completed，执行器在查重前按活跃条件拒绝旧请求；现在先核对不可变输入并返回旧运行，只有新派发要求活跃线程。真实 OMP session 文件首行为 title，原首行假设导致丢失 locator；现在有界读取 session header，加入 title 前缀夹具。51 项定向验证、OMP 两轮真实恢复和旧运行重放通过。

### R-074 ｜ P2 ｜ open ｜ OMP 原 devin provider 证书失败
- 首次显式 devin/swe-2 试跑在 devin provider 返回 unknown certificate verification error；保留原始失败，不关闭证书校验、不改全局凭据。改为显式使用已有 magpie provider 的同一免费模型后通过。适配后端可用，原 provider 证书问题尚未修复。证据见 harness-backend-2026-10-11.md。
### B1-ID-1 ｜ P1 ｜ verified ｜ 派发run缺少可核查任务身份

- M静态审查B1源e8d3e43发现HarnessRunStore未持久实际派发TaskId，DeliveryReceiver仅核对同线程及卡片/部分关联，无法证明run属于该任务。现200定向通过不覆盖同线程跨任务反例。未集成；原会话补忠实红反例及身份校验，尚未宣称运行复现。证据M-B1-review-2026-10-11.md。

- 修复轮 ebb6864 已持久化派发任务，M独立230项通过；源树修复已验证但未集成，故本登记仍open至收编。执行者红反例见B1-host记录。

### B1-HEAD-1 ｜ P1 ｜ verified ｜ 候选HEAD未绑定运行终态

- 源f3510fa执行器只保存启动HEAD，接收门检查候选等于当前HEAD，可能让同线程旧run借后续run提交。执行者披露、M代码核实，M尚未运行红反例；原L4/原模型修复，要求可信终态HEAD持久化、旧行与缺观察者fail-closed。未集成、不当生产接线阻塞跳过。

- 20:19修复9267fd3已收编0815314，任务身份修复收编83eeed8；M独立Hosting256/Harness51通过，集成Automation通过、全解决方案构建0警告0错误。两项关闭仅指缺陷，不代表生产宿主接线/整体工作台验收。

### P2-PS-1 ｜ P2 ｜ verified ｜ 包装验证脚本PS5无法解析
- 源脚本PS7三元导致Windows PowerShell5.1整段ParserError，源会话实跑记录保留；43dd644修复收编cb69506，M独立三文件PS5 AST解析0错误。不用warning降级或改运行平台掩盖问题。

### P2-PROXY-1 ｜ P2 ｜ verified ｜ Sandbox环回请求被继承代理阻断
- 源会话HTTP/2对照确认wsb环回gRPC被代理阻断，c120c96仅本进程NO_PROXY加入环回，收编9a87f79。提交态Sandbox12项通过证据已核对，未改变系统/用户全局代理。

### P2-OMP-1 ｜ P2 ｜ open ｜ 干净机器候选未携带OMP运行时
- 固定e64dd1b候选包runtime/omp缺失，未配置外部OMP时只有Pi可用。记录与交付须明示；OMP已实现桌面后端不等于它已打包部署。后续托管/配置及干净机器双后端验收仍待，不阻断本次明确Pi范围的候选体验。

### C3-HOST-RESTART-1 ｜ P2 ｜ verified ｜ 已停止宿主在同进程内无法重新取得槽位
- C3真实后台恢复夹具在Stop完成后以新hostId启动失败，旧TryClaim忽略stopped_utc，仍把活的容器进程视为占用。忠实失败日志tmp/m-c3-host-restart-red.log；修复仅承认显式完成Release，重新占用时清除旧stopped标记，未释放活/不明宿主仍拒绝。Control66及既有Hosting256全通过，不放宽竞争断言。证据M-C3-control记录第二增量。

### C3-REAPPROVAL-1 ｜ P1 ｜ verified ｜ 下游重新批准后无法产生可用执行意图
- 上游改规范会作废直接/传递下游意图，但旧实现不更新下游包版本；重新批准仍撞同一package/version唯一意图，留在superseded，三包链只有上游重新可绑定。忠实失败日志tmp/m-c3-reapproval-red.log，ReapprovalChecks通过公共API创建/批准/改上游/重新批准后断言实际失败。修复采用事务内递增受影响下游契约版本、保留原规范与意图历史，不复活旧run或重发生产任务。
- fe1bf3d已修复；原反例不改断言，Control135 PASS含三包链重新批准产生三个新意图、旧三个意图保持作废、旧规范可读。最终日志tmp/m-c3-relations-green.log；实际绑定仍需新的已登记Task，旧run/绑定不回收复用。

### C3-HOST-SNAPSHOT-1 ｜ P1 ｜ verified ｜ 宿主快照队列计数跨时刻不一致
- Hosting真实恢复测试失败于“快照列出整个三项积压”，日志tmp/m-c3-relations-hosting-final.log。Snapshot分别调用四次Count，每次打开独立连接读取；运行中状态转换可能使同一行重复计数或漏计，事件游标/活跃列表也不是同一读时刻。修复须读取同一事务快照，不放宽总数断言。失败后只向身份核实的本轮restart临时夹具宿主发原协议STOP，保留日志/数据库，不操作生产宿主。
- fe1bf3d将四项聚合计数、事件游标、活跃run列表放入同一SQLite读事务；Hosting257 PASS包含新增80行并发转换时每张快照总数恒定，以及原三项恢复积压断言。tmp/m-c3-snapshot-hosting-green.log，Control135回归也通过；没有增加sleep或放宽断言。

### E1-RESPONSE-1 ｜ P1 ｜ verified ｜ 多字节响应/请求预算与发现项验证不严格
- 源2b695cc的2MiB字节限制拿来切字符串字符，750000个中文字响应直接越界抛异常；数字优先级99和空证据被接受，20份多字节输入超过512KiB请求总预算。M独立公开API反例实际失败，tmp/m-e1-counterexamples-final.log；原L3模型修复，不放宽断言。

### E1-COMPLETENESS-1 ｜ P1 ｜ verified ｜ 过期或未完整送审被报告为完整审查
- 实际Git+协议夹具执行：源在审查中变化且模型空发现，返回NoFindings/complete=true/stale=false；所有发现无效仍Completed/complete=true；超每文件限制被省略的输入仍报告完整无发现。七项合并反例见M-E1-review与上述日志；待原模型修复，尚未收编。Unknown scratch清理另需保留对账证据，不以运行结束自行删除不明目录。

- 两项修复实现d54c87e，源最终e5b2950停写；M原七项反例全部通过、源模块84/UI77/CE20独立通过。verified只表示缺陷修复，模块收编与共享接线另记，见M-E1-review追加记录。

### GIT-DIFF-HEADER-1 ｜ P1 ｜ verified ｜ 补丁内容误被当成文件头删除
- 实际文件由 `-- old` 改为 `++ new`，hunk 内两条行分别以 `---`、`+++` 开头，旧解析器无条件跳过。M忠实红 `tmp/m-e1-range-diff-red.log`，修复只在 hunk 外识别头部，另外保留Git可执行mode变更。57项GitSnapshot及全量Core340/Pi21通过；不修改测试预期掩盖丢行。

### CORE-STOP-OUTCOME-1 ｜ P1 ｜ verified ｜ 停止返回后适配器结果仍空
- 全量Core旧断言实际失败，定向FakePi确认停止ack可先于事件泵终态，Stop返回Idle/queued0/outcome=null。`tmp/m-core-stop-red.log`为确定握手反例，不依赖随机等待。Stop成功后从同步快照发布Interrupted，真实TurnSettled仍由协议发布，状态锁防止旧结果覆盖新回合。定向4项、全量Core340/App自测0失败通过；见M-E1-review。

### E1-HISTORY-1 ｜ P1 ｜ verified ｜ 历史展示借用上一活结果定位和导出
- M公共接线后增加真实页面反例，LoadHistory后仍可用前一_result快照定位历史发现，日志tmp/m-e1-history-red.log实际失败。现在分开保存历史文档与当前活快照；历史不得定位当前文件，但仍能复制保存的证据，旧缺完整性字段不默认为完成。79项UI通过，tmp/m-e1-history-green.log。未知当前位置不能借其他结果证明。

### E1-NATIVE-EVIDENCE-1 ｜ P1 ｜ verified ｜ 原生补验证据扩大与错误窗口截图
- 源2f76296原生卡只有UIA事件与数据，03-fufu.png实际为Terminal/Ubuntu设置页；页签隐藏被写成VM释放，Mock默认启用被写成偏好持久，AX控件存在被写成窄栏可达，ref摘要被写成字节验证。实际App41020仍在，记录SHA因amend不一致。M查源码/图片/真实Git及数据后原L2/免费模型返工，仅修记录/补可信证据，不据此推定产品回归。M-E1-native-review-2026-10-12详列判断；未接受、未整合。
- 源追加42125c8保留历史并撤回全部扩大声明，自有App已退出，终态卡停写；M独立scope/记录/进程与调用轨迹核对后accept修正版部分证据。verified只指记录诚实性纠正，不代表缺失原生项目通过；真正工具阻塞另列E1-NATIVE-TOOL-1，文档收编实际SHA另在M复核记录补录。

### E1-NATIVE-TOOL-1 ｜ P2 ｜ open ｜ Windows原生操作通道未配置
- E1-NATIVE同一免费devin/swe-2原chat重试技能@oai/sky，真实js调用失败，记录Trusted RPC service is not configured: sky和kernel reset，sky.target未配置；cua实际只提供浏览器接口，本M工具亦无node_repl。这是外部工具环境阻塞，不推定Fufucode产品回归，不改全局配置/登录采购/换昂贵测试lead；保留8432树与证据，待通道恢复后原任务补验。
- 右键移栏/真实Pi窗口托管审查/复制导出历史/过期定位/取消/320宽/IME/DPI/正常关闭仍未验。接受诚实部分记录不解锁整项E1原生验收；局部阻塞继续C3独立工作。M-E1-native-review-2026-10-12为判断依据。

### WORKBENCH-OPTIONAL-LAYOUT-1 ｜ P1 ｜ verified ｜ 外观布局同步丢弃可选功能页
- 新真实Enter断言失败，palette确实执行但审查page被停放回隐藏容器。tmp/m-e1-palette-diagnostic.log记录前后身份/宽度/选择，根因SynchronizeLayout和Normalize按旧KnownPages过滤library/local-review。只补实际可用ID，R3绿色日志m-e1-palette-green.log；资料库/审查打开后可见且保存布局含该页另有行为断言。不改写已完成辅助栏设计，不放宽页面可见断言。

### C3-AUTH-INDEPENDENT-1 ｜ P2 ｜ verified ｜ 集成授权误绑定独立组其他成员
- 新授权实现将所有关系成员的版本/成果都纳入证据指纹；独立组A已接受并授权后，只编辑无依赖的B规范，A授权也失效。真实协议子进程/U1交付/人类授权反例tmp/m-c3-integration-tests-2.log实际失败于“an independent peer contract change must not invalidate this accepted authorized outcome”，未降低断言或改协作语义。
- 修复限定独立组指纹仅含目标成员；竞争/互补保留相关成员规范与真实证据，流水保留前置契约及交付。真实绿色回归及实现SHA验后补录，初始失败证据保留；本项不是旧生产故障或整项C3验收。
- 最终Control310通过原忠实反例及竞争/互补失效反例，tmp/m-c3-integration-acceptance.log；M-C3-control第八增量记录提交SHA，相关门禁已通过。

### C3-INBOX-LIVE-CARD-1 ｜ P1 ｜ verified ｜ 收件箱禁用审核但仍读取展示活写入者正文
- 新收件箱虽禁用了活/Unknown对应的审核选项，ContextJson仍带已登记的早期card正文。文件握手的真实活子进程、临时U1早期交付记录反例tmp/m-c3-integration-live-red.log实际失败于“never its premature card body”；这是隔离负例，不是生产agent违规报告。
- SQL读取改为按确已settle条件选择card_json：活/Unknown/缺失归属只投影身份与状态，不载入正文；确认退出后原记录才可显式审核。集成上下文的本包与关系成员也按同一条件遮蔽正文。反例不降低断言，最终门禁验后补录。
- 原文件握手反例及确认settle后正文可读取通过，Control310、tmp/m-c3-integration-acceptance.log；记录只写实物证据与实际提交，未把隔离负例当生产违例。

### C3-REVIEW-LIVE-BYPASS-1 ｜ P1 ｜ verified ｜ 直接U1审核绕过活写入者收件箱阻断
- 同一真实活子进程早期登记反例中，收件箱已遮蔽/禁用，但直接RecordDecision仍接受该早期revision，tmp/m-c3-integration-direct-review-red.log忠实失败。不把UI禁用当核心门禁，也不放宽反例。
- RecordDecision事务内新增受管当前批准规范/当前task/确已停止写入者检查；旧未受管U1任务保留原协议。确认settle后同revision可审核，活/Unknown/缺失归属/退役规范不能旁路。最终绿色与实际SHA验后补录。
- 原直接live RecordDecision拒绝及settle后同revision接受反例通过，Control310与旧U1 58全通过；tmp/m-c3-integration-acceptance.log和u1-final.log。无跨协议自动收管。

### C3-REVIEW-ABANDONED-1 ｜ P2 ｜ verified ｜ 旧未启动弃队列阻断新版本正常审核
- 新审核门初版把历史host_requests.state=Abandoned且无harness_runs的行视为缺失执行身份，导致知识采用后旧队列从未启动、新版本正常settle仍无法审核。原AdoptionChecks忠实失败于tmp/m-c3-integration-final-green.log及delivery-tests.log，未改原断言。
- 明确区分未启动已弃队列与已settle缺失harness身份：前者没有启动事实，可保留历史且不阻断；后者仍fail-closed。活/Unknown/queued/缺失队列仍拒绝。原既有adoption反例恢复通过后再标verified，不凭编译结论。
- 原AdoptionChecks与新live/settle直调反例完整通过，Control310/Hosting257/U1 58；tmp/m-c3-integration-acceptance.log。未改变原契约版本/弃队列/准确prompt的断言。

### C3-OWNERSHIP-DIAGNOSTIC-1 ｜ P2 ｜ open ｜ 定向回归出现一次所有权拒绝但原因不完整
- tmp/m-c3-integration-independent-green.log在原RelationChecks创建AfterProof绑定时，真实所有权门返回active或unverifiable；当次旧错误没有具体run/verdict/reason，不能据此归因PID复用、时序或新授权代码。未重发生产任务、未修改所有权判断/放宽断言/接管或杀未知进程。
- ControlIntentConsumer错误补真实runId/verdict/audit.Reason，后续同套件再跑到收件箱活进程反例时，该所有权检查通过。一次未重现不证明根因修复，本项保留open，若再现按具体证据处理，不靠延时掩盖。
- 本轮同AfterProof再现tmp/m-c4-control-final.log：Completed writer1548、start1791842553155关联1780/1908，实际为更早数小时的csrss/wininit。新增child创建时间门，受保护进程仅针对父PID补CIM metadata；未知仍阻断。真实tmp/m-c4-birth-proof.log RawChildren1780,1908→FilteredChildren空，五子进程反例/Control511/Hosting267通过。本次明确原因已修，最早无诊断旧失败不能证明同因，本项保留open，另记C4-CHILD-BIRTH-1 verified。

### C4-CODEX-GIT-SANDBOX-1 ｜ P1 ｜ verified ｜ 默认Codex沙箱无法提交交付
- 首次免费模型A正确建文件但.git写入拒绝返回blocked，B报module_ready仍baseline HEAD；tmp/m-c4-live.log及fufucode-c4-923c78ada60b4ccd86066e7e66cdf82d。两卡未接收/接受，未替agent补交。HarnessRuntime受信可选CodexSandbox默认workspace-write/旧payload保持，支持值限定/客户端无覆盖/恢复继承；显式隔离danger-full-access最终真实59通过，全局和生产默认不变。

### C4-PROBE-TIMEOUT-1 ｜ P2 ｜ verified ｜ 新探针清理误用OwnedHere判断活writer
- OwnedHere按设计不承认活writer的接管，首次超时分支未终止、两个writer自行结束，未收交付。工装修正严格核实自身hostId/supervisor PID+start和writer PID+start；真实hold-second有界退出、无活writer/Started请求/交付，Control511。没有扩大生产终止权限。

### C4-CHILD-BIRTH-1 ｜ P1 ｜ verified ｜ Windows旧数字creator PID误关联新writer
- 实际原PID证据与修复见上方C3诊断追加。保留未知birth、后生真实子进程、缺父身份的阻断，仅排除验证为更早出生的进程；无名称白名单/提升权限。Control511/Hosting267通过。

### C4-ENDPOINT-SHARE-1 ｜ P2 ｜ verified ｜ Host端点文件读写竞争
- tmp/m-c4-hosting.log真实RestartBacklog读host.endpoint报IOException。读分享ReadWrite/Delete、完整临时写后File.Replace、端口范围校验；File.Move(overwrite)旧read handle在本机UnauthorizedAccess红例保留tmp/m-c4-boundaries-atomic-red.log，Replace同反例通过，10边界/Hosting267通过。自有失败后台经STOP正常退出，未知宿主未动。

### E2-PASTE-TARGET-1 ｜ P1 ｜ verified ｜ 延迟剪贴板粘贴重定向到切换后的终端
- 源156eacd控件await读取后才取Session。M真实控件/两个tab/延迟TaskCompletionSource反例：A发起粘贴、切B、读取完成，内容写到B草稿，A草稿未变。tmp/m-e2-counterexamples-final-red.log FAIL，M-E2-terminal-review为细节。原L4/原模型/原chat返工，仅许可终端范围，尚未收编；缺公共接线不是返工原因。
- 源修复6ed9bad、冻结b16a36c，M原两反例独立PASS（tmp/m-e2-fix-counterexamples.log）；绑定发起tab、已Dispose丢弃、不同当前tab不交焦点。只证明该模块反例修复，原生/主线接线仍未验。

### E2-SELECTION-GRAPHEME-1 ｜ P2 ｜ verified ｜ 终端选区边界拆开组合字符
- 源156eacd实际TerminalBuffer.Append("Ae\u0301X")，按格0..1选择输出Ae而非Ae加组合附加符。M同上独立公开API反例FAIL；局部选择模型需按字素边界处理，不越权改VT/ConPTY。原宽字符/emoji/快照保护测试保持，未放宽断言制造通过。
- 源b16a36c原ASCII组合符反例PASS，但宽基字符及宽表情边界仍拆开字素；因此整项保持open。忠实剩余反例与第二次返工见下项，不把局部修复当全部通过。
- 第二次修复源2b6a74c由整行StringInfo字素边界映射回实际cell，M六反例/893源全量/真实ConPTY全部通过，tmp/m-e2-grapheme-width-green.log与source-selftest/conpty.log；缺陷verified，原生和主线接线另验。

### E2-SELECTION-WIDE-JOINER-1 ｜ P2 ｜ verified ｜ 宽续格阻断字素扩展且旗帜被拆开
- 源b16a36c，M实际TerminalBuffer输入A中+U+0301+X与A👨+ZWJ+👩+X，选0..1只得A中/A👨；宽续格空文本导致扩展循环提前停止。A🇨🇳X选0..1只得A🇨，手写分类遗漏区域指示符配对。tmp/m-e2-grapheme-width-red.log三FAIL，原两个反例及同ZWJ完整边界仍PASS。没有修改执行者测试预期/VT/PTY，原L4/原模型/原树返工、not_integrated。
- 源4fc0ccc修复、最终2b6a74c，M原六反例保持且全PASS；起止与高亮共同ResolvedRange，原96终端检查通过、源App893全量0失败。旧红证据保留，不用口头“已修”作判断，主线整合状态另记。

### F1-OWNER-CLOSE-1 ｜ P1 ｜ verified ｜ 新浮动展示窗口阻断主窗口正常退出
- 新增宿主把owned-window关闭询问也当用户X关闭并取消，主窗口Closing事件未触发。tmp/m-f1-close-red.log：ownerVisible=True、ownerClosingEvents=0、owned已隐藏；真实控件正常Close反例失败，不是任意延时不足。只拦WindowClosing而放行OwnerWindowClosing等退出原因，原断言通过，tmp/m-f1-close-green.log和final-target.log；未绕过主窗口原异步业务清理。

### F1-RESTORE-OWNER-1 ｜ P1 ｜ verified ｜ 恢复浮动布局在主窗口显示前打开owned窗口
- 首次全量tmp/m-f1-selftest-full.log在后续MainWindow构造期报Cannot show window with non-visible owner，说明真实恢复路径缺门禁；未删配置或改可见性断言。构造先挂内容，owner不可见时不Show，Opened再投影。新增真实新MainWindow读取已保存布局且页控件归属浮动窗口反例通过，final-target.log 29项0失败；全量最终结果另在F1记录补录。

### F1-BROWSER-PARENT-1 ｜ P1 ｜ verified ｜ 浏览器跨窗口需更新真实controller父句柄
- 源码核对BrowserViewHost的延迟释放条件为回调时VisualRoot为空；同批移到另一Window后该条件可能不成立，控制器保留旧HWND而布局参照新窗口。未做真实WebView2移窗，不据静态判断声称已修或原生通过。官方本机SDK XML ParentWindow描述支持重挂，E2-B必须补父句柄更新/异步初始化竞态/实际DOM与profile往返验证；F1通用控件29项无头通过不能覆盖此项。
- 2026-10-12 E2-B源0fe7d98已修复并收编，M主线真实SDK移窗/整栏/隐藏重开/停靠回main验证，create A/B=1/1、release=0/0、reparent=2/2，真实父HWND和DOM往返通过；tmp/m-e2-browser-main-native.log，NativeModule0失败。主线默认App996零失败/构建零警告错误。不是实际指针/IME/DPI或真实登录态验收，早静态open记录保留。

### BUILD-TEST-LOCK-1 ｜ P2 ｜ verified ｜ 并行solution构建与测试加载同一输出DLL产生复制警告
- M第一次集成构建tmp/m-e2-final-main-build.log报MSB3026共5警告，具体锁定者.NET Host PID36036是本轮自有Pi.Tests执行进程，退出后构建完成；没有改警告级别/重试次数或杀未知进程。最终验证改为测试退出后串行solution构建，tmp/m-e2-acceptance-build.log实测0警告0错误；保留早期有警告日志，不把exit0当零警告。

### F1-NOTIFICATION-FOCUS-1 ｜ P1 ｜ verified ｜ 重复已聚焦观察清掉新提示与系统回退
- NotificationCenterViewModel.SetWindowFocused(true)无条件清LatestPrompt。多个浮动窗口活动状态需重复观察同一应用焦点；实际控件/TCS通知反例明确先设置已聚焦、发布A/B，再重复true，B被清掉，两个延迟系统失败无回退。tmp/m-f1-focus-red.log三FAIL，diagnostic记录latest空/hasPromptFalse而Windows开启；不归因任意等待不足。
- 改为只在未聚焦→聚焦的真实状态边沿关闭短时提示，保持返回应用取消提示的旧契约。原反例及晚旧系统失败不能覆盖新回退、禁用Windows后回退消失、页内历史保留均通过，tmp/m-f1-notification-green.log，25条0失败；最终默认App979条0失败，含NotificationPage26，tmp/m-f1-notification-acceptance-full.log。未改通知来源/审批有效性或放宽断言。

### F1-NOTIFICATION-EMPTY-BINDING-1 ｜ P2 ｜ verified ｜ 公开通知集合更新后空态绑定未刷新
- 隔离真实控件夹具直接加入Items后，首轮320图同时显示“暂无通知”和两条真实记录，HasItems派生绑定未收到集合变化通知。红图位于临时根fufucode-notification-page-000d58a06c5a4a51b602986cb3b268a3/notifications-320-Light.png；这不是既有生产ApplyNotice路径失败的证据，后者本身已有NotifyCounts。
- 构造订阅CollectionChanged刷新计数及派生属性，增加真实空态控件断言。最终26条及App979条全量通过，M复看fufucode-notification-page-f241b353d98e4bd38129dfee75de65ab浅深图确认空态隐藏；不把无头渲染当原生指针/IME/DPI验收。

### C3-CLIENT-POST-RESET-1 ｜ P2 ｜ verified ｜ Windows短POST拒绝响应被未读请求体断连遮蔽
- 实际HTTP夹具第二轮在403拒绝时WinError10053，tmp/m-c3-client-tests-final.log；拒绝前未消费POST体，关闭socket可能导致拒绝响应丢失。只消费有界体后拒绝，不降低Host/Origin/token要求；三种拒绝各重复3次及全8项通过，tmp/m-c3-client-tests-green.log。

### C3-CLIENT-POLL-FOCUS-1 ｜ P2 ｜ verified ｜ 无变化轮询也重建按钮造成焦点丢失
- Chrome两次跨5秒轮询后旧批准按钮ref失效，原refresh无条件replaceChildren。改只在真实快照变化时渲染，6.5秒后原按钮connected/focused均true，tmp/m-c3-client-dom-green.log；随后批准/切模式均提交到真实同库，不禁用轮询或放宽版本门。

### E2-B-RESTORE-STATE-1 ｜ P1 ｜ verified ｜ 损坏多页签配置使恢复崩溃
- reviewedHead9f2f19d，M真实控件重复ID抛ArgumentOutOfRangeException，null记录抛NullReferenceException；tmp/m-e2-browser-restore-final-red.log两FAIL。合法记录/草稿必须保留，不能丢全配置凑不崩溃。原L4/模型/树修复，not_integrated。

### E2-B-INIT-ORDER-1 ｜ P1 ｜ verified ｜ 创建前最后的文档请求被旧地址覆盖
- M真实SDK先地址后文档，最终title=M_PENDING_OLD；tmp/m-e2-browser-native-final-red.log。继承旧双pending实现，仍违反本任务最新请求语义，非主线程调度或假环境失败。

### E2-B-EARLY-ZOOM-1 ｜ P2 ｜ verified ｜ 创建前缩放仅更新UI未进入真实控制器
- 两实际场景UI175/140、controller两次1；ready后Zoom及双tab隔离通过，定位待创建状态丢失。源9f2f19d、同一最终SDK日志两FAIL，不用VM数值代替实际ZoomFactor。

### E2-B-FIND-CANCEL-1 ｜ P2 ｜ verified ｜ 关闭查找后迟到回调复活提示
- 真实FindNext后立即Close，条隐藏但全轮询结束FindStatus=2/2，最终SDK日志FAIL。必须取消/过期隔离，不仅隐藏控件。

### E2-B-FIND-REOPEN-1 ｜ P2 ｜ verified ｜ 同query在新DOM重开沿用旧查找结果
- 新实际HTML只1个marker，重开旧query仍SDK(2,2)。早轮只数2会误判已复核，最新不同DOM匹配数反例FAIL，原日志保留；不能靠延长等待或旧计数标成功。

### E2-B-PENDING-STOP-1 ｜ P1 ｜ verified ｜ 初始化前Stop已受理但取消的导航仍执行
- 场景确认未ready且CanStop=true，Stop实际执行，最后仍加载M_PENDING_OLD；最终SDK日志FAIL，正常无取消的最后URL与创建中Dispose均通过。修待发导航和loading语义，不降低CanStop断言。

### E2-B-DOWNLOAD-POLICY-1 ｜ P1 ｜ verified ｜ 默认下载行为与任务要求不符（初始静态发现）
- DownloadStarting仍设系统Downloads路径/Handled=true且未Cancel，继承旧实现，交付声称不自动下载但源码未落实。未触发向用户目录下载，不冒称运行反例或修复；要求本包默认取消并清晰提示，修后临时SDK验证，下载管理仍范围外。

2026-10-12修复轮独立复核：上述E2-B-RESTORE-STATE-1/INIT-ORDER-1/EARLY-ZOOM-1/FIND-CANCEL-1/FIND-REOPEN-1/PENDING-STOP-1/DOWNLOAD-POLICY-1七项原缺陷状态更新为verified（源0974ff5、修复37129bd/工装2a17f65）。M原33项全通过、源NativeModule0失败/真实下载Cancel无落盘。只表示原反例修复，不覆盖以下新相邻缺口；模块仍not_integrated，真实用户原生输入/DPI另验。旧open段是当时状态，红证据不删。

### E2-B-RESTORE-ID-COLLISION-1 ｜ P1 ｜ verified ｜ 归一化生成ID抢占后续合法选中页
- 源0974ff5，前空ID条目被生成tab-restored-1、后合法tab-restored-1被重命名；ActiveTabId指向前坏条目，选中blank-draft而不是selected-real-draft。tmp/m-e2-browser-adjacent-red.log真实控件FAIL。要求预留合法原ID、保存正确选择，不丢全部坏配置或放宽身份断言。

### E2-B-STOP-STATE-1 ｜ P2 ｜ verified ｜ 创建前Stop已受理但loading未立即结束
- 源0974ff5，Stop仅StatusChanged，tab.IsLoading直到初始化回调仍true。M真实SDK受理后立即断言FAIL，tmp/m-e2-browser-history-red.log；取消旧页面终态已通过不表示即时状态正确。

### E2-B-STOP-RETRY-1 ｜ P1 ｜ verified ｜ 创建前Stop后新导航也被取消
- 源0974ff5，旧待发Stop保留_pendingStop，新有效Navigate写pending但未解除旧取消标志；真实最终title为空而非M_PENDING_URL_LAST。相同SDK最终日志FAIL；必须保持旧已取消请求不加载并允许新请求，不能把按钮禁用当修复。

2026-10-12上述三相邻缺陷由e5e6f4c修复，最终源0fe7d98；M独立同38项全PASS，tmp/m-e2-browser-rework2-green.log。原15无头/23SDK预期不改，合法原ID保留、Stop即时loading=false、新请求真实title=M_PENDING_URL_LAST；主线模块已正常收编，17公共接线/996全量/NativeModule0通过，源码/原会话保留。

### COORDINATION-APP-TRANSPORT-1 ｜ P2 ｜ open ｜ Codex app监督接口连接关闭
- 第37轮wait两个原线程已能返回实际状态，但一次恢复不等于长期稳定或根因修复。本轮依此发现R1中断并安全恢复；E3最新turn01a13470 inProgress，不接收半成品、不重复启动。旧故障证据与open保持。
- 2026-10-13第36继续轮：list_threads返回local/thread_list_unavailable，wait E3真实ID报local/durable无法确定存在。创建接口已排队成功，新树与只读本地metadata/任务状态事件确认真实thread/turn启动；未读运行正文或半成品卡，未重复create/恢复。R1/E3均未见terminal，验收门保持。此为监督接口故障，原因未知，不推断agent失败，不改用户配置；记录E3-local-dispatch-2026-10-13.json保留该限制。
- 本轮wait_threads连续Transport closed；不凭故障判worker失败/重启/重派或采信其正文。只读已知受管本地结构化task_complete及无后继task_started，核对0fe7d98干净/无写入者，再读完整卡SHA一致。产品独立复核/主线验证继续，app渠道仍不可用；未修改全局设置或假称协调通知已送达。

### C3-CONTEXT-CASING-1 ｜ P1 ｜ verified ｜ 真实Core上下文的字段大小写导致命令缺身份
- Core ContextJson使用PascalCase记录字段，JS按camelCase读定向问答版本、竞争候选、授权和知识采用身份；原模拟全camelCase检查遗漏。新增真实形状反例后Node断言undefined !== 7（tmp/m-c3-context-red.log）。仅规范化模板所读副本、保留原context/token；23条模板检查与真实Host交付Core→Node身份检查通过，Chrome实际review生成/记录绑定真实任务、run、HEAD及token。solution0警告错误/Control346/Python8/HTTP9全PASS，见M-C3-review-2026-10-12。模型修复投递尚未完成与此缺陷分开。

### C3-REVIEW-EXPLICIT-1 ｜ P1 ｜ verified ｜ 省略decision被枚举默认值隐式接受
- 新review入口提交真实绑定/证据却省略decision，CLI反序列化给默认Accept并修改部分；HTTP反例实际失败于Missing explicit decision must be rejected（tmp/m-c3-review-missing-decision-red.log）。Decision改JsonRequired，Core JSON及实际CLI/HTTP缺省提交均拒绝且状态不改，原接受/重放仍通过。最终Control346/HTTP9与solution0警告错误，不靠UI默认Defer掩盖传输缺口。

### C3-HTTP-DISCONNECT-1 ｜ P2 ｜ verified ｜ 客户端关闭导致HTTP尝试二次错误响应
- Chrome关闭后GET回传WinError10053，旧respond异常进入上层OSError分支后又发送503到同一断开socket，出现双栈。独立写边界红于ConnectionAbortedError，tmp/m-c3-client-disconnect-red.log。respond只处理连接类异常，不重试Core修改；三类断连的header/body六边界和真实HTTP“Core已提交→客户端RST→原ID重放”通过，无未处理请求异常/重复计划/版本推进，客户端10测试及审核/再审HTTP9+9通过。代码/证据见M-C3-reconsider-2026-10-12。

### C3-REVIEW-NONDELIVERY-1 ｜ P2 ｜ verified ｜ 尚无交付的占位部分误提供审核动作
- CreatePart缺省Delivered但revision0且无run/HEAD，旧inbox只按status筛选；真实零交付反例在tmp/m-c3-repair-red2.log FAIL。只显示已有revision和真实身份的Delivered部分，Control442/HTTP10/客户端10通过，不把无交付当完成或放宽身份断言。

### C3-REPAIR-START-DIRTY-1 ｜ P1 ｜ verified ｜ 返工入队后新增未提交修改仍被启动
- 入队前clean已通过，随后新增tracked改动而HEAD不变；旧Host actual-start只查HEAD，真实反例tmp/m-c3-repair-red3.log FAIL且第三子进程启动。返工启动前再次查clean，同反例验证Abandoned、无新run、仍仅初次+resume两进程、新增内容保持，阻塞原因可见。Control442/Hosting257/App996及Core340/Pi21通过；未改变初次任务继承改动策略。

### C3-REPAIR-REACTIVATE-1 ｜ P2 ｜ verified ｜ 新返工授权未恢复完成线程
- 第十二增量首轮中先接受一部分导致线程完成，后来另一部分返工；意图创建后原session查询仍要求Active而拒绝（tmp/m-c3-repair-control.log）。授权同事务明确恢复Active，冻结仍拒绝；真实原身份续接链通过，旧卡/决定保留。此为新增实现轮修复，不冒称既有生产故障。

### C3-REPAIR-PLAN-READ-1 ｜ P1 ｜ verified ｜ 新阻塞投影错误依赖尚未初始化的Host表
- 新增BlockingReason读投影直接join host_requests，纯计划库尚未初始化Host而报缺表（tmp/m-c3-repair-control-green.log）。无返工记录先返回空状态，不为快照读启动Host；Control442起始计划检查、客户端10/HTTP10全部通过。没有迁移用户生产库。

### E7-TAB-CONTEXT-1 ｜ P2 ｜ verified-headless ｜ 页签右键打开栏菜单
- M真实右键反例FAIL；自定义DockTabButton继承Button样式入口，并按命中行路由context request，页级菜单与空白栏头分开。同反例最终绿，原生新源码待验；不以旧无效mouseButton调用归因。

### E7-CAPTION-INITIAL-1 ｜ P2 ｜ verified-headless ｜ 初次恢复皮肤仍用Fluent窗口图片
- 新建MainWindow恢复material3时UseSkinGlyph false，定向/首次全量各1FAIL。改Caption自身动态资源订阅（默认false）后初次恢复、热换及Fluent图片恢复绿，最终App1451零失败。红日志保留。

### E7-STORAGE-TEST-CONTENT-1 ｜ P2 ｜ verified ｜ 存储UI工装强转全部按钮Content
- 帮助图标使UiSmoke第37行InvalidCastException。改类型判断后统计文本为“打开”的按钮，原区域数量断言不变，Storage212通过。

### E7-NATIVE-4 ｜ P1 ｜ open ｜ 旧临时根启动Dispatcher关闭异常未归因
- NATIVE R2真实进程栈Cannot perform requested operation because the Dispatcher shut down，只有MainLoop/StartCore/Program，无内层恢复栈。新root正常，M无头hidden恢复/重开绿，不证明浮动布局造成/修复崩溃；下一原生包隔离复现。

### E7-AERO-CAPTION-SCOPE-1 / E7-AERO-THEME-OWNERSHIP-1 / E7-AERO-CLASS-OWNERSHIP-1 ｜ P2 ｜ open-rework
- Aero首卡163样板绿，M生产CustomCaption混合工具条挂接报左侧边栏语义异常；外部Button.Theme丢失、已有aeroCaption类被删，共三个独立红。结构化rework与原模型L3返工已派，不接共享入口。

### E7-COMPOSER-CORNER-1 ｜ P2 ｜ fixed-pending-final ｜ 输入卡本地圆角覆盖皮肤
- 实际Metro=0/M3=16两断言均红，原本地settings.Corner覆盖样式。可选皮肤清本地值，Fluent仍用原偏好；对应资源/页返回验证需最终绿，不能以样板卡绿代替。

后继验证：Skin67及64全量1455零失败证实此同反例修复，状态verified-headless；原生新源码仍待验。

### E7-NATIVE-ACCENT-OVERRIDE-1 ｜ P2 ｜ verified-headless ｜ 窗口直接调色板色覆盖可选皮肤
- Metro原生SystemAccentColor断言FAIL，外壳AppAccent已蓝但底层原生仍沿用旧直接色。清掉外观流程七个直接色（不清用户任意资源），Metro/M3/浮动/Fluent自定义恢复四实际断言及Skin71绿。原生像素仍待新源。

E7-AERO三所有权/混合顶栏缺陷后继：修订2 bdf8540已completed/clean；M同一独立程序重跑三个PASS/exit0，接受模块修复（verified-module），未集成或原生整项验收。原red日志保留。

### E7-C5-ENTER-OBSERVATION-1 ｜ P2 ｜ open-unattributed ｜ 取消核对Enter一次未启动
- 66完整有一次Until超时、SQLite确认run仍Unknown且没有resolution；C5单跑通过。保真实Enter和30秒限制，新增输入前IsEnabled/Focus/CanExecute断言与失败状态诊断，67/68完整绿；原因未证明，不冒称产品已修。证据m-cycle66-full、m-cancel-failure-audit.py、m-cycle67-automation/full、m-cycle68-full。

E7-NATIVE-4后继：R3旧临时根副本+新冻结0ffb504成功一次，没有旧新同fixture对照，不升级fixed。页级菜单E7-TAB-CONTEXT-1另由真实R3右栏截图支持verified-native-on-0ffb504，仍不验68后继源码。

### E7-R05-REDO-OBSERVATION-1 ｜ P2 ｜ open-unattributed ｜ 冻结0ffb504全量Redo一次失败
- tmp/m-cycle63-full-final.log exit1、R05纯配置redo断言失败，其余通过；没有更改Restore逻辑或放宽断言。新增失败时输出composer/statusBar/status/root诊断，定向R05绿。瞬时文件竞争/系统错误是假设，后续全量保留失败证据再判，不称已修复。

- 第69轮初稿全量再次报告Access to the path is denied.（tmp/m-cycle69-skins.log、r05-tests-7ca4ba0bdc80493eabed4b31f2f9c86c）；后验ui.json及bak属性Archive，不能归因或称修复。原断言与目录保留。

### E7-AERO-M-CAPTION-SCOPE-1 ｜ P2 ｜ verified-headless ｜ 公共接线误把状态条当标题栏
- M初稿扫描所有chromeBar，包括没有窗口按钮的CustomStatusBar，Aero切换失败；实际初稿8个Aero失败见tmp/m-cycle69-skins.log。只识别包含真实CaptionAssetButton的caption，后继91公共检查通过，含主窗/浮动/确认及精确回退。不是原worker模块缺陷；系统原生另门。

### E7-SKIN-ENGINE-ASSERTION-1 ｜ P2 ｜ verified-test-correction ｜ 旧皮肤断言引用已失效层级
- Aero由未知键变已实现第四项，改精确四键并另保未安装键原回退保证；Metro hover诊断root=#2D89EF而实际绘制PART_ContentPresenter=#1E6FC4、pointer=True。用真实命中和唯一绘制层原精确色检查hover/离开，不接受近似或任选层；121断言通过。日志m-cycle69-engine-diag/final。

### E7-M-BUILD-COLLISION-1 ｜ P2 ｜ resolved-tooling ｜ M并发构建共享obj
- 两个引用App的自身构建并发导致MSB3061/MSB3026文件占用；改串行后solution0警告错误。原tmp/m-cycle69-solution.log保留，未关未知进程或降级警告。

### E7-NATIVE-4-R4-PROBE ｜ P2 ｜ open-unattributed ｜ R4崩溃假阳性归因排除
- R4报告PID 45720在启动含hidden floating的data4时重现CLR20r3 Dispatcher shut down并归因于floating.hidden=true。M独立对照探针证明：使用该ui.json.crashed-backup备份在0ed8以完全相同参数启动完全正常（PID 34792存活、窗口句柄有效、无崩溃）。崩溃现象确曾出现在事件日志，但非ui.json hidden配置引起；状态保持open-unattributed。

### E7-NATIVE-5 ｜ P2 ｜ tooling-misconfiguration ｜ sky浮窗按钮点击未命中独立窗口
- R4中worker尝试使用主窗WIN坐标与手动偏移计算点击浮窗caption按钮导致未命中。sky对多顶级窗口支持通过sky.list_windows()单独选择浮窗Window对象并使用其自身的screenshot/坐标系交互。R5已明确该正确调用方式。

### E7-NATIVE-6 ｜ P2 ｜ tooling-caveat ｜ UIA在最小化窗口调用触发COM异常
- Worker在窗口最小化状态下调用UIA FindAll/Invoke导致COM异常。已指导先通过ShowWindow/SetForegroundWindow恢复激活窗口后再调用UIA。

### E7-NATIVE-9 ｜ P2 ｜ verified-m-repair ｜ 触控板页签无法滑动且清空后栏位不回收
- R5人工实测发现触控板无滚轮下无横向滚动条，页签溢出无法切换；右栏页签全部关闭后显示“暂无页面”占位但不自动塌陷，挤压主聊天区域。M在第72轮修复：DockRegionView与BrowserView滚动条切为Auto，页签清空时自动触发Hidden收起，已通过Tabs与Skins自测验证。

### E7-NATIVE-10 ｜ P2 ｜ verified-m-repair ｜ 关闭侧栏页签后残余空黑框
- R5人工实测关闭文件预览/浏览器后侧栏未自动收起，保留固定宽度空白区域。M在第72轮修复：Restore与CloseDockPageCore对零页签区域自动标记Hidden，且ApplyWorkspaceLayout仅在存在页面时展开右栏，彻底消除残余黑框。

### E7-NATIVE-11 ｜ P2 ｜ open-m-repair ｜ 800×640窄窗模式控件重叠
- R5人工目检确认窄窗模式下操作按钮重叠、终端输入条空间受挤压。归属M主区修复。

### E7-MAIN-CAPTION-DRAG-1 ｜ P2 ｜ verified-m-repair ｜ 主窗口顶栏无法全局拖拽
- CustomCaption根元素缺少PointerPressed且被居中CustomTopCenter遮挡，导致空白处无法拖拽窗口。M在第72轮修复：将CaptionDrag_PointerPressed直接挂载在CustomCaption根元素并设透明命中背景，过滤按钮与输入框后空白区域均可拖动。

### E7-FLOAT-DRAG-STUTTER-1 ｜ P2 ｜ verified-m-repair ｜ 浮窗拖动频繁同步写库卡顿
- 浮动窗口PositionChanged与SizeChanged每次微小像素变化均同步调用SaveWorkspaceDimensions触发SQLite同步写入与JSON序列化，导致UI主线程严重卡死。M在第72轮修复：增加300ms防抖计时器，拖动期间实时更新内存坐标并在空闲后批量落盘，窗体关闭时主动flush，彻底消除拖拽卡顿。
