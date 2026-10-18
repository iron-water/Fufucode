# M：后台刷新后的旧输入保留

实际实现mainSHA `c417db8c62c8192874f7685419baae96099213c9`。13文件白名单与暂存实际集合逐项核对，diff --check通过，未夹带继承demo/目录。状态integrated仅本增量；本补录只改文档，不改变最终门禁对应源码。

用户时区2026-10-12继续轮（UTC2026-10-13），任务C5-STALE-INPUT，L3，M公共入口单写。主仓库 `C:/Projects/Fucode/Fufucode`，main起点 `350fa461acf1b0e677ee0623f64d3206b3f4574d`。已读当前AGENTS/权威计划、拓扑、执行/交接约定与台账；受管持久化与浏览器会话均idle，无新卡，不接管其他用户会话或恢复旧调度。核实旧主线写入租约释放后取得本轮独占main-writer租约。继承用户demo JSON及未跟踪目录保持。

## 真实反例与修复

`tmp/m-c5-stale-red.log`保留实际exit1：先通过编译绑定回答框保存，再修改多行Unicode回答，另一Core客户端回答原问题。刷新移除旧待办时回调清空Reason/Evidence/Response，最新输入不在原已保存草稿，之后保存也无法保留。旧已保存草稿存在，不代表新编辑未丢失。新增严格读取本地文件反例失败，登记C5-STALE-INPUT-1。

修改只限App自动工作台VM/View与相关自测：后台使选中待办失效时，先捕获原待办ID/版本/context/token与最新理由、依据、回答、候选，显示单独的只读旧输入区。原选中待办清除，旧提交保持禁用；用户可复制、明确保存原草稿或明确丢弃临时输入。保存不重写原版本/上下文，不提交Core。此前已保存的稳定草稿ID保留，重复刷新不创建副本。

保守可用选择：未保存临时输入仅属于当前工作台连接。项目/库切换或关闭前文案提醒保存；切换清临时缓存，已保存文件不删除、不暴露给新scope。不是自动保存。用户自己的成功决定只结束它对应的输入，不把其他待办输入视作已经提交；失败或外部变化继续保留。暂存旧输入后打开其他表单不会借用旧稿ID覆盖新稿。

## 验证记录

最终源码实际命令：`dotnet build Fufucode.sln --no-restore -v quiet` → exit0、0警告0错误，日志 `tmp/m-c5-stale-final-build.log`；`dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest` → exit0、1198项通过、C5 194项通过、0失败，日志 `tmp/m-c5-stale-final-app.log`。较起点新增9项：旧可执行决定失效但最新输入保留、编译绑定预览、原稳定草稿身份、实际最新文本落盘、原版本/context保真、重复观察不造稿/无新决定、丢弃不删保存稿、从未保存输入仍可保存、项目隔离/旧文件字节保持。证据根 `C:/Users/mio/AppData/Local/Temp/fufucode-selftest-60b8824b88c24e22b2e51d79ff1a5bd9/automation`。

最终对账可用性检查诊断busy=False、理由/依据绑定均True，但不据此关闭未确认的旧瞬时根因。Core/宿主/持久化独立模块源码未变，本轮没有机械重跑其上一轮已通过的同一套测试；这里没有宣称它们又跑了一遍。最终源码之后只补文档。

第一修复版定向 `dotnet …/Fufucode.App.dll --selftest --automation-only` exit0，日志 `tmp/m-c5-stale-final.log`。真实窗口编译绑定显示/焦点保存、实际SQLite双客户端、落盘文本/版本与Core事件不变均验证。随后补上项目切换和未曾保存输入用例，并把成功命令的输入完成标记绑定其原选中对象；最终源码重新构建/全量结果见下方补录，不拿较早绿色替代最终源码。

同第一红日志另有旧对账CanExecute断言失败，之后严格旧证据拒绝、实际新核查和焦点提交均通过。原失败未记录瞬时状态，不能把观察忙碌期当已确认根因。仅检查前等待真实IsBusy=false并加入busy/理由/依据诊断，原有效性断言保持；C5-RESOLUTION-AVAILABILITY-TEST-1仍open。既有按键偶发/PID复用问题也不关闭。

本轮不改Core/宿主/协议，也无云模型请求或昂贵测试lead。系统原生IME/DPI、跨进程长稳、新候选包和干净安装未验证；无头编译绑定输入/按键不等于系统原生验收。下一步仍原生补验/恢复路径和当前候选包，皮肤等待设计选择，macOS留后续。产品源提交和真实门禁补录后停写，未push/reset/清树/采购部署。
