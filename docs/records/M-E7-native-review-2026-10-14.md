# M E7-NATIVE首轮交付审核（第61轮）

补漏来源为提问线程协调通知，并非执行者自动回传。M本轮main起点08b67cf，持main-writer租约PID49316。执行者原树0ed8、源HEAD `6ee5b51e78246c972f58ec07f005a72d3eb19af3`，turn01a13998-e0c7已completed/idle、工作区干净，唯一提交包含记录和10张JPG，符合允许范围。原应用PID2836/38232不在，保留的node_repl/cua kernel不等于仍在写源码；未关闭这些kernel/未知应用。

## 证据核查与决定

**rework / not_integrated / mainSha=null**，绑定真实run/HEAD见E7-native-review-rework-2026-10-14.json。不要求受限验证者改产品或接入口。

- 构建原日志为0警告0错误；git diff固定7deef34与构建基线cd1b470的src为空，产品来源吻合。
- M查看M3设置、浮动文件页和终端原截图。换肤/浮动确有原生图；终端输出确实是`cho 你好中文测试`，不能忽略这个观察。
- 原调用三次所谓右键均`sky.click(... mouseButton:"right")`。computer-use本机api.md明确ClickInput字段`mouse_button`。这三次输入不证明右键产品失灵；优先修验证方式，不能因此改DockRegionView或绕过ContextMenu行为。提取脚本tmp/m-cycle61-native-calls.py只读本已停写会话工具调用。
- 终端调用在一次单元中坐标click后立即type_text，缺guidance要求的单独focused_element观察/确认。截图证明缺字现象，原因仍未知；需真实逐键/文本注入/粘贴及普通TextBox对照。mock命令回显不证明ConPTY或中文IME。
- 首轮最终机器卡前带说明文字、缺status/verification并用commit对象，按规范不能直接进入strict队列。保原卡/记录/失败列表，下一回交严格唯一JSON规范卡，不从正文词推断accept。
- 整栏浮动、隐藏重开、Tab焦点、800×640、IME/DPI/复制粘贴/本地浏览器/真实后端多项未验；部分是受错误右键工装影响，部分尚无明确外部阻塞。仅补可操作项，剩余逐项记notRun原因，不能宣称完整原生/G3通过。

## 回传与后续边界

首轮创建/第一次正常续接发生在直接人类回传授权之前，M按后续协议没有注入在途任务，因此本卡未自动发消息。这不是已启用worker回传后传输失败。此次正常返工将携带已核用户原文与来源、M真实threadId、原任务身份/稳定新修订eventId、停写落卡→一次回传→最终JSON退出，旧worker树不必有新协议文件。

遇用户输入保护/工具拒绝，必须记录阻塞并停止相关操作，不能用ShowWindow/自定义Win32输入绕开Sky或抢用户焦点。未发生真实worker发送成功前，回传机制仍未验。本轮不启动新重复任务、不改原模型、不打断Aero；两槽保持原受管任务。
