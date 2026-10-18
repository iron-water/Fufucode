# M：E3 本地插件第一次独立审核

2026-10-13 12:59 UTC监督入口。主仓库起点 `81f3b7fc23890a46528b414d28413bcaff9153f5`。交付线程 `01a1345a-47b6-7460-94d0-ae3218a0dc90`，完成turn `01a1353d-8d92-7373-97b9-84e1a20bdd49`。实际树 `C:/Users/mio/.codex/worktrees/2884/Fufucode`，干净、detached，HEAD `21eebc564e28f2158a268d55a1ab72ffbecd0ec3`。正式idle/completed、writerStopped=true、无相关后台写入进程后才读取完整卡与源码；15文件均在原允许范围内，四提交和起点相符。原卡保存E3-local-delivery-2026-10-13.json。

决定 **rework / not_integrated / mainSha=null**。公共页与宿主未接线本身是有效模块交付，不是打回原因。原L3保持L3、原模型 `group/auto-deepseek-v4-1-flash`，未达到升两级，不换模型或另起执行者。

## M实际验证

- `dotnet run --project tests/Fufucode.App.Plugins.Tests -c Release --no-build`，workdir=原2884树：81项PASS、exit0。使用原交付Release产物，不声称M重新构建或原生验收。
- `pwsh -NoLogo -NoProfile -File tmp/m-e3-review/run.ps1`，workdir=主仓库：编译独立临时Console，引用交付App/Core Release程序集，真实临时文件/独占句柄/异步回调，**五条FAIL，exit1**。未改worker源码或现有断言，不运行模型或原生窗口。
- 源码 `tmp/m-e3-review/Program.cs`，原红日志 `tmp/m-e3-review/results.log`，数据根 `C:/Users/mio/AppData/Local/Temp/fufucode-m-e3-review-25418b59d61b429db21a452951325ee0`。只操作自己临时副本，源文件不删除。

## 四项finding

1. **E3-VERSION-COMPAT-1 / P1**：版本1仅支持Pi1.1.0，版本2仅支持Pi2.0.0。导入2覆盖插件级SupportedHostVersions，使1在正确宿主被拒；切宿主到2.0.0后1又实际进入Apply回调。两条FAIL。兼容声明必须绑定固定版本，应用/回退/列表按所选版本核验。
2. **E3-PENDING-REMOVE-1 / P1**：同插件有1/2，快照未知时明确请求移除1；恢复已知空快照后ResolvePendingAsync删全部版本，包括未请求的2。一条FAIL。pending须记录具体目标和操作身份；只读快照核对入口不得自动删文件，后续删除须明确且限定原目标。
3. **E3-REMOVE-FAILURE-1 / P1**：独占打开版本入口后移除，TryDelete吞IOException，方法返回成功且目录记录丢失，入口文件仍在。一条FAIL。失败必须报告并保留可恢复目录状态，不能把残留版本写为已移除；部分删除/状态写失败也须保真实诊断与所有权。
4. **E3-STALE-APPLY-1 / P1**：Apply1回调等待时，同服务Apply2成功；释放旧1成功回调后SelectedVersionId退回1。一条FAIL。OS短锁不覆盖跨await操作身份；副作用前和完成时核验同插件操作/版本/generation，或明确串行拒绝竞争操作。旧结果不能清新pending、覆盖选定/回退目标，移除不能删正在应用文件。

## 未验及下一步

R1仍active/inProgress、冻结4de5151，在串行核查包内按键/焦点不稳定现象；不是正式交付或M确认根因。本轮不跑UI/全量App、不读运行中卡，不重复启动或改包。旧15个CLI运行无新delivery.received未决定项，旧调度未运行，不恢复。

E3原线程原模型原树返工，保原提交、红日志、继承状态。原81、worker无头36及全量重跑绿不能盖掉新红。worker首次focused=False观察登记，但根因未确认，不能直接称非模块引入或只是假抖动。修后M忠实五项及邻接边界复核再定收编，实际Pi加载/外壳/IME-DPI另验。没有push、改历史、删树、登录采购或产品接线提交。

协调实际发送成功：E3同线程已新turn `01a13560-5b88-76e2-be8c-81369f30fa42` active/inProgress，不重复启动。R1收到稳定性证据保真要求：保每次失败/退出/来源，不能重跑挑绿或未证实排除产品/工装根因；可独立包装项继续，最终不稳定门须如实未通过。R1两绿三红为worker中途观察，不是M正式审核或已确认根因。
