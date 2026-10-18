# M：只读刷新保持键盘焦点与连接切换

实际实现mainSHA `cc44eeef02c84c82847e104d31186a08f3d3c6a1`，精确24文件白名单提交，所有继承状态保持；后续只补记录/派发，不改变对应源码。集成仅本增量，最新installer/生产全范围仍待。

用户明确继续轮（2026-10-13），真实主仓库 `C:/Projects/Fucode/Fufucode`，main起点b644a3f。旧调度不恢复，R1已停写、E3仅原树修复；M取得独占main-writer租约，继承demo及未跟踪目录保护。验收条件：忠实焦点红转绿；SQLite读锁下输入不失能、显式切库等待且只接最后项目；同源self-contained Release clean PATH/新数据根全量稳定复验。不改Core协议或已有布局，不启动模型/昂贵测试lead。

## 真实原因与修复

AutomationControlPageView每2秒调用RefreshAsync；原VM在只读刷新时IsBusy=true，NotifyCommands使当前创建/审核按钮不可用，Avalonia丢键盘焦点，恢复enabled也不恢复focus。M先误把未BringIntoView的按钮作为初始焦点前置，tmp/m-continue43-focus-red.log两红保存，但它不能证明刷新因果。补正确可见前置后tmp/m-continue43-focus-red2.log清晰：实际初始Focus/IsFocused PASS，await未变化Refresh后IsFocused FAIL。

产品修复把只读观察与写入忙碌分离，以独占refresh gate序列化读取；不因轮询禁用输入，epoch/revision/event cursor拒绝旧快照覆盖。Core原版本/CAS与决定身份门保留，写命令与明确重连仍禁用动作。

第一修复版定向0失败、Release第一轮1200绿，但第二轮暴露两个切库/重挂FAIL；立即停止第三轮，保完整日志 `tmp/before-connection-fix-m-continue43-release-full-1.log` 和同名前缀results JSON，不挑绿。进一步让显式Connect等待原观察结束并落实最新pending项目；只读刷新不伪装连接已完成。新增真实SQLite EXCLUSIVE三检查，不更改原切库/重挂断言；最终定向0失败（原日志tmp/m-continue43-final-automation.log）。

## 证据与范围

最终标准solution0警告0错误，日志tmp/m-continue43-final-standard-build.log；App最终源码发布为self-contained win-x64，日志tmp/m-continue43-final2-publish.log。暂存独立tmp/m-continue43-published，不覆盖R1原两包或旧主线候选。

最终串行三次全量结果保存在tmp/m-continue43-release-results.json、release-full-0/1/2.log；每次剥离全局Node/Pi/Fufucode PATH，新临时cwd/data，使用同一发布物，不是Debug绿替代Release。具体三轮计数和最终源码SHA在本记录后续补录，不提前标完成。

实际门完成补录：同一最终发布物三轮各**1203项PASS、0失败、exit0**；C5最终199项（新增焦点2/观察切库3）。三进程PID35280/32436/10856，证据根 `C:/Users/mio/AppData/Local/Temp/fufucode-release-focus-m0_lqdtp`；log0/1/2与JSON均完整。先前1200绿/1198两红不是最终门，也不是被删除的日志。最终Core349/Pi21（Atria选跑SKIP）实跑通过；真实提交SHA随后只补录不改源码。

真实控件/编译绑定/Headless按键不是Windows指针/IME/DPI证明。验证的是已确认刷新失焦及连接时序缺陷，原不明PID复用与其他瞬时工装根因不自动关闭。新ZIP/setup与干净安装由R1固定新源再验；当前暂存发布验证不冒称新的安装包验收。
