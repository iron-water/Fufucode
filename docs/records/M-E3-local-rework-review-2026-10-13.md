# M：E3 第一修复轮独立复核

2026-10-13 13:55 UTC监督入口。main起点 `7599cc295e6fae48e5315872c6118428b013a5f4`。E3原线程 `01a1345a-47b6-7460-94d0-ae3218a0dc90` turn `01a13560-5b88-76e2-be8c-81369f30fa42` completed/idle，正式submitted卡、writerStopped=true。原2884树HEAD `200a36a46fdae8958167a9189343f224c7742990`干净，无相关测试/写入进程；两新提交 b3cdfa9/200a36a，修复delta9文件，累计15文件都在允许范围。未读运行中半成品。

## 已验证

- M在2884树运行主仓库临时脚本 `pwsh -NoLogo -NoProfile -File C:/Projects/Fucode/Fufucode/tmp/m-cycle42-service-check.ps1`：交付源码独立工程Release构建0警告0错误；服务套件125 PASS、exit0。日志在worker tmp/m-review42-build.log和m-review42-services.log，worker已停写后M执行。
- M在主仓库 `pwsh -NoLogo -NoProfile -File tmp/m-e3-review/recheck.ps1`：原Program.cs五断言完全不改，引用刚重建交付产物，5 PASS、0 failed、exit0，日志tmp/m-e3-review/rework1-green.log。旧红results.log未覆盖。旧四finding在原反例限定范围verified，不能当模块接受。
- 新相邻真实临时文件与可控异步回调 `pwsh -NoLogo -NoProfile -File tmp/m-e3-adjacent/run.ps1`：**4 FAIL、exit1**。源码和日志同目录，数据根 `C:/Users/mio/AppData/Local/Temp/fufucode-m-e3-adjacent-59a1380fbbe344e286df1f04a3008417`。不修改worker源/原断言，不运行云模型/原生UI或用户真实文件。

## 仍需修复（原L3保持）

1. **E3-SUPERSEDED-INFLIGHT-1 / P1**：挂起Apply1宿主回调，Apply2成功后新OperationToken清空；此时明确移除1返回成功且删入口，第一回调还没完成。一条FAIL。只保护最新PendingApplyVersionId不足以保护所有仍在途候选；旧宿主副作用实际仍可能发生。可按插件显式串行拒绝竞争，或持久登记所有在途操作/候选并阻断对应移除；不能只CAS最后目录选定字段。若后续串行化，严格替代用例要证明第二请求拒绝且没有第二宿主调用、第一文件/决定保持，不删除竞争场景。
2. **E3-CANCELLED-COMPLETION-1 / P1**：宿主回调取消调用方token后返回Success=true（迟处理取消的真实接口允许如此）。CompleteAsync捕获取消直接false；结果报StaleOperation，但目录留下自己的非空OperationToken和PendingApplyVersionId。两条FAIL。必须区分自己的取消/未知结果与真正被更新操作取代，独立清理/登记原身份，不遗留永久inflight。宿主已成功而本地提交被取消不假称失败或自动重发，保可只读核对的原回执/身份；不能用取消异常处理分支替代这个返回成功分支。
3. **E3-FIXED-CONTENT-1 / P1**：导入固定版本后修改托管index.js正文，Apply仍Success=true且宿主回调收到已换内容，在旧ContentHash记录下执行。一条FAIL。应用/回退前重新核对托管文件摘要、入口/声明/路径，篡改/损坏/链接均须无宿主副作用，不能改旧摘要来制造通过。

## 接线与其他流水线

结构化决定E3-local-review-rework2-2026-10-13.json：rework/not_integrated/mainSha=null，仍原模型 `group/auto-deepseek-v4-1-flash`，L3；没有升两级，不提示换模型。模块未公共接线不是打回理由。公共Pi事实、安全重启、现有扩展页、原生IME/DPI、最终全量门仍M承担。

R1原turn01a1353a仍active/inProgress，冻结4de5151；中途clean-start说明不是最终可接受证据，未读其卡/源、未整合、未重派。WindowsSandboxServer与构建节点未终止，不接管未知进程。E3本轮M不跑UI/全量自测以保原生验证串行；worker无头46仅为其记录，未冒称M复跑。旧15CLI运行无新received未审项。用户demo/继承目录、红日志和工作树保持，main仅记录小步提交。
