# M：Aero 公共接线（第69轮）

2026-10-14。主区起点 `6f8d5c066063123e905f148be880104fd25c75fb`。M持有cycle69独占main写入租约，原生桌面仍仅由原NATIVE R4执行者操作。AERO原线程completed/idle、HEAD `bdf8540eb17889ea156eb602c038aa27f189c61c`、工作区干净，与已接受修订2一致；本轮不重新审核同一交付或唤醒原实现者。

实际公共集成SHA：`8ddbeac77d922a16aea4c75232f9a026bcf45c7b`（28文件，仅本次范围）；事件/审核/集成绑定见同名JSON。原accept审核保持历史原值，新集成记录与当前dispatch单独保存真实mainSHA，不伪造原审核时已接线。

## 改动与验收边界

- 从上述真实HEAD逐字导入3个Aero源文件、3个资源文件、6个测试源/工程文件；不导入worker生成图、不合并历史文档或继承用户文件。接线由M增量实现。
- `aero`为独立第四皮肤键，设置选项从目录同源生成；主窗、既有浮动窗和新确认框持有各自资源/标题栏适配器，切回Fluent精确释放。同步最大化/还原/激活状态；辅助栏与对话框保32DIP标题区，主窗保原48DIP。
- Aero使用教师渐变/窗口按钮主题，语义图标、头像和终端选区有自身度量/颜色。玻璃质感用实色渐变呈现，不宣称系统DWM模糊。默认Fluent、原设置/辅助栏结构、极扁状态条默认关闭均保留。
- 本轮验证公共接线、持久化、主题/状态切换、释放与无头实际绘制；不代替Aero原生输入、DPI/IME、全部页面品味验收或新安装包验收。

## 实际验证

| 命令/证据 | 实际结果 |
|---|---|
| `dotnet build src/Fufucode.App/Fufucode.App.csproj -v quiet -o tmp/m-cycle69-final` / `tmp/m-cycle69-final-build.log` | 0警告、0错误 |
| `dotnet build Fufucode.sln -v quiet` / `tmp/m-cycle69-solution-final.log` | 串行重跑0警告、0错误 |
| `dotnet tmp/m-cycle69-final/Fufucode.App.dll --selftest --skins-integration-only` / `tmp/m-cycle69-skins-final2.log` | 91 ok、0失败，含17个新增公共接线检查 |
| `dotnet run --project tests/Fufucode.App.Aero.Tests -v quiet` / `tmp/m-cycle69-aero-module.log` | 169断言通过 |
| `dotnet run --project tests/Fufucode.App.Aero.Tests/RealCaptionProbe -v quiet` / `tmp/m-cycle69-caption-probe.log` | passed43、failed0 |
| `dotnet run --project tests/Fufucode.App.Skins.Tests -v quiet` / `tmp/m-cycle69-engine-final.log` | 121断言通过 |
| `dotnet tmp/m-cycle69-final/Fufucode.App.dll --selftest` / `tmp/m-cycle69-full-final.log`、`.exit` | 1480 ok、0失败，exit0；初稿失败未被覆盖 |

M实际核看 `C:/Users/mio/AppData/Local/Temp/fufucode-skins-integration-cc7ee7952b7c465588f2c0e26df1fcca/main-aero-light-1100x800.png` 与同目录 `main-aero-dark-1100x800.png`。看到实际主窗、聊天、辅助栏、浏览器、终端与Aero三键；截图不等于真人操作或全部细节终验。

独立引擎本轮新生成的6张帧另保留 `tmp/m-cycle69-engine-evidence/`；测试前为干净的仓库旧参考图已按明确6路径恢复，不覆盖任何继承用户改动。Aero工装本轮生成帧保留在其evidence目录，提交只选择源与工程文件。

最终App DLL SHA256：`7F2FBDF2A05251FB0CA24FE5495F5D04314AD29071A748010C01529554241E3F`。Core/Pi源码本轮未改、全量未重跑。导入源码/工程的定向路径检查发现Aero测试日志一处旧worker绝对路径，已改运行时根目录；不把历史记录里的工作树身份当运行时路径。

## 失败与判断

1. 初始main接线扫描全部chromeBar误把无窗口按钮的状态条当caption，真实初稿全量8个Aero失败；只扫描包含实际CaptionAssetButton的标题区，后续公共91全绿。原日志 `tmp/m-cycle69-skins.log` 留存，不宣称worker模块缺陷。
2. 定向第一次在虚拟化视口外查找用户头像而抛异常（`tmp/m-cycle69-skins-final.log`）。工装通过真实TimelineScroller滚到顶部，再检查当前实例的圆角及原消息VM同一性；不造控制件、不删断言。
3. 独立引擎旧断言把`aero`当未知键/目录限定三项，现真实第四项使旧假设过时；改为明确未安装键保原未知回退要求，目录精确四个键，另验aero解析。Metro hover旧检查外层Background，诊断 `tmp/m-cycle69-engine-diag.log` 实际pointer=True、root=#2D89EF、绘制ContentPresenter=#1E6FC4；改检查真实命中状态和唯一PART_ContentPresenter的原精确颜色，离开也查原精确颜色。不放宽成任一层/近似色通过。
4. M曾让两个引用App的构建并发，产生MSB3061/3026同一obj文件占用。已结束自身构建并改为串行，原 `tmp/m-cycle69-solution.log` 保留。未杀未知进程、无警告降级或删除用户目录。
5. 首次误用不支持的`--skins-only`，实际入口回退跑了全量，9失败=上述8个Aero+已登记R05 Redo一次拒绝访问；不能称定向验证。原退出1/日志保留。Redo目录文件后验为Archive而非只读，不能证明当时为何拒绝；未修改存储策略或宣布修复。

## 下一步操作卡

- 原NATIVE R4仍验冻结e3cc78a/固定hash，只查状态、不注入；完成/停写/实际HEAD一致后审核其交付，不能用旧源证据验本轮Aero。
- Aero本轮公共接线完成且必要门通过后记录真实集成SHA；后续原生Aero另固定源/产物，并等待桌面串行槽释放。
- Redo拒绝访问、旧Dispatcher、C5 Enter间歇观察继续保留open；以明确反例/原生新证据处理，不被本轮绿色覆盖。
- 新生产候选安装与完整六门仍待；不push、不改写历史、不删除worktree。继承demo-verify与所有既有未跟踪内容不暂存。
