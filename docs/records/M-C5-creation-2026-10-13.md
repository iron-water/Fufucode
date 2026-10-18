# C5 原生创建入口（第三增量）

任务/软件：C5-CREATE，Codex Desktop 主控M，L3；实际主目录 `C:/Projects/Fucode/Fufucode`、main，起点a35f36045fc68088e81dda7c67b227b6551a98a4，派工意图9b06d6c后实施。实现提交 `40e778a60d30faa06147cc7d81f3e8b6097835b6`，integrationStatus=integrated仅第三增量；补录不改变门禁源码，未push。保护原用户demo JSON与未跟踪目录，原工作树/旧调度保留；不夹带它们。

本次18文件：App的AutomationControlPageView/AutomationControlViewModel、新增Creation partial/UserControl及code-behind、AutomationControlSelfTest与新增Creation/Mutation自测；Core的ControlQueue/ControlAdoption及新增ControlMutation；任务书/拓扑/此记录/failure-registry/台账/progress/decisions。最终本增量源码停写；新worker范围仍隔离，未收编其半成品。

## 实际实现

本轮新增原生任务规范创建/明确修订、知识提案、定向问题、独立/竞争/互补/流水线关系、单来源→单后继替代方案入口。新局部UserControl继承同VM编译绑定，复用App样式与浮动页，无新外壳、JSON编辑器、第二权威数据库、运行者/消费者。

规范输入包含ID/标题/目标/允许范围/验收/harness/模型/成果类型/依赖/知识引用；默认显式devin/swe-2/evidence，提交只是待批准规范。输入现有ID不能静默覆盖，明确选中原规范锁定ID并捕获版本，历史保留。知识来源必填，保存提案不发布；定向问题绑定不同规范真实ID/版本，不按相同标题判同对象。四种关系选择真实未批准规范，竞争必须基准SHA和比较条件，流水线必须明确顺序且恰好覆盖成员；Core继续验证执行状态、关联和环。

单项替代复制原规范供修改，新后继ID不可复用旧身份；预览原目标、后继目标、保留理由、受影响关系的新ID/成员/基准/依据。提交Core正式重组待审提案，显式映射所有受影响关系，不退役/批准/执行后继，不继承原接受或集成。复杂一对多/多对一原生创建编辑器另做；既有复杂提案批准表单可用。

打开表单捕获数据库/epoch/控制revision与所有原选项版本、知识版本；观察更新不替旧草稿换前提。失败保留输入，必须明确重开核查，不自动回退/重试。成功才清表单；返回、项目/数据库切换清旧输入与scope。API原ID回执重放，接受/采用/集成边界保持。

## 新缺陷与诚信

- C5-CREATE-OPAQUE-ID-1：Core合法的两端带空格旧ID在修订时统一Trim，指向另ID被拒；`tmp/m-c5-create-opaque-red.log`实际1 FAIL。修订时从明确选中的原对象保留精确ID，新建输入仍规范化。未放宽Core版本门。
- C5-MUTATION-WRITER-1：规范修订、采用、重组门仍只检查control绑定run；实际同线程额外HarnessExecutor held writer的SQLite备份分别测试三个路径，`tmp/m-c5-create-mutation-red.log`三个FAIL。共同追加门纳入同线程/同树真实Dispatching/Running/Unknown、其他queued/started请求；接规范替换与采用阻断，重组复用。保留合法“原规范尚未启动队列被替代”能力，不简单照搬ReviewStopped阻断所有queued。
- 工装初次误用DeliveryPart.HeadSha构建错误，已核实真实Graph元数据读取精确HEAD；最终构建结果单独记，不把失败隐藏。无云模型调用，无昂贵测试lead，本地合成协议参数显式devin/swe-2。

## 验证

最终定向 **101项C5 / 0失败**，原opaque-ID及三个heldwriter反例均通过，包含真实临时Git/SQLite/Core、实际Window/Canvas/编译绑定、按钮焦点Enter；这不是系统IME/DPI证据。证据 `tmp/m-c5-create-targeted.log`，临时根 `C:/Users/mio/AppData/Local/Temp/fufucode-selftest-b12077c24b9f4269831413a6cff02d69/automation`。

最终App全量 **1105 ok / 0失败**，其中C5 **101**（第二增量72→增加29），exit0；Control **511**、Hosting **267**、Core **340**、Pi **21**均exit0，solution **0警告0错误**。Atria实模型选跑SKIP，未把凭据存在当通过；本轮无模型云请求。命令在实际主目录：

```powershell
dotnet build Fufucode.sln --no-restore -v quiet
dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest --automation-only
dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest
dotnet run --no-build --project tests/Fufucode.Control.Tests
dotnet run --no-build --project tests/Fufucode.Hosting.Tests
dotnet run --no-build --project tests/Fufucode.Core.Tests
dotnet run --no-build --project tests/Fufucode.Pi.Tests
```

日志：`tmp/m-c5-create-{solution,app,targeted,control,hosting,core,pi}.log`；最终App临时根 `C:/Users/mio/AppData/Local/Temp/fufucode-selftest-3cfb7fc4c5684978827264e912823250/automation`。Control根fufucode-control-c91d156005654c68bb194bfecda4e443，Hosting根fufucode-hosting-tests-2e2c17a52e9542a295276a50aef6242f，均位于本机Temp。Core额外writer变更后已重跑Control/Hosting门，不沿用第二增量旧结果；既有合法原队列废弃/采用检查仍全通过。

## 第29轮监督与并行

app运输恢复，逐项核实6个已知受管会话终态。E2-B旧未送达集成通知成功补发，原chat已确认只读/不改树/不自动开新任务，真实源0fe7d98仍干净。旧gate/workflow PID23020/22868不在，历史队列只读元数据、跳夹具、无新结构化待审卡；不自动重启/转换旧卡。

新受管C5-PERSIST由M创建：任务书/意图已先提交9b06d6ce49db8e7df870a0691afb9a7bf3472274，L3/group/auto-qfmodel。client-new-thread:e1451839-bb95-48f4-b74f-df34a5cea6b8后list_threads对账确认真实chat `01a132c3-319c-7510-abe5-571ca2fc50c8`/local，树 `C:/Users/mio/.codex/worktrees/9fde/Fufucode` 起点9b06d6c，标题“C5 原生草稿与决定记录持久化”。最后观察active/inProgress（游标fe0920cb…:4），只读状态，不读半成品代码/卡/做验收。独立worker拥有新Persistence目录/独立测试/自身记录，M只改现有UI与Core公共门；源范围不重叠，未新增测试lead。

## 未验证/下一步

本增量不标C5整项/生产六门全关：系统原生指针/IME/DPI、生产宿主及消费者接线、跨重启原请求/草稿恢复仍待。持久化worker交付后须M独立反例复核并公共接线，不把模块交付当UI恢复完成。复杂拆合/图编辑/动画与性能另增量；新包安装/生产迁移未做，旧固定候选保持。皮肤待用户选择、macOS后置。继续收新卡/返工/集成与独立就绪项，不以等待单任务停整个流水线。
