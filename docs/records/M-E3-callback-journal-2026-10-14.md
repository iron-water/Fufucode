# E3 宿主回调返回与目录提交失败的事实分离

第53轮，E3-M-HOST L4增量，M直接实现共享插件生命周期；起点main `83cb86de90b5b380b813c5fd93a923c57dd4c50e`，实际产品提交 `02ba53c8755f6ff4a571736b8887e4f7f348c6bd`，七源/自测+记录/缺陷登记，共九允许文件。先完成本轮运行监督：原R1/E3均notLoaded/completed、游标/真实HEAD未变，无新卡、不重派。未知Sandbox PID15712/出生不变，冻结安装门继续defer，所有继承文件保留。

本轮在原回调已经返回、但真实目录独占锁使CompleteAsync提交失败的接缝核对产品。当前持久登记不能区分“尚未收到宿主返回”与“返回已收到但目录没提交”。目标为新增绑定原token/version/登记时间/原宿主上下文的独立原子返回记录，并在原只读核对区显示；不合成目录成功回执、不修改Selected/pending/原回执、不重发或解除保护。旧记录/无返回文件继续未知，当前NotLoaded仍不授权安全释放。

允许路径：Plugins服务/新的返回记录存储/只读观察/VM、相关App定向自测与注册、本记录/缺陷登记/进度/台账/决策。宿主与Core/Pi协议保持；不改原布局基线或冻结R1。先做原产品独立红反例，再验证实际锁/回调成功与失败/取消/新服务重开、身份错配/未来损坏/原字节不变/不覆旧文件、编译绑定；最终solution零警告错误、App全量零失败及必要模块/真实Pi回归。所有测试用临时数据；无模型请求。

回调Task返回/抛出是可记录事实，不能单独证明引擎未加载、后台无副作用或旧进程终止。完整安全释放仍pending；本轮不把本地JSON当进程终止凭证或认证边界。

## 独立红反例与实现

`tmp/m-cycle53-journal-red.log`一FAIL：夹具实际返回成功前取得原目录OS独占锁；宿主调用次数1，目录CompleteAsync确实失败，原InFlight/OperationToken保持且无目录回执；原产品未留下可重开核对的返回事实。未放宽原锁门或让夹具替换目录写入。

新PluginCallbackJournal在实际宿主Task返回成功/失败或抛取消/异常后、目录CompleteAsync之前，原子发布不可覆盖的返回记录。路径用完整原token的SHA256，不拼不可信token进文件名；记录保原plugin/token/version/登记时间和请求前的宿主身份/上下文，返回结果与时间另记。写入临时文件flush-to-disk后File.Move禁止覆盖；重复/无法写入拒绝，不认证成功或偷偷释放保护。取消不取消本地返回事实记账，原cancelled_unknown契约仍保留。

原目录schema、选定/pending/回执提交规则不变；返回记录独立存于原插件根 `.callback-results/`。只读观察按原token/version/登记时间核对；未来格式/缺字段/损坏/错配拒绝且保文件。缺记录只显示无法核对，不据此断言回调仍活跃或已经终止。当前加载与原返回两部分显示，在实际原插件区增加说明，不另建窗口/布局或按钮。记录不能证明后台/外部副作用已终止，不能当可信签名或授权凭证。

## 实跑验证

| 验证 | 结果 | 证据 |
| --- | --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet` | 最终0警告0错误、exit0 | `tmp/m-cycle53-clean-build.log` |
| App Release DLL `--selftest --local-plugins-only` | 126通过（原102+新24），0失败、exit0 | `tmp/m-cycle53-final.log` |
| App Release DLL `--selftest --local-plugins-real-only` | 15真实Pi外壳检查（原14+原返回绑定1），0失败、exit0 | `tmp/m-cycle53-shell-real.log` |
| App Release DLL `--selftest` | 1329通过、0失败、exit0 | `tmp/m-cycle53-app-full.log` |
| 插件独立Release DLL默认 / `--ui` | 原服务166/proof规则21 / UI46通过、exit0 | `tmp/m-cycle53-services.log`、`tmp/m-cycle53-ui.log` |

同App的构建/定向/真实Pi/全量顺序执行，未运行中重建DLL。最后只修真实测试nullable检查 `id is not null`，不改24机制或布局断言；保 `tmp/m-cycle53-final-build.log`首1条CS8604 warning，实际修后clean-build零警告，不抑制。Core/Pi协议未改，不机械重跑旧全量，旧359/21不是本轮新证据。

24项新检查涵盖真实目录锁导致提交失败但返回事实可重开、原身份/上下文保真、离线未知与已返回分开、目录及返回文件字节不改、不可覆盖及临时文件清理、八种未来/错配/缺字段、损坏/缺记录、实际返回失败/抛取消/异常、记录发布失败保护保持，以及真实编译绑定/两尺寸稳定可视区域。夹具仅测试状态与真实文件锁，不冒称真实Pi成功；15真实Pi路径另证明真实初始化后返回记录的原请求/原session绑定。

M查看 `C:/Users/mio/AppData/Local/Temp/fufucode-plugin-callback-c7bed637c2c64013bdba3c8d547125d1/callback-1100x800.png` 与同目录 `callback-800x640.png`，原返回说明/上下文及“未证明引擎终止/安全释放”完整可见。最后nullable修正不改布局，此两图对应最终布局及同24断言。无头像素不是原生输入证据。

## 未验证、监督与下一步

未做实际进程崩溃/断电耐久、跨机器可信认证、磁盘耗尽、长期记录保留策略、系统原生IME/DPI、生产迁移及包含当前源的新安装。返回与目录提交之间的窗口现可观测，但仍没有持有者/宿主终止证据；本轮不接Abandon/重发/删除入口，旧无记录的请求保持保护。下一M先补绑定原token的持有者生命周期证据；不把已返回Task或NotLoaded升级为“无未来副作用”。整项插件与生产六门仍pending。

原R1/E3原卡未重审/重派/恢复；冻结R1不含后续插件，未知Sandbox保留，只有验证槽可证明空闲才有限续验原setup。M续期独占PID39768，编辑/提交前活检；原demo/全部未跟踪状态不暂存。无云prompt/昂贵验证lead、不push、不改写历史、不清理树、不登录采购部署。产品SHA与结构化integration已绑定真实源，文档补录不升级整体验收。
