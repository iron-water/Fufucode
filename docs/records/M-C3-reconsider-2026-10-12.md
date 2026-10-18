# C3 第十一增量：阻塞／延后成果再审

真实main实现SHA：54777a00d898f2f3605e6404d4f5801276a53fdb。integrationStatus=integrated，仅再审与历史迁移/断连修复增量；C3/C4/C5整项仍open。后续SHA补录提交不改变该产品源码。

M在C:/Projects/Fucode/Fufucode的main单写，起点43a456d5606a13228730120085c2ee31a9aa9c64，难度L4。读取当前AGENTS/计划/拓扑/台账后核实：app wait_threads仍Transport closed；已知E2-B本地结构化事件仍task_complete，源1dd7 HEAD0fe7d9889315959edbb1876f7545ae608cf80f32干净。旧gate/workflow原PID23020/22868均不在；旧CLI队列仅元数据/明确历史决定，probe/transport夹具跳过。未重新投递通知、重派、接管未知会话或恢复旧调度，执行空槽保留。继承用户文件不改、不暂存。

## 已实现

- 新增显式ControlReconsiderCommand/Receipt和CLI `control reconsider <absolute.db> <command.json>`，接Python/HTML相同入口。只允许当前批准且停写的Blocked/Deferred成果，理由必填，绑定task/part revision/run/HEAD/context token及原decisionId/CAS版本。Accepted、ReworkRequested、已集成、Unknown、冻结线程和撤回批准均拒绝；ReworkRequested仍须原执行者修复交付，不用再审绕过代码返工。
- 重新审核只把该成果置Delivered并使完成线程回到Active；不改卡、run、HEAD、交付revision或任何旧决定，不生成运行、不发送提示词、不撤销集成。旧决定/再审原因、上下文、事件与幂等回执持久保存。新决定按插入顺序追加；即使调用方时钟回退也选真实最新记录。兄弟成果各状态保持，accept不集成。
- 再审/状态/线程与任务状态/事件/control revision/回执同事务。迟到原ID重放在后来再审、接受及进程重开后仍返回原回执；改理由或用旧decision/context拒绝。直接U1追加决定也必须有匹配最新决定的显式再审记录，不能只改状态绕门。

## U1 数据升级

旧U1 v1表UNIQUE(part_id,revision_number)使同交付无法再次决定。正式升级AutomationStore.SchemaVersion到2：在事务中复制全部决定字段和插入顺序，保留原decisionId、actor、UTC、证据，再替换约束和建索引；原交付、事件、session与集成标记不改。并发开库在锁内重查版本，避免二次迁移；异常旧引用使整次升级回滚，原v1版本与数据保持。

前期C3仅增加Control表时要求兼容旧U1构造器；本次改变的是U1决定历史语义，不能让不理解再审的旧写入者继续处理新库。旧SchemaVersion=1构造器的已有版本检查会拒绝2（代码依据，未启动旧发布二进制）；任务书已明确区分纯Control扩展和正式U1迁移。只在临时夹具升级验证，未打开或迁移用户生产库，不回改固定P2候选；新候选的实际数据迁移/备份恢复另验。未设置兼容谎报版本或删旧决定来制造通过。

## 实际验证

全部无模型调用；devin/swe-2只是Host夹具元数据。命令显式workdir主仓库。

| 门 | 实测结果 | 本地证据 |
|---|---|---|
| dotnet build Fufucode.sln --no-restore -v quiet | exit0，0警告0错误 | tmp/m-c3-reconsider-solution-build.log |
| dotnet run --project tests/Fufucode.Control.Tests --no-restore | 388 PASS，346→388新增42项 | tmp/m-c3-reconsider-control-final.log |
| dotnet run --project tests/Fufucode.Automation.Tests --no-restore | U1 58 PASS，ALL PASS | tmp/m-c3-reconsider-u1.log |
| dotnet run --project tests/Fufucode.Hosting.Tests --no-build --no-restore | 257 PASS | tmp/m-c3-reconsider-hosting.log |
| dotnet run --project tests/Fufucode.Core.Tests --no-build --no-restore -- --cli-contract-only | 29 PASS | tmp/m-c3-reconsider-cli.log |
| dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest | 996 ok，0项失败，exit0 | tmp/m-c3-reconsider-app-full.log |
| python -B scripts/control_console.tests.py | 10 tests，OK | tmp/m-c3-reconsider-client-final.log |
| node scripts/control-commands.tests.cjs | 29命令检查 | tmp/m-c3-reconsider-commands.log |
| python -B scripts/control-reconsider.tests.py --database C:/Users/mio/AppData/Local/Temp/fufucode-control-1a398faf8dc84559a2298d322e4edf83/integration.db --cli C:/Projects/Fucode/Fufucode/src/Fufucode.Cli/bin/Debug/net10.0/Fufucode.Cli.dll | 9组真实HTTP再审→接受 | tmp/m-c3-reconsider-http-final.log |
| python -B scripts/control-review.tests.py --database C:/Users/mio/AppData/Local/Temp/fufucode-control-7f0798a681a04b07bf616dd8e9871559/integration.db --cli C:/Projects/Fucode/Fufucode/src/Fufucode.Cli/bin/Debug/net10.0/Fufucode.Cli.dll | 9组原审核HTTP回归 | tmp/m-c3-reconsider-review-regression.log |

Control42新检查覆盖：错身份/旧版本/旧决定/空理由、Unknown正文遮蔽、冻结/撤批/集成回退拒绝、晚期回执故障全回滚、完整线程再激活、兄弟隔离、反向时钟下的多轮决定、直接U1门、原ID迟到重放及CLI跨进程回执；真实v1唯一约束夹具字段/身份/卡与marks保真、重复/并发升级、非法旧外键失败回滚及修正后重试。初次非法旧数据注入被夹具连接自身FK检查拒绝，未到迁移探针；改为明确仅在夹具连接关闭FK后注入，最终真实迁移失败回滚门通过。该失败不当产品迁移缺陷，不放宽期望。

Playwright自有会话m-c3-reconsider-20261012、临时副本18777：选择reconsider-blocked→准备决定自动带原decision/run/HEAD；填理由后79→80，该条从reconsider变review；再填写Accept与证据后80→81，待办消失，review-blocked/race1/untouched仍保留。SQLite独立核对：原Blocked/human与新Accept/human-browser-fixture两条决定、1条原delivery、0条integration mark。快照.playwright-cli/page-2026-10-12T20-00-55-495Z.yml、20-01-43-982Z.yml、20-02-14-186Z.yml、20-02-44-874Z.yml；截图output/playwright/m-c3-reconsider-accepted-20261012.png已实际查看，tmp/m-c3-reconsider-ui-verify.log。自有Chrome关闭，服务exec69846以Ctrl+C退出且进程复查不在；证据保留。

## 新发现及修复

Chrome关闭时HTTP GET回传触发WinError10053，respond异常被上层解释成服务错误，再尝试发503到断开socket又异常。登记C3-HTTP-DISCONNECT-1；单写断连反例红于ConnectionAbortedError（tmp/m-c3-client-disconnect-red.log），respond只吞连接类异常，不再次响应、不重试或回滚Core决定。10项Python测试包括三类断连×header/body六个写边界，以及真实HTTP：Core完成提交后客户端RST断开，服务继续可读，原ID重放返回同回执，版本保持1且只有一个工作包、零未处理请求异常。原审核与再审HTTP9/9亦复验通过；不是靠关闭日志隐藏未知执行结果。

## 仍待

这是再审路径增量，不是C3/C4/C5整项验收。原模型原会话的部分修复意图/受信续接/交付接收及自动stop传输、项目目标/权限、最终原生图、全自动策略仍待。维护JSON表单不是最终原生用户界面；App无头全量和Chrome不证明Avalonia真实IME/DPI。Core340/Pi21全量与安装包门本轮未重跑，候选旧SHA保留；生产用户库升级/回退没有实测。

下一步为原身份修复命令和Host受信消费：重用已登记会话/runtime、核对停止者与Git来源，不把ReworkRequested当已投递，更不能在未知归属时重启。桌面监督通道恢复后先对账再派独立功能包；单项外部阻塞不阻止M独立开发。
