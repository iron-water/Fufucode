# M 独立复核 E2-T 第一轮

任务E2-T、L4，原模型group/auto-deepseek-v4-1-flash，chat01a1301a-127c-7e81-a841-81a552c58864/local，ba55树，起点3fb78d3/产品bf7ad40。wait游标4006b5e1-50c2-45a1-bc2e-0353462085d3:4，idle/completed、最终卡主动列出全部提交并停写；M确认HEAD156eacdd24f037a99393aa6672e6c4408f3f3cb9干净，10文件在允许范围，未有该树App/dotnet写入进程。

decision=rework，reviewedHead=156eacdd24f037a99393aa6672e6c4408f3f3cb9，integrationStatus=not_integrated；审核主线产品cf509d8，M同期C3授权增量未提交。不把模块待公共接线或原生工具缺失当返工原因；下面两项是M实际复现的产品缺陷。

1. **E2-PASTE-TARGET-1**：TerminalScreenControl.PasteToDraftAsync先await读取剪贴板，再取Session。真实控件+两个TerminalTabViewModel+延迟TaskCompletionSource读取：在A启动粘贴后切到B、再完成剪贴板，A草稿未追加，B草稿收到A操作内容。违反每tab隔离，未来显式运行可能在错误shell执行。调用应绑定发起时tab；切tab/隐藏/关闭期间的完成行为需明确并有真实异步反例，不把await之后的当前Session当来源。
2. **E2-SELECTION-GRAPHEME-1**：现有TerminalBuffer实际Append("Ae\u0301X")给出A/e/组合附加符/X格；TerminalSelectionText.Extract(snapshot,(0,0),(0,1))实际输出Ae，遗漏组合附加符，拆开字符。需在局部选择模型按字素边界扩大/提取，不能改任务外VT解析器；起选落在附加符、emoji ZWJ/变体选择符等相关边界也须覆盖，保持原宽字符/跨行/回滚快照保护。

M独立可重跑证据：主区忽略tmp/m-e2-review/M.E2.Review.csproj引用原源App，Program.cs实际控件及公开模型调用；dotnet run --project tmp/m-e2-review/M.E2.Review.csproj --no-restore，exit1、两项FAIL，tmp/m-e2-counterexamples-final-red.log。未编辑执行者源码/测试，没有云模型请求。早先临时试验对历史编辑行为有一个额外假设，因任务要求是原未发送草稿恢复，不据此登记产品缺陷；最终忠实反例只上述两项，保留早日志但不把它当验收依据。

已读取source记录、源码和全量日志末尾0失败声明；尚未独立运行源全量/真实ConPTY或验证本轮图片，不将执行者口头69/866断言当M独立门禁。两反例修后复跑原套件和构建，M再独立审核；公共快捷键/动作注册由M负责，模块待接线有效。原生指针/真实剪贴板/IME/DPI仍按工具阻塞留未验。

原L4/原模型/原chat/原树返工，范围不变。只追加实现/必要忠实反例/本任务记录，不同步未来main、push/amend/清理/删树。复杂度仍L4，未较原始升级两级，不提示换模型或昂贵辅助。保存原snapshot及日志，完成主动终态完整卡并停写。

## 第一次修复交回后的独立审核（13:34 UTC）

wait游标4006b5e1-50c2-45a1-bc2e-0353462085d3:6，原会话idle/completed，实际HEADb16a36c145491c3dbcdfd97522d4d16e4bf94b44；Git干净、无该树App/dotnet/MSBuild进程。四个追加提交6ed9bad/516ea1d/ba308cd/b16a36c，五文件均在原允许范围，未改共享入口。

M原两反例独立复跑均PASS（tmp/m-e2-fix-counterexamples.log）：await前固定发起tab、Dispose后丢弃、不同当前tab不交焦点；ASCII基字符的组合符原反例恢复。E2-PASTE-TARGET-1可标verified，仅为模块缺陷修复，不代表原生剪贴板/IME通过或主线收编。

字素处理仍有真实残留：新增实际TerminalBuffer反例，不修改源测试/VT解析器。`A中\u0301X`选择0..1实际A中、缺附加符；`A👨\u200D👩X`选择0..1实际A👨、缺ZWJ及后半表情；`A🇨🇳X`选择0..1实际A🇨、拆开旗帜。原因是字素扩展在宽字符续格空文本处停止，并且手写扩展码点不包括成对区域指示符。同一连接表情选择0..4完整，证明边界缺口而非全模型失败。tmp/m-e2-grapheme-width-red.log，六个断言三PASS/三FAIL、exit1。

decision=rework，reviewedHead=b16a36c145491c3dbcdfd97522d4d16e4bf94b44，integrationStatus=not_integrated。E2-SELECTION-GRAPHEME-1保持open，新增E2-SELECTION-WIDE-JOINER-1追踪宽续格/旗帜边界；原L4/原模型/原树第二次返工，无难度升档。要求采用完整行文本字素边界与真实cell映射或等价准确实现，起/止/宽续格、高亮/复制一致；禁止放宽既有反例或改任务外VT。未跑新的全量/ConPTY/截图，因为仍有确定产品失败；通过后再作独立全量与接线。

## 第二次修复独立验收与收编决定

原chat最新游标:7，idle/completed，实际HEAD2b6a74ca4da8c1ae048ed36fd9e7cd05a2b51e5c干净、无该树App/dotnet/MSBuild进程；追加4fc0ccc/20ff6b1/47036bb/2b6a74c，四文件在范围内。初始起点3fb78d3..最终HEAD共13个线性提交/10文件，Core/共享入口/用户文件未改。

M独立执行证据：

- 六项实际控件/真实buffer反例均PASS，tmp/m-e2-grapheme-width-green.log；旧红日志保留，M工程未降低断言。
- pwsh -File tmp/m-e2-independent-verify.ps1（主区忽略工装，显式sourceRoot/命令参数）：源solution构建0警告0错误，tmp/m-e2-final-source-build.log；默认App893项ok/0失败/exit0，tmp/m-e2-final-source-selftest.log；真实ConPTY输出→有界snapshot→选择/查找→resize→exit0，tmp/m-e2-final-source-conpty.log，0失败。
- M查看源320DIP light/dark两张无头图片，输出/输入及复制/查找/返回底部/新建/结束按钮位置可读；这是截图检查，不是原生点击或IME证据。

decision=accept，reviewedHead=2b6a74ca4da8c1ae048ed36fd9e7cd05a2b51e5c，integrationStatus=pending_integration，mainBefore=4275373（F1浮动宿主先行）。模块有效交付，M接共享动作/快捷键；源硬编码只在独立控件回退，App需绑定表优先、改绑后旧默认失效、禁用不退默认、保留Ctrl+C无选择中断。原生/跨DPI/真实clipboard/IME/长ConPTY任务仍未验；accept不自动表示主线已接完或整项验收。收编后真实mainSHA与门禁追加，不改源交付卡。
