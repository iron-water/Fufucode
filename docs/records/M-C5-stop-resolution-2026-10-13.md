# C5 取消后的明确中断对账（第五增量）

实际产品mainSHA `470c8cd91a610b202ccd0e0b6ea07fe410413db5`，21文件在独占租约内显式提交，integrationStatus=integrated仅第五增量。门禁对应实际源码，补录仅改文档；C5-PERSIST仍最新rework/not_integrated，不把主线提交当模块接收或C5整项验收。

最终验证：solution0警告0错误，App全量1168条ok（C5 164，较第四增量新增35）/0失败，targeted164/0失败，Control511/Hosting267/Core340/Pi21全部exit0；Atria实模型选跑SKIP。最终App证据Temp/fufucode-selftest-a6428049f2484982938042a7e0709a2e/automation，定向根fufucode-selftest-524aeb6ced3748bc99220d994e3c1e41。日志如正文，最终app-final优先于先前失败/较窄源码的app记录。源码最终已验证，实际mainSHA提交后补录。

按键工装根因项仍open：成对按下/释放后另一Creation Save仍超时，tmp/m-c5-reconcile-command-final-red.log保留；追加明确Activate目标Window再Focus及完整Creation超时诊断。最终定向/全量通过，但不足以倒推唯一根因或永久关闭该问题。Windows路径缺陷和Busy检查工装项已有真实最终反例回归，verified；原PID复用长期边界仍open。未放宽任何原断言、直接Execute替代窗口按键或关闭观察。

任务C5-RESOLUTION，Codex Desktop M/L4；实际主目录C:/Projects/Fucode/Fufucode、main，起点daee2835022ddbaab06006c513e541b69de11371；独占main-writer租约内单写，实际实现SHA验证后补录。不涉及C5-PERSIST执行者目录，不push/改写历史/删除树/登录付费部署；原demo JSON与未跟踪内容保留。

## 产品行为与范围

选择线程/任务后从取消记录中明确选择run，点“核查原运行与工作树”。Core只读复用原RunReconciler完整PID/出生/子进程规则，核对原stop上下文、run/thread/task/原树、真实当前Git HEAD和tracked dirty状态。不新建或迁移权威库，不从输出正文猜成功。UI显示原请求/真实HEAD、原写入者可核查性和文件是否仍有修改；核查本身无状态写入。项目/库切换、换选择、返回/开其他创建表单清旧核查，不借旧依据执行新目标。

仅支持原Host已记录attempt_finished_reconciliation_required、队列Settled且原run仍Unknown的取消。人类输入理由及实际核查依据，确认“记录为中断”：Core重新核查，绑定run/thread/task、stop命令、evidence token、HEAD、原revision，原子写Unknown→Interrupted、唯一harness_resolutions、该run未决claim结清、取消reconciled_interrupted、control/harness事件、revision及原回执。事务失败全部回滚。原ID原输入重放回执；同ID改依据/目标拒绝，新ID不能重复解决已中断run。

只有Interrupted目标，不给Completed捷径，不产生settle HEAD、不接受卡、不推进集成，不自动解冻、恢复原session或重派。原model/runtime/session/写入者身份与原文件保留；脏文件不清理，只如实展示并由人类明确核对。还在请求/原queue Started、身份损坏、树不符、活writer、复用PID、不可核查、不可读Git、旧HEAD/revision/token/目标均拒绝。取消自然完成结果不归为中断。Windows子进程规则沿用既有实现，不宣称已经排除所有未受管外部写入者。

代码范围：Core/Control新增StopResolution及typed命令/回执，既有RunReconciler抽出共享只读AuditRunAsync（原路径及判断顺序保持），App现有VM/View/创建入口加StopResolution partial，实际控件和协议子进程SelfTest。没有通过迁移型HarnessRunStore在客户核查里创建表/改库。

## 验证与反例

实际上轮取消协议子进程退出后的Unknown继续走本增量，不手工造运行状态。原取消/完整规范与知识问答/旧门全保留。核查无事件写入，错误thread/task/stop/token/HEAD/revision、空/超限依据均拒绝；四份真实SQLite备份只改变队列/取消/元数据，原run仍Unknown，检查pending/Started/出生错配/工作树错配拒绝。备份里真实SQL trigger阻断resolution INSERT，证明run/stop/revision/event/claim一并回滚；去trigger后真实成功、原回执幂等/改依据拒绝，主fixture保持Unknown。

真实编译绑定ComboBox按run ID选择，理由/依据使动作可用，窗口聚焦Enter按下/释放实际Command.ExecutionTask完成；在旧核查后真实Git空提交改变HEAD，原请求拒绝并保人类输入。再次核查明确清旧依据，真实tracked文件修改被报告；记录Interrupted后原脏字节/原session/task/writer不变，无settle HEAD，唯一人类resolution/原取消结果可重连读取，仅当前Unknown claim结清，独立claim保持。原线程状态不改变，原队列不重入，Interrupted不能作为交付卡。无头Avalonia+实际kernel子进程证据不代替系统原生指针/IME/DPI。

```powershell
dotnet build Fufucode.sln --no-restore -v quiet
dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest --automation-only
dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest
dotnet run --no-build --project tests/Fufucode.Control.Tests
dotnet run --no-build --project tests/Fufucode.Hosting.Tests
dotnet run --no-build --project tests/Fufucode.Core.Tests
dotnet run --no-build --project tests/Fufucode.Pi.Tests
```

日志tmp/m-c5-reconcile-{build,targeted,app,app-final,control,hosting,core,pi}.log，显式workdir主目录，无云模型或昂贵测试lead。Core340、Pi21、Control511、Hosting267均exit0，Atria选跑SKIP保持。第一轮App1164/C5 160全部0失败；追加四个元数据门后最终App计数待回填，solution0警告错误。第一轮App根Temp/fufucode-selftest-3d76d9a3c0e3429584acdbd4f20efe5e，Control根fufucode-control-f7ef69e12eb04da0b7045aed781a9fc4，Hosting根fufucode-hosting-tests-10c1f41eefc14589b6a8b13e8121ea8c。

旧工装两项实际失败及红日志保留：C5-CREATE-BUSY-TEST-1（Unicode字段正确但观察IsBusy时CanExecute=false，检查前等待真实空闲）；C5-KEY-SEQUENCE-TEST-1（原PressDecision只按下Enter不释放，后续新ExecutionTask等待超时；补实际成对输入并记录超时busy/focus/身份诊断，保新Task和全部旧断言，原因仍是基于现场/源码的推断，不据一轮绿过度认定）。第一轮全量通过；最终验证后记录状态，不关观察/直接Execute取代按键证据。常规首次编译接线错误已按真实Avalonia/API修正，最终不降警告。旧PID复用长期边界仍open。

## 第31轮监督与下一步

追加错树门的实际全量抓到C5-RESOLUTION-PATH-1：SQL字符串等式区分大小写，原Harness日志uppercase与线程保留大小写本是同Windows目录，却查不到原run。tmp/m-c5-reconcile-worktree-red.log保留FAIL，ignored只读元数据脚本确认现场。改双方完整绝对路径规范化并按Windows OrdinalIgnoreCase/其他平台Ordinal比较；不放宽thread/task/stop出生身份或合法错树拒绝，最终回归后补录。nullable警告加真实null拒绝条件，未降级编译警告。

2026-10-13 02:57 UTC触发。先读当前权威入口/台账/HEAD/继承状态，旧队列仅元数据且跳probe，无新结构化卡；旧调度PID23020/22868不在。C5-PERSIST修复原会话起初active只看状态；随后completed/idle、树干净HEAD5b546f2/允许17文件/无树专属writer，才独立审卡。166+原M五反例全通过，原四项verified，结构化accept/pending_wiring已通知原会话，只读确认停写。接线前新增精确opaque身份/null知识版本四反例FAIL，生成另一个明确rework/not_integrated，不覆盖旧审核历史，尚未收编。具体证据见M-C5-persistence-review-2026-10-13；原L3/group/auto-qfmodel/原树已收到完整限定返工，无新派包、未知会话接管/昂贵测试lead。

下步：停写修复卡独立复核→M收编并公共草稿/原请求接线，包括新stop/resolution；完整Unknown通用失败恢复、未消费/崩溃取消对账、生产宿主及原生IME/DPI/当前新包另验。本增量仅取消后明确Interrupted出口，不等于全自动/整个C5或生产六门全部完成。用户皮肤选择/macOS计划保持边界。

本轮收尾：C5-PERSIST原新返工turn01a13347-9cce-7bb3-8c37-19b016e460dd仍active/inProgress，最后游标fe0920cb…:19。运行中实现/测试口头结论不作为交付/验收，未读新源码/卡或收编，等正式停写卡；M公共第五增量完成后仅按证据提交。不新增第三执行者/新任务，也未把接受旧轮次通知当新授权写入。
