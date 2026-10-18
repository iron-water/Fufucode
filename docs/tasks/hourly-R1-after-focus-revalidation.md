# R1-REVALIDATE：主线焦点修复后的当前候选复验

任务 R1-REVALIDATE｜L3｜原主模型 `group/auto-qfmodel`（保留R1原模型，隔离打包与真实安装链路）｜所有模型试跑lead显式 `devin/swe-2`，本包无需云请求。

仅在M提交并在协调消息给出完整冻结产品SHA后执行。复用原线程 `01a133b9-7e8d-7b61-a49a-cd02aed15303`、原cb9f树；不创建新的会话/工作树，不同步未来main。原交付a99693c、两套out、继承LICENSE状态/旧包/所有红日志保留。

## 有限基线更新（M明确授权，不能自行实现公共源）

原R1不允许改src的约束，对以下**按冻结SHA逐字导入已由M实现的文件**作限定例外。除此仍禁止worker实现或修改公共源码：

- src/Fufucode.App/Features/Automation/AutomationControlViewModel.cs
- src/Fufucode.App/SelfTest/AutomationControlCreationSelfTest.cs
- src/Fufucode.App/SelfTest/AutomationControlSelfTest.cs
- src/Fufucode.Cli/CliHostCommands.cs
- src/Fufucode.Cli/Program.cs
- tests/Fufucode.Core.Tests/CliHostTests.cs
- tests/Fufucode.Core.Tests/CliTests.cs
- tests/Fufucode.Core.Tests/Program.cs

开工核对原树没有这些路径的用户编辑/另一个writer；有不明内容先记录，不能覆盖。从协调消息固定SHA导入这些路径，单独精确提交“导入M冻结产品修复”，保护其余继承状态；核对整棵src及tests树与给定SHA相同，runtime锁/桥接未变。不能用任意未来main或重置整个树。保存原包装四提交，不rebase/改历史；独立提交给出原source SHA、导入路径和真实HEAD。

## 可写范围与执行

除上述基线导入，仅自己的 docs/records/R1-after-focus-revalidation-2026-10-13.md、新labs/packaging/clean-start-report-r1-after-focus*.jsonl，既有scripts/packaging实际缺陷修复及忽略tmp/out自有新目录。共享Core/窗口/设置/外观、全局记录禁止。公共实现再暴露缺陷交M，不放宽断言。

1. 先标准solution还原/构建0警告0错误，必要Core/Pi/Host入口回归。本轮M已提供原红→产品修复→当前完整门证据，不能当包内复验通过。
2. 新目录 `out/r1-after-focus`，版本保守 `1.4.4-candidate`；实际单命令构建ZIP/setup，记录固定产品SHA与程序集内嵌HEAD、清单/大小/SHA、托管Node/Pi来源。旧out/r1-refresh/r1-e2e/旧候选/继承输出逐字节保留；不使用Overwrite。
3. 新包clean PATH/独立数据根：App全量三次串行全部0失败，必须同一份包与字节身份。任一失败即保失败门并交具体协调线索，不挑绿。核验CLI公开host入口：临时authority明确初始化、独立serve/status/stop、第二宿主拒绝、错误ID不停止、退出清理；不发模型任务或接真实用户工作台。托管Pi发现/停止/残留、CLI跨进程恢复、完整性和启动器门按当前脚本保存实际命令/退出码/计数。
4. Windows Sandbox干净安装/启动/卸载/数据保留，用当前新setup，先确认无未知实例；原生验证串行，不停止他人的实例、不在宿主装卸或改系统。无环境/工具则记录具体阻塞、继续其他门，不借旧两份证据证明新版安装。
5. OMP尚未托管、WebView2/真实云聊天、IME/DPI、多显示器、升级迁移等未验如实保留。Control/Hosting是Core中的命名空间，**不是必须独立装载的同名DLL**；新的CLI host入口是当前实际部署增量，后台运行成功不等于Pi/OMP桌面H1或手机/全自动整项完成。

模型/配置不换，任何试跑显式最低成本。当前可用性校验/构建为本地代码，不新起昂贵测试lead。每项有真实红/绿原日志，background PID/出生/退出留证；未知状态先对账，禁止第二publish或并发同任务验证。

## 收尾

小步提交只含允许路径，完整人类卡写自身记录；最后只一个delivery.card JSON，schemaVersion1、taskId=R1-REVALIDATE、status submitted/module_ready/blocked/incomplete、真实全SHA/commits/changedFiles（写delta基准）/passed failed notRun/evidence/产物哈希/integrationNeeds/blockers/writerStopped=true/worktree/base/head/fixedProductSource。所有SHA用git实际查询生成；禁止猜测自引用SHA或附前导文本。停写保树，不push/reset/删树/清理/登录采购部署或启动下一任务。由M独立审核、收编、决定阶段验收。
