# Pi 并发停止与释放必须等待真实进程退出

第54轮，E3终止证据前置中的L4生命周期修复，由M直接改公共Pi传输及必要测试。起点main `fd6808d1c913d123ddfca4b09f73fd91b603b080`，实际产品提交 `eb710d627132a96ea16751b4d7f346ca6036ad23`，三源/测试+记录/缺陷登记，共五允许文件。原R1/E3均notLoaded/completed、游标及真实HEAD保持，无新交付或派发；未知Sandbox PID15712/出生相同，冻结R1安装门仍defer。

源码发现PiRpcClient.StopAsync以Interlocked标志拒绝重复执行，但第二个调用直接成功返回，未等待第一个调用的真实退出/管道收尾；DisposeAsync因此也可能提前释放仍在使用的Process/Writer。这会让“await停止完成”的事实不可靠，必须先用真实本机子进程验证，不能直接生成引擎终止凭证。

范围：PiRpcClient的停止/释放屏障及进程退出事实、Pi独立定向/全量测试，必要App/Core回归及记录。验收：原产品在可控本机Node子进程上并发Stop/Dispose红；各调用等待同一终态；进程确实退出后才能完成，正常/超时强制退出及反复关闭皆可核对真实generation/退出码。原请求失败传播/末尾响应/诊断不回归。solution零警告错误、Pi/Core/App全量与必要真实Pi插件/取消路径通过。仅自有临时子进程，不处理未知Sandbox或用户进程，不请求模型。

此增量不把单个Pi进程退出自动升级为旧插件回调/所有派生进程无未来副作用。原请求持有者与跨重启归属仍待，不能据此解除InFlight或新增Abandon按钮。

## 实际反例与实现

`tmp/m-cycle54-shutdown-red.log`有五FAIL、exit1：自有Node子进程在stdin关闭后确实仍活着，第二个Stop立即完成；混合Dispose提前关闭原生Process句柄，第一个Stop随后抛InvalidOperationException。超时强制退出的第二Stop亦假完成。测试按文件握手等待真实stdin关闭，不把固定sleep或单次IsBusy当进程退出；finally仅释放自己的Node夹具，不触碰未知进程。

PiRpcClient现在让每次Stop返回同一Task、每次Dispose等待同一释放Task。原关闭stdin→有界等待→必要时终止自有进程树→收完管道/事件/等待者的顺序保持；只有该序列完成才能报告停止，Process/Writer只释放一次。重复调用不另开关闭流程，也不吞掉首个失败。

新增不可变PiProcessExit观察：在底层WaitForExitAsync实际结束后，缓存真实RPC generation、PID、OS退出码及观察时间；原生句柄关闭后仍能只读核对ExitCode/ProcessExit。关闭stdin或发起Stop不会产生退出观察。它只证明该RPC子进程退出，尚不关联原插件请求持有者、跨重启的新进程或任意外部后台任务；没有写入释放凭证或解除原保护。

## 本轮验证

| 命令/产物 | 结果 | 证据 |
| --- | --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet` | 0警告0错误、exit0 | `tmp/m-cycle54-solution.log` |
| Pi Release DLL `--shutdown-only` | 初步25项通过；随后增加16并发调用断言并由全量覆盖 | `tmp/m-cycle54-probe.log` |
| Pi Release DLL全量 | 48通过（原21+新26屏障+1真实Pi退出观察）、0失败、exit0 | `tmp/m-cycle54-pi-full.log` |
| Core Release DLL全量 | 359通过、1可选云SKIP、exit0；真实云开关显式0 | `tmp/m-cycle54-core-full.log` |
| App Release DLL `--selftest` | 1329通过、0失败、exit0，未改App断言 | `tmp/m-cycle54-app-full.log` |
| App真实插件/取消入口 | 最终15/27通过、0失败、均exit0 | `tmp/m-cycle54-plugin-real.log`、`tmp/m-cycle54-cancel-real.log` |

26屏障检查覆盖：正常延迟退出、Stop/Dispose混合、超时强制退出；首次/重复Stop共享未完成Task、重复Dispose共享等待；16个线程池Stop/Dispose调用在同一个实际退出前都未完成；旧挂起请求仍为PiRpcException；真实PID/generation/退出码与独立OS句柄一致；多次释放后观察不丢。全量原末尾响应、Unicode/分帧、异常/限额/并发挂起请求和真实Pi隔离查询保持。

首绿工装读取独立Process.GetProcessById.ExitCode时未先持有该进程的OS句柄，Windows拒绝，日志`tmp/m-cycle54-shutdown-green.log`保留；在进程活着时取得独立Handle后才能验证退出码，修工装后全部通过，未改产品预期。原五红发生在此额外检查加入之前，属于产品真实反例，不用工装异常替代。

红反例/修正工装/最终全量三次专属Node根经只读进程核查均无残留（计数0）；仅证明这些夹具退出，不代表未知Sandbox或其他应用进程状态。

Pi/Core/App各运行独立输出，全部构建完成后才并行，不在测试时重建同DLL；同App默认/真实插件/取消入口串行。Node机制夹具显式携带devin/swe-2标识、无模型调用；原实际Pi只做隔离无害查询，App自有proof模型离线，Core可选云开关0。没有昂贵测试lead或云prompt。

## 监督、未验证与下一步

原R1/E3均停止且无新卡，固定候选不含后续插件；未知Sandbox不停止或接管，安装门继续defer。主区可续期独占PID38208，编辑/提交前活检；原demo及所有未跟踪状态不暂存。无新派发/未知会话接管、不push、不改写历史、不清理树、不登录采购部署。

未验证整个应用的真实崩溃/断电恢复、跨重启所有引擎与外部后台任务终止、原插件请求持有者/终止事实的持久关联、系统原生IME/DPI及当前源安装。完整插件安全释放、原生与生产六门仍pending。下一M把真实进程generation的退出观察与原请求/回调生命周期关联，先验证再考虑显式安全释放；旧无证据请求继续保护。结构化integration绑定上述实际产品SHA，只记录本增量，不升级整体验收。
