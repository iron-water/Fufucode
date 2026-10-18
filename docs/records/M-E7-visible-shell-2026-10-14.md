# M：可见皮肤、页签交互与说明层级

2026-10-14 用户要求皮肤覆盖全部可见元素，修交互、收起说明。起点 main `6e3f03aabe1cc1126065b3de1666de9426b009c3`，M 单写；继承 demo-verify-results.json、用户意见和未跟踪目录保持原样。

## 已实现

- 自定义页签与 caption 显式继承 Button 样式入口。关闭符号 10 DIP、独立 24 DIP 热区；标题与按钮居中；不可见关闭按钮不接受误点；固定页签禁止关闭。
- 辅助栏与浏览器页签接受普通滚轮横向浏览，隐藏侵占栏高的滚动条。切换显示整个页签；普通刷新不跳回选中项。浏览器迟到的显示请求核对当前页签。
- 页签右键按实际命中行路由，页级菜单与空白栏头整栏菜单分开。M 真实输入反例先 FAIL，未放宽断言。
- Metro/M3 原创完整语义图标集、矢量窗口按钮，跨窗口动态换肤与恢复 Fluent。设置导航在当前窗口解析，统一单色；Fluent 保留原分类颜色与位图。品牌图标/插画保留。
- 主窗/浮动窗标题与表面、可选状态条、辅助栏/分隔线、工具按钮、菜单、提示、输入与卡片圆角覆盖皮肤；可选皮肤关闭 Mica。浮动栏自定义 caption 可拖动、最小化、最大化/还原、关闭隐藏。
- 终端默认底色/字色/选择色使用皮肤资源；显式 ANSI 色继续按终端协议显示。
- 设置解释默认收起，通过可聚焦帮助按钮展开/收起并保留 HelpText；全宽设置行保持右侧对齐。资料库、快捷键、存储、插件、用量长说明按需展开，去掉重复导入说明。危险提醒、确认、错误和即时状态仍显示。
- 后端选择、设置重置/丢弃、信任、会话命名、分支、凭据移除、终端关闭等 owned dialog 继承皮肤和 caption；原结果/取消/命令保留。
- 最底部状态条默认关闭；原设置布局与辅助栏结构基线保留。

## 验证

| 实际命令 | 结果 | 证据 |
|---|---|---|
| `dotnet build Fufucode.sln -v quiet` | 0 警告 / 0 错误 | `tmp/m-cycle62-solution.log` |
| `dotnet build src/Fufucode.App/Fufucode.App.csproj --no-restore -v quiet -o tmp/m-cycle62-final-build` | 0 警告 / 0 错误 | `tmp/m-cycle62-final-build.log` 与 `.exit=0` |
| `dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest --skins-integration-only` | 63 PASS / 0 FAIL，原 32 + 新 31 | `tmp/m-cycle62-skins-final.log` |
| `dotnet tmp/m-cycle62-final-build/Fufucode.App.dll --selftest` | 1451 PASS / 0 FAIL，exit 0 | `tmp/m-cycle62-full-final.log` 与 `.exit=0` |
| `dotnet run --project tests/Fufucode.App.Library.Tests -v quiet` | 134 断言通过 | `tmp/m-cycle62-library.log` |
| `dotnet run --project tests/Fufucode.App.Storage.Tests -v quiet` | 212 断言通过 | `tmp/m-cycle62-storage-final.log` |
| `dotnet run --project tests/Fufucode.App.Plugins.Tests --no-build -- --ui` | 46 UI 断言通过 | `tmp/m-cycle62-plugins-ui.log` |

真实 MainWindow/设置/浮动/浏览器/终端测试包含鼠标滚轮/右键、键盘帮助、受限关闭、首次皮肤恢复、hidden 浮动恢复/重开、确认取消与租约释放。不是 HTML：M 已目检实际 CaptureRenderedFrame 设置、浮动文件、800×640 主窗。定向图目录 `C:/Users/mio/AppData/Local/Temp/fufucode-skins-integration-7e416ddf375a42e284b25a84ef3250f0`；全量图目录见日志。

## 失败与纠正

- 页签右键先红后绿，修实际控件样式入口和命中路由。
- 首次恢复 caption 断言抓到“只在编辑设置后换肤”。定向/第一次全量各 1 FAIL；改为 caption 自身订阅动态资源，最终定向/全量均通过。红证据 `tmp/m-cycle62-caption-initial-red.log` 与 `tmp/m-cycle62-app-full.log` 保留。
- 存储 UI 工装把全部 Button.Content 强转 string，帮助图标触发 InvalidCastException。改为先判断类型，再统计“打开”，原 `>= Areas.Count` 不变，212 项通过。
- 一次 `--no-restore` Debug solution 使用旧 Release restore 的 TestApp 诊断依赖而编译失败；正常 restore/build 后 0 警告错误，不删除工程/降级警告。
- 一次在可被重建目录跑全量没有最终退出证据，原 shell/租约 PID 后来消失，原因未明。M 核 main HEAD/index/继承状态后重新取得独占租约，独立输出构建加明确退出文件后完整通过；中断尝试不算通过。

## 在途与未验

NATIVE R2 回传已实际收到，核原会话 completed/idle、clean、HEAD `d3a34da56dd45e9c0a0dbab5524bf72cdd0f836b`。证明第一次 worker→M 回传，不证明失败补扫/去重全覆盖。旧源 `7deef34` 不能验新源码；正确右键/聚焦输入修正旧工装，M 新页签右键反例另证。旧数据根 native Dispatcher 崩溃有进程栈，因果未证实；无头 hidden 恢复绿不关闭原生故障。

AERO 首卡 `3732900...` 已停写且范围正确，M 三个独立反例均红：真实混合顶栏挂接、既有 Theme 引用恢复、既有 aeroCaption 类保护。原 L3/模型/树返工，不集成。163 项样板绿不能证明生产顶栏可用。

未验证：本增量原生指针/IME/DPI/系统剪贴板、所有业务页最终像素审阅、原生旧数据根恢复、当前源码安装卸载。Core/Pi 未改，本轮不冒称新全量。Aero/Luna/Glass 不称完成。下一包原免费 native 会话验冻结新构建，按真人路径查其余缺陷；M 继续修明确问题。

## 提交与冻结构建

主增量提交 `632361d87d87f9bbfbab17e1db32f007e7d75522`（53 文件，全部白名单）。未提交的继承 demo-verify-results.json 保留。随后仅把终端 AffectsRender 注册移到静态构造，避免每次创建控件重复注册；新专用构建 `tmp/e7-native-r3-final` 0 警告错误、Skin63零失败（`tmp/m-cycle63-skin-final.log`）。该跟进未假称另跑 full1451；上一 full 对应主增量，静态注册跟进另有定向证据。原生验收使用冻结产物，DLL SHA256 `F167EF3208E63EF96E7B0E942016D90D2095F91098DA73FAB3421DA4A9E2319B`；允许 Main 后继变化，不得重建覆盖该目录。

静态注册跟进与新原生任务书提交 `0ffb5045cc798e54d22338bc63f83c41ac569017`。已向原 native 会话成功发送完整 R3 范围、冻结来源/hash、直接人类回传授权与严格交付规则；新 turn `01a139ff-3a1a-7443-8f17-06bf270db389` active，不把摘要中的单次工具失败直接当产品缺陷。Aero原L3/DeepSeek返工 turn `01a139f3-b9f7-72e3-b54a-7aca9cb13a2e` active。两槽占满，无新会话，无原生并发。此时M再跑冻结0ffb504的全量，完整退出结果另补。

## 后续逐控件遗漏（第64轮）

ComposerCard.CornerRadius 原代码直接赋本地 Fluent 偏好，压过 Metro/M3 样式；新增实际控件两个断言均 FAIL（`tmp/m-cycle64-corner-red.log`）。可选皮肤清本地值，Fluent 保留原偏好；颜色/材质/圆角等 Fluent-only 选项在固定皮肤中禁用并说明，返回 Fluent 重新可用且原值不改，避免能点击却无效果。

冻结0ffb504完整全量实际返回 1 FAIL（`tmp/m-cycle63-full-final.log` / `.exit=1`）：R05纯配置Redo未重现目标。不能以先前1451绿覆盖此次失败；新增失败诊断保留原同一布尔断言，`--r05-only` 实跑绿，尚未确定是否瞬时文件写入失败。下一全量保留诊断，失败则继续归因；未关闭此观察。原生 R3仍验0ffb504，不受第64轮未提交源码影响。

第64轮定向 Skin67/R05 79绿，完整 App1455零失败exit0（`tmp/m-cycle64-full.log` / `.exit=0`）。随后动态修自动工作台/资料库/回到最新的本地圆角，保默认6/16不变；自动工作台连线修正不存在的 AppAccentBrush 为真实 AppAccent。第65轮发现窗口7个直接SystemAccentColor调色板色仍压过皮肤，实际原生控件颜色断言FAIL（`tmp/m-cycle64-accent-red.log`）。清除仅外观流程所拥有的7色，Metro/M3/浮动/恢复Fluent同路径绿；Skin71、App/solution0警告错误，第65轮完整App最终退出另补。R05之前一次失败保留open-unattributed，两次定向/后续全量绿不证实其原因。

Aero修订2 `bdf8540eb17889ea156eb602c038aa27f189c61c` 完成主动回传、停写且范围/HEAD核实。M再次独立执行原三个反例，green.log三PASS/exit0，red result.log保留；模块169和实际caption探针43支持模块修复，决定accept/not_integrated。未把新模块收编或接目录/浮动/确认窗口，原生/整项另门。

第66轮进一步隐藏固定皮肤中无效果的Fluent配置（含整个颜色组），保留原值供Fluent恢复；M目检设置帧，减少无意义灰色控制项。Skin71绿，65完整App1459零失败exit0。66完整记录1失败：取消核对的实际Enter测试30秒未启动（`tmp/m-cycle66-full.log/.exit=1`）。只读失败SQLite确认native-cancel-run仍Unknown，确未提交，不是只等错Task；定向automation绿。测试保真实Enter和30秒要求，新增输入前enabled/Focus成功及超时真实状态诊断，67完整App1460零失败exit0。该间歇失败原因未证明，保留open，不放宽或绕过实际输入。

第68轮消息头像也按皮肤动态形状，Fluent保12/14、Metro方角、M3圆形；变更范围的说明收进默认折叠帮助。新Mock消息实际模板验证：原测试误拿已虚拟化脱离的旧头像控件看当前窗口资源，两个FAIL；改观察当前真实控件并同时核原消息VM引用不变，预期0/20/12不放宽。Skin74绿，完整68最终退出另补。资料库134/存储212/插件UI46原相关回归见前文。

NATIVE R3 e3a2903实际回传且completed/clean，M核来源/范围并查看真实右页签菜单图：E7-NATIVE-3在冻结0ffb504原生通过。旧data副本新构建一次正常仅“不复现”，不能如源卡称“Dispatcher已修复”。还原、确认框、滚轮、窄窗与多个关键页未跑，整体rework，不把14通过升级完整验收。PID36768是明确原冻结应用，保留交由原验证者正常续接；没有杀进程或改用户状态。

## 最终本增量门（第68轮）

- App 构建 `tmp/m-cycle68-build.log` 0 警告0错误；solution `tmp/m-cycle68-solution.log` 0 警告0错误。
- 当前完整 App `tmp/m-cycle68-full.log` **1463 PASS / 0 FAIL**，`.exit=0`。定向 `tmp/m-cycle68-skins-final.log` **74 PASS / 0 FAIL**（最初32，新增42）。第67轮完整1460绿、C5定向绿另保留。
- 当前实际设置/浮动/主窗图目录 `C:/Users/mio/AppData/Local/Temp/fufucode-skins-integration-b41f278775d44b26a7ff2861c1fd2385`。M实际目检过最终同布局设置图，稳定预览副本 `output/skin-polish-2026-10-14/settings-material3.png`，未触碰已有其他output。
- 资料库最后 `tmp/m-cycle65-library.log` 134绿；存储212/插件UI46没有后续业务变化，不无理由反复跑。Core/Pi、安装、IME/DPI不冒称本轮执行。
- R05 Redo与C5实际Enter各一次间歇失败保留。C5只读失败SQLite确认Unknown、确未提交；输入测试新增确认Focus/启用与真实状态诊断，仍发真实Enter、仍30秒，没有直接执行Command或延长超时。后继两次完整绿不证明原失败根因。

下一步仅原免费native会话在最新冻结构建补用户最关心的滚轮、还原、确认、关键页与返回；Aero模块接受但公共接线仍待M，不能宣称整个皮肤/生产全部完成。继承用户工作保持未暂存。

第二批源码/审核提交 `e3cc78ac9422d7b34e5db77b7fb4e834ddae7d72`（24文件）。专用原生构建 `tmp/e7-native-r4-final` 0警告错误，DLL SHA256 `37E7BB6A60678457352ADEB5CCEC3C97AD291F440BBF65644A031BE1CE7E5FCC`。R4完整L3/原devin/原树prompt实际发送，turn `01a13a46-7cc8-78a3-bae9-8cafeb22e272` active。未因root工具缺失放弃原生，未把14项旧图升格；后续等停写卡再审。
