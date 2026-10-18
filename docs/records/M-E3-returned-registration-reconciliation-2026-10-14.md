# E3 已返回请求登记的有限对账

第56轮，M直接实施E3-M-HOST L4公共宿主增量，起点main `c4c7be6afb64f777644c5491f8dc6eb25ac834a9`。范围见 `docs/tasks/hourly-E3-returned-request-reconciliation.md`。实际产品SHA `bddf39f6e601886c2d7389045223f1a510b1f2e6`，十一源/测试和任务/记录/失败登记，共十四允许文件。本记录只覆盖明确结束已返回登记；E3整项与生产六门仍待验。

## 交付行为

真实Pi宿主调用已经返回，但目录被其他写入者持锁而导致完成提交失败时，原插件页现在可以先核对原请求，再明确确认“仅结束已返回登记，保留文件与原回执”。只结束选中的一个token，另存有界对账历史。不删除文件、不重发、不认证原应用成功、不认证任意外部任务终止，不合成ApplyReceipt，不覆盖新选定/上一版本/新请求回执或pending。

结束登记须同时满足：独立原返回为returned_success/returned_failure；结构化原project/session/generation及原RPC已观察退出匹配；原请求的真实OS持有锁已释放；当前为同项目/session/精确Pi身份的新连接；原候选完整且未被当前宿主加载；实际宿主取得应用排他门与Core空闲维护；提交前重核原登记/返回/副本/宿主并通过目录generation CAS。未知、取消、抛错、旧无结构化身份/持有协议记录、仍加载、忙、关闭、异会话、损坏或过期均拒绝且保留原登记。

原调用从token产生、目录接受开始直到完整方法返回/异常退出始终持有同token OS锁。回调Task返回后仍可能有本地完成写入者，不能只凭返回日志释放；对账也取得同一个锁，跨实例可拒绝活跃原调用。适配器租约持实际_applyGate及BeginIdleMaintenance，期间真实Pi新prompt/重启/重复对账不可执行。对账临界区只写目录元数据，不调用引擎、不等待模型。

schema1新增可选结构化身份/RequestLeaseVersion和独立对账历史。旧记录照常只读展示，缺字段不伪造、不迁移、不自动解锁。原返回JSON并非认证签名。该动作延续正常回调完成后的本应用登记语义，不宣称任意插件启动的外部进程已经结束；崩溃时没有可核对返回/退出的旧unknown仍保护。

UI默认未确认；换请求重置确认和核对代次，忙时禁用重复动作。关页取消尚未提交的对账。已明确授权的旧动作迟到时只对原token记账，不能把新选中请求当目标、不能覆盖其页面结果。正常完成后刷新，独立历史仍可离线重开查看；原应用回执不改名为“对账成功”。设置/辅助栏/工作台基线不变。

## 红例、工装与验证

`tmp/m-cycle56-red.log`真实Pi原源两个FAIL：原返回缺结构化project/session/generation；共享VM无明确登记对账入口。原19真实插件断言保持通过。先确认缺口，再实现，没有放宽旧断言。

47条新机制/编译页面检查通过：真实目录锁造成残留，原完成方法尚活时不能释放；九类旧/未知/错配证据拒绝且保字节；仍加载目录/异项目/session/身份/代次/忙/关闭/篡改/持锁/取消/CAS；选择变化、重复、关闭、迟到；单token结束、新请求/unknown回执/pending和版本指针保真；returned_failure只清自有登记、独立历史离线保真。注入PID123、fake host/租约仅作规则夹具，非真实Pi/跨进程崩溃证明。

实际Pi共享外壳34条通过（原19+本轮15）：真实安全重启后置catalog OS锁阻断完成，原返回/退出存在且原登记保留；候选仍加载时拒绝且目录原字节不变；用户明确安全应用旧版后，原登记单次结束，当前coordinator/generation/session/草稿、固定文件、原返回及新回执均保持；实际Core维护期间prompt在本地拒绝，没有发云请求。重复对账/重启被实际适配器门挡住，错误scope拒绝后门正常释放。

首次像素工装两FAIL在 `tmp/m-cycle56-final-real.log`：安全重启已把外壳返回Chat，工装仍拿离树插件控件测可见性。M查看两帧确实是Chat。修工装为通过当前真实VM导航扩展页、确认实际挂树后再按原几何断言测量；未放宽可见性断言、未据此假报产品布局缺陷。修后1100×800/800×640两帧均见明确登记动作，按钮完整可达。

M实际查看：

- `C:/Users/mio/AppData/Local/Temp/fufucode-plugin-shell-f18c471931d24d989dc543902a24cd60/reconciliation-1100x800.png`
- `C:/Users/mio/AppData/Local/Temp/fufucode-plugin-shell-f18c471931d24d989dc543902a24cd60/reconciliation-800x640.png`

两帧来自真实Pi数据/无头编译外壳，仍不是原生指针、键盘或系统IME/DPI证据。

| 实际命令/入口 | 结果 | 原日志 |
| --- | --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet` | 最终0警告0错误、exit0 | `tmp/m-cycle56-build-visible.log` |
| App DLL `--selftest --plugin-reconciliation-only` | 新47 PASS/0 FAIL、exit0 | `tmp/m-cycle56-final-reconciliation.log` |
| App DLL `--selftest --local-plugins-real-only` | 34 PASS/0 FAIL、exit0 | `tmp/m-cycle56-real-visible.log` |
| App DLL `--selftest --local-plugins-cancel-real-only` | 27 PASS/0 FAIL、exit0 | `tmp/m-cycle56-cancel-real.log` |
| Core/Pi Release DLL全量 | 366 PASS+1可选云SKIP / 48 PASS，exit0 | `tmp/m-cycle56-core-full.log`、`tmp/m-cycle56-pi-full.log` |
| 插件独立DLL默认/`--ui` | 166服务+21proof / 46无头UI PASS，exit0 | `tmp/m-cycle56-final-services.log`、`tmp/m-cycle56-ui.log` |
| App DLL `--selftest` | 1388 PASS/0 FAIL、exit0 | `tmp/m-cycle56-app-full.log` |

命令均在真实主区显式workdir。所有机制和真实Pi只用临时数据/自有插件，模型标识proof/devin/swe-2，未调用云模型、未派贵测试lead。首条red启动命令误用net8.0不存在的DLL，原工具输出在本轮会话保留；该临时red.log随后用于真正原源两红，不把启动错误冒充磁盘红例。核实csproj/net10.0真实产物后原源两红实际跑出，此启动错误不是产品缺陷。核心源码未改，Core/Pi全量依旧运行并通过；不同DLL必要验证并行，同App入口串行。最终本轮真实插件红/绿及取消专属四根只读核对Node残留0，不推广为全机器清理结果。

## 监督、限制与下一步

R1仍idle/latestTurn同一01a1368a completed、原HEAD4097328；E3仍notLoaded/latestTurn同一01a13612 completed、原HEAD8c67289。wait游标重置恢复到7f0ba842:13/5c7b503d:2只重送旧确认卡，不当新交付或重新审核。R1旧defer/not_integrated与E3历史模块pending_wiring回复不覆盖M后续实际集成记录。未投递、重建或接管未知会话。

未知WindowsSandboxServer PID15712/出生保持，冻结cc44eee候选的安装门继续defer；没有停止或接管。仅本轮已识别临时Pi节点作只读残留核对。原demo脏改及全部未跟踪保护，main续期独占PID35208编辑/提交前活检，不push/改写历史/移除树/登录采购部署。

未验：真实异常持有者跨进程崩溃、断电/迁移/长期压力、取消/异常无返回记录的有限释放、插件任意派生外部任务；系统原生指针/键盘/IME/DPI、当前源码安装和完整E3/生产六门。不能把“原登记结束”扩展成“插件全部任务结束”或“生产验收”。下一步从正常已返回残留的闭环转向缺证据unknown恢复契约及原生/当前候选门；旧无证据不自动丢弃，R1仍只在槽可证明空闲后有限续验原固定setup。
