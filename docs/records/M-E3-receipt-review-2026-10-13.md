# M：E3第二修复轮与回执身份

用户明确继续轮，main起点b644a3f。原线程01a1345a turn01a1359a completed/notLoaded，卡writerStopped=true；真实2884树干净HEAD `ff6bab6a1eeabd838820cbd98c91ab1d4b47fbc2`，无相关writer后读卡与源码。原允许15文件内第二delta7文件，245bba9/ff6bab6两提交；未收编/公共接线。

M实际重建独立工程Release0警告0错误，服务156 PASS，日志worker tmp/m-review42-build.log与m-review42-services.log（上一轮同名日志已由本轮重建更新，不据它称上轮125证据仍存在；各次命令输出有记录）。首轮5与第二轮4忠实断言全绿，主区原红results日志保留。并行启动反例时Release构建尚未终止这一调度顺序不作为新源码首次编译证明；随后确认构建exit0和源未变，实际被引用产物对应本轮源。没有UI/模型请求。

新增不可变回执身份反例：`pwsh -NoLogo -NoProfile -File tmp/m-e3-receipt/run.ps1`，主区workdir，exit1，**一FAIL**。Apply1宿主回调等待，Apply2成功；旧1返回失败后，LastApplyReceipt用了新版本2的VersionId配旧1的Token和“actual failure of version 1”。selected保护已通过，不等于回执身份准确。日志/源码同目录，数据根 `C:/Users/mio/AppData/Local/Temp/fufucode-m-e3-receipt-1b295e3e00a54b64921157e713c0d235`。

源码CompleteAsync的VersionId来自existing.PendingApplyVersionId或existing.LastApplyReceipt，丢失原请求版本。必须用原token对应的InFlight候选/不可变请求身份。允许迟到结果入原身份历史，或保留最新请求的回执而不冒充旧结果已核对；不能把版本1失败归版本2、不能凭last receipt认定另一原请求结果。未知/取消/抛异常及回收入口同样身份准确，不丢已确认新回执。

决定E3-local-review-rework3：rework/not_integrated，原L3/deepseek线程同树修这一个具体问题，原九断言不得放宽；共享入口仍M所有，不因未接线打回。R1停写保候选，M同时实际复现C5只读RefreshAsync清按钮焦点（修正滚动到可见的前置后，焦点先true再false），公共焦点问题正在主区修，不停止流水线。
