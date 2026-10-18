# 当前开发流程接入

## 显示未运行流程修正

后续用户指出仅显示两个。原因是旧实时视图只读取批准workflow，而当前plan只有D4。现新增“显示未运行流程”默认开启及localStorage持久记忆；保留全部项目规划层级、任务和依赖，显示2个真实绑定线程+8个规划归组。规划归组不是已创建native会话，状态显式非实时，不复用原型running/integrated；D4-S映射真实D4-SNAPSHOT而非重复规划。真实M合并规划任务，实际plan门禁和历史记录保持原来源。

关闭后隐藏规划、等待和已完成执行线程，仅保留running/starting执行线程和M监督节点；当前零执行时只剩M。开关只改变显示，不派工或修改暂停状态。真实浏览器检查全部10节点/8规划、规划状态未同步、零假运行、隐藏已完成D4、M保留、无悬空边、重载记忆、重开全部、57cedd5原集成证据保留，全部通过；console零错误/警告。证据output/playwright/automation-flow-filter-checks.txt及automation-all-flows.png，截图目视检查。

当次现场调度器PID22072仍alive、idle_complete且paused；固定M ready，上次模型结束04:03:49UTC。自动模式配置与暂停状态分开；没有剩余批准任务及待审事件，不调用M。M的编排职责目前没有把其余规划转为完整派工契约，不能宣称已完成全图自动推进；本会话主agent在继续实现用户要求的显示功能，区别于门房保存的固定M。保留现场暂停，不主动恢复。

用户要求将演示实际接入当前开发。本轮实现的是可立即使用的本机 CLI 控制台，不是原生 Avalonia tab。

## 实际接线

- 新增 automation-console.py / automation-console-control.ps1 与 live-console.js；127.0.0.1:18765 只服务本机页面。读取批准 plan、当前 workflow 的manifest/state/delivery/review及integration，排除未知会话与夹具。
- 固定M `01a126ff-597f-7231-9ecb-c287e8d17458`；D4保存会话 `01a1277f-6793-7411-bbdf-f3f6d9564f0c`，原模型devin/swe-2，原worktree不变。
- 第一次rework `3848c5ea6f5f42f6965deb78e0b669fd`，第二次accept `791deb5bb655448bad77f3995c5b6191`；源HEAD ef18d70263fb0076719222d5fdafe507523a37d0，独立集成57cedd57010e452a1d77c683f224b31ccf510819。accept与integration分开，未把pending_wiring旧审核当成当前未集成。
- 自动模式沿用已验证批准依赖/所有权调度、最多2worker、串行审核与单写入集成。默认调优按钮只改未启动任务为免费模型并去重依赖；这是默认策略，不宣称全局数学最优。
- 手动模式实际暂停新派工/审核，保留在途执行。任务选择、模型及执行/集成依赖编辑与完整JSON契约入口；已启动任务只读。保存草稿不派工。应用停止workflow并调用原生Approve；成功保持暂停、恢复单独执行。
- 原始字节备份保留原审批hash；失败恢复。不明apply claim禁止重发。界面不直接写review，不更改门禁/引擎身份，不接管未知线程；Host/Origin/token限制本机命令，无任意shell入口。

## 实测

`python -B scripts/automation-console.tests.py`：11项全部通过，覆盖workflow过滤、accept/集成区别、草稿隔离、循环/门禁拒绝、已启动身份保护、过期计划、原生控制路由、应用不派工、BOM/换行原字节恢复、不明apply冻结、坏ID/引擎身份拒绝。其中独立临时目录实际执行原脚本Pause/Pause/Stop/Approve，核对计划SHA和契约SHA及两个paused文件；没有调用模型。

真实浏览器验证：当前2个绑定线程/2次运行；真实HEAD/保存线程/集成SHA/rework+accept可见；示例操作禁用；手动按钮真实生成两个paused文件；编辑器保留原免费模型且已启动任务不可改；自动切换不偷偷恢复，单独恢复后两个暂停取消；缺token请求403；运行数保持2。证据 `output/playwright/automation-live-checks.txt`、`automation-live-restart-checks.txt`、`automation-live.png`，截图已目视检查。初次测试 page.request 相对URL不被CLI支持，改绝对URL后通过，未改生产数据。

控制台通过原生shutdown关闭再Start，PID41272→48776，父进程56780为独立wscript宿主；重开浏览器重新取token，policy auto及原两轮记录、计划恢复。生产gate PID33452、workflow PID22072维持，末态ready/idle_complete且未暂停。M lastFinished仍04:03:49UTC，D4运行数2，控制测试没有启动worker/审核/集成模型。浏览器console零错误/警告；未改八份冻结核心调度脚本。

## 当前使用与边界

入口及直接复制命令见原型README。控制台任务 `Fufucode-Console-0BB7DB6C31FD`；脚本路径、PID/端口写tmp/automation-console/server.json，控制审计audit.jsonl，HTTP日志server.stderr.log。浏览器每5秒仅访问本机代码，不发送模型请求。关浏览器/当前CLI不停止计划程序宿主；睡眠期间不执行，登录触发恢复用于重启后启动，本轮未做整机睡眠/重启试验。

没有恢复Desktop heartbeat（其automation.toml仍PAUSED）。没有push、删除worktree、迁移会话或触碰继承改动。当前没有开发模型工作：已批准唯一D4子任务完成，门房等新批准计划/交付。G1/G2/S2仍open，D5未越门。U1状态协议、U2执行宿主和U3原生tab继续独立阶段；本轮没有C#变更，不运行无关App构建。现有CLI仍整项交付，示例中的部分审核及单线程多任务不假称生产已实现。
