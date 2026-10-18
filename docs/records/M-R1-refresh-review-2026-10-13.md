# M：R1当前候选停写交付审核

2026-10-13 13:55 UTC监督周期内正式收卡。主仓库起点7599cc2，工作树 `C:/Users/mio/.codex/worktrees/cb9f/Fufucode`。交付turn `01a1353a-56c5-7fa0-840c-2682ade62f10`已completed；后继人类说明turn `01a13596-8ee9-7580-8181-ba2330dbd63a`也completed/idle，不是新的实现交付。正式JSON从前一turn提取，实际HEAD `a99693ce87e3fce40a9d8fa5f0b95f1890c4ab5f`，四提交及10允许文件真实相符。runtime/node/LICENSE未提交状态保护，不刷新/还原/夹带提交；无该候选运行中的打包/App/dotnet进程后读完整记录/代码。

决定 **defer / not_integrated / mainSha=null**。保留有价值的两套候选与包装修复，候选产物成立，但全R1任务的包内自测稳定门未通过。产品/公共工装根因由M修，不把禁止src的worker打回要求越权，也不要求用户作普通发布选择。源码冻结4de5151未改；src树实际与交付树均 `8d84b690c1cc3562daa98fc166b6e5c315152bd6`。记录提交本身的SHA不等于包内嵌源码身份，两套包分别d7d452f/b405af0，卡已区分。

## M实际核验

- `pwsh -NoLogo -NoProfile -File tmp/m-r1-review.ps1`，主仓库workdir，exit0。六件产物尺寸/SHA256逐项匹配卡；两ZIP都逐条流式核验**15542**文件摘要、没有重复/missing/extra，ZIP内manifest与对应build manifest字节一致。不是只看worker绿日志。源码tmp/m-r1-review.py，日志tmp/m-r1-review.log。
- 两Sandbox证据目录results.json均12/12 true，complete.txt=PASS；第二目录sandbox-info真实WDAGUtilityAccount/Virtual Machine。M读取了实际安装/启动/卸载证据，**未重新跑Sandbox**，不写M重验安装通过。源证据路径保留在正式卡。
- `pwsh -NoLogo -NoProfile -File tmp/m-r1-parse.ps1`：五改动PS脚本AST0错误、冻结src树相等，exit0。已读package/build-installer-version/integrity差异，独立输出保护、清单字节稳定与脚本退出码修复无公共产品改动；未运行整套publish或覆盖继承工件。
- 候选r1-e2e ZIP SHA `B2D20A49B34AA15F87B5ED653BCE43F4FC504C62D2DEC8474FEA8FB4E4CA06B7`，setup SHA `A7694956D310259AAF920A6E5C30068AC18B9230C3DC33A1BCD1053C230824B1`，实际191736776/141147158字节。当前工件仍在worker独立out，未迁入主区或公开发布。

## 未通过与部署边界

1. **R1-PACKAGED-SELFTEST-STABILITY-1 / P1 open**：worker八次包内全量3绿5红，失败来自实际按键步骤、busy=False/enabled=True/focused=False后的命令启动超时。记录/卡和三报告均保留失败，M读取源码确认Focus后马上KeyPress且只观察ExecutionTask变化；这仅是定位线索，未证实原因。包内门保持未通过，不能挑一绿或改断言造通过。源码Debug两绿及Core340/Pi21等均是worker本轮记录，M未再全量跑一遍。
2. **R1-HOST-PAYLOAD-1 / P1 open**：M按两ZIP清单实际确认没有HarnessHost、Fufucode.Hosting、Fufucode.Control载荷；包装只publish App/Cli。候选不能证明自动工作台后台执行闭环在无开发目录机器可用。已有桌面布局显示/包内合成自测不能替代宿主部署；公共宿主启动发现/发布清单接线由M补，不擅自扩大原worker范围。
3. OMP仍未托管，候选干净机器不具备完整双后端；与宿主部署一并收口。真实云聊天、WebView2、原生IME-DPI、多显示器、跨进程GUI、升级/重装/数据迁移仍未验。Inno编译器Non-commercial use only为卡中的环境观察，不推断许可法律结论或发起采购。
4. Control首跑ownership异常、后独占511通过两事实保留，不能合并为稳定全绿；既有PID复用/进程归属根因继续open。本周期没有新模型/昂贵测试lead，没有重复读用户未知会话或处理其Sandbox。

## 下一步

M先定位包内Release/clean PATH下焦点/输入驱动失败，保真实红/绿和命令启动语义，再补宿主部署/OMP交付的适用门。R1原会话保树停写，明确修复源和范围后才同线程有限重验，不继续挑绿或要求原worker改禁止的src。本次defer是M公共修复待办，非登录/付费阻塞，也不停止E3原树返工。E3仍原L3/deepseek新turn01a1359a进行第二修复轮，公共插件接线尚未实施。最终六门与整个产品范围没有验收。
