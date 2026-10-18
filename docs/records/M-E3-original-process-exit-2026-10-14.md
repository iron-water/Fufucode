# E3 原请求绑定的已观察进程退出事实

第55轮，E3-M-HOST L4公共生命周期增量，由M直接实现。起点main `fa35f180ef9d59322462009c50e36938e43dc402`，实际产品提交 `e5db4c94dfc30b38ff31ec29ffc47765001d586d`，十源/测试+记录/缺陷登记，共十二允许文件。原E3停写notLoaded；R1从notLoaded变idle、cursor升:13但latestTurn仍同一completed旧确认卡，非新交付，不重审或恢复。两个真实worker HEAD不变，未知Sandbox PID15712/出生保持，冻结安装门defer。

目标：把上轮PiRpcClient实际退出事实传到Core关闭后，再由明确安全重启捕获的原coordinator绑定到原请求返回记录。仅记“请求前那条RPC连接确实退出”的generation/PID/退出码，不把新连接或另一个项目的退出写到原请求，不据此认证所有插件副作用或持有者结束、不解除保护/重发。

允许路径：Core coordinator退出观察、PiLocalPluginHost/Plugins契约和返回记录/VM、Core及App定向自测、记录/缺陷登记/进度/台账/决策。保持Pi传输既有屏障、目录schema兼容、原回执/选定/辅助栏/设置基线和R1冻结源。前置验收：真实Pi安全重启原返回记录缺退出关联的红反例；关闭/重开代次不串、历史记录缺省/错配/损坏不误认；真实失败恢复仍绑定原连接；solution零警告错误、Core/Pi/App全量及真实插件/取消回归。只临时数据，无模型调用。

此步只落地正常可观察关闭的请求级进程事实。异常退出持有者寿命/跨进程恢复、任意插件启动的后台进程、回调持久终态与安全释放仍另验。

## 反例与实际实现

`tmp/m-cycle55-exit-red.log`一FAIL、exit1：实际Pi安全应用/更新/失败恢复/回退全部原断言通过，原返回记录已有原token/session上下文，却没有原RPC退出关联。本轮新断言先于产品改动，未假装进程未退出；缺口是已观察的退出事实在Core清空client后无法沿原请求持久核对。

Core在真实client.DisposeAsync完成后缓存LastProcessExit；之后Close清空live generation不抹退出观察。重新Start保原历史退出，但它的generation不能冒充新连接已退出。PiLocalPluginHost安全重启前固定原coordinator引用，回调返回时只取该对象的实际LastProcessExit，并匹配请求前snapshot的结构化Generation。没有按可变current session或PID外查猜退出归属。

原HostSnapshot/HostApplyResult与返回记录增加可选generation/OriginalProcessExit。服务再次核对generation后才收录；缺generation、错误代次不采纳。普通历史schema1记录缺这两个字段仍只读兼容，不合成退出、不迁移改写。含退出字段却缺/错原generation、PID无效或观察时间缺失的文件拒绝且保字节。原目录schema/回执/选定/pending与受保护语义未变。

原插件页只读结果增加原PID、退出码、原代次，明确“仅此进程退出；原请求保护保持”。回调抛取消/异常没有可核对返回结果时仍不冒填退出；该路径保持unknown。当前新连接继续加载插件，不能因为旧连接退出就删除候选或解除保护；失败候选恢复时同样只记录请求前那条连接。

## 本轮验证

| 命令/产物 | 结果 | 证据 |
| --- | --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet` | 最终0警告0错误、exit0 | `tmp/m-cycle55-final-build.log` |
| Core Release DLL `--process-exit-only` | 新7通过、exit0 | `tmp/m-cycle55-core-exit.log` |
| App Release DLL `--selftest --local-plugins-only` | 最终138通过（原102+返回记录36，其中本轮新增12），0失败、exit0 | `tmp/m-cycle55-final-local.log` |
| Core/Pi Release DLL全量 | Core366通过+1可选云SKIP/Pi48通过，均exit0 | `tmp/m-cycle55-core-full.log`、`tmp/m-cycle55-pi-full.log` |
| App Release DLL `--selftest` | 1341通过、0失败、exit0 | `tmp/m-cycle55-app-full.log` |
| App真实插件/取消入口 | 最终19/27通过、0失败、均exit0；此前同产品逻辑19真实Pi日志另保留 | `tmp/m-cycle55-final-real.log`、`tmp/m-cycle55-cancel-real.log`，初轮`tmp/m-cycle55-real.log` |
| 插件独立Release DLL默认/`--ui` | 原服务166/proof21/UI46通过、exit0 | `tmp/m-cycle55-services.log`、`tmp/m-cycle55-ui.log` |

7新Core检查覆盖未启动/活动无伪退出、Close后保实际generation/PID/时间、重复Close保历史、重新Start的新代次不冒用旧退出、查询仍可用与二次Close按自己代次记退出。App新增12检查覆盖旧schema字段缺省保真、匹配/错代次/缺generation、四种损坏退出事实拒绝保字节、实际编译绑定及两尺寸稳定可视边界。注入PID123的机制夹具不冒称真实进程退出；真实Pi新增4检查分别验证原退出存在/原context绑定、二次重启不串代次不覆原记录、坏候选恢复后记录自己的原退出而恢复连接仍活。

M实际查看 `C:/Users/mio/AppData/Local/Temp/fufucode-plugin-callback-5570d50f3ba24798a70ed36d67f2f455/original-exit-1100x800.png` 及同目录 `original-exit-800x640.png`，原PID/代次与保护说明完整可见。它们是无头编译页面的夹具像素，不是原生键鼠或真实PID证据；实际Pi关联由19真实检查另证。

初次build的测试nullable1条CS8602保在 `tmp/m-cycle55-build.log`，按真实非null分支修工装后最终零警告错误，没有抑制。全部构建完成后并行独立Core/Pi/App/模块DLL，同App默认/真实/取消串行，不在其运行中重建。Core可选真实云开关显式0；所有机制和真实Pi查询/自有proof不发云prompt，不启动昂贵测试lead。

最终真实插件、取消及Pi独立屏障三专属根经只读进程核查Node残留0；不据此推断未知Sandbox或整个机器的进程归属。

## 未验证、监督与下一步

尚未记录异常退出持有者的生存期、失败初始化/取消抛出链中的全部新旧引擎退出、任意插件派生后台进程或外部副作用终止；普通JSON不是认证凭证。也未做断电/长期压力/迁移、系统原生输入/IME/DPI及当前源安装。原请求存在旧RPC退出事实仍不自动解锁Abandon/删除/重发，插件整项及生产六门pending。

下一M按实际回调持有者、当前活动候选与已关闭连接集合收敛恢复条件，再设计明确且有限的恢复动作；不把“所有任意外部副作用均已结束”写成已证明。旧无证据登记继续保护，单项阻塞不阻止独立工作。R1原冻结候选不含后续插件，只有原生槽可证明空闲才有限续验同setup，不追main重建或停止未知Sandbox。

main可续期独占PID40140，编辑/提交前活检；原demo/全部未跟踪未暂存。原R1旧确认卡未重审、未投递，:13只是UI变idle后的监督版本；E3仍原停写，无新任务/未知会话接管。无push、历史改写、树清理、登录采购部署。结构化integration绑定真实产品SHA，仅限定此事实关联增量，不升级整体验收。
