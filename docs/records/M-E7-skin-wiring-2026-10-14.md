# M E7独立审核与公共换肤接线

本轮从main `71d6f017c09f6fc300d8e8602048baab93c78dbd`继续。M持main-writer独占租约PID7332；原两执行者均完成/停写，树干净，源HEAD与交付卡一致，真实范围核对后接受。用户继承demo JSON/未跟踪目录未覆盖或夹带。

## 接受与收编边界

| 交付 | 源HEAD | 独立M证据 | 主线收编出口 |
| --- | --- | --- | --- |
| E7-DESIGN教师 | 841ce9fe93a1917aa22f77fa91fadb229a86b819 | 重建0警告0错误、45实际Avalonia检查、17浏览器交互检查、实际截图目视 | 226cf40；教师有限样板/参考书，不是生产Aero或用户视觉接受 |
| E7-ENGINE模块 | 4d084888ca305f4c5a49e45afac04393a7fa3a73 | 重建0警告0错误、120真实控件检查、M独立10项Default随App主题/子树跨明暗窗口重新挂载检查、帧目视 | fe0573f；模块已导入，公共接线另由M完成 |

原结构化accept决定保留pending_wiring时点，不覆盖历史；后续真实公共SHA另记integration记录。两包是独立Codex会话，绑定真实turnId作runId，不伪称旧OMP队列脚本已经生成这些事件。

## 本轮公共实现

- 独立WorkbenchAppearance.Skin默认fluent，原Surface材质字段/消费者保留；未知皮肤身份原样保存，只在应用与展示时有效回退Fluent，并显示原因。保原全局/项目稀疏JSON与未知键。
- 现Windows设置式外观区加入Fluent/Metro/Material 3选择；即时持久化/应用走原唯一Customization链。皮肤固定样板accent/形状的说明明确，外观预设保当前皮肤，默认值仍Fluent。设置分类/子页/搜索词补皮肤和具体名称。
- 主窗口与现有/新浮动页签/整栏Window各持独立租约，替换/停靠回主窗/关闭只释放自身资源和Styles。沿原Theme控制Default系统继承；新皮肤覆盖时移除本外观流程自己的直接accent值，避免它们遮住皮肤字典。业务VM/页面/浮动布局不重建，主底部状态条不自动开启。
- 原模块接线记录“Surface无消费者”的旧S1假设按现源更正为独立Skin；教师像素验证prompt改为显式免费devin/swe-2。原worker终答机器卡与源提交不改写，主区接线说明另作更正。

## 红绿、验证与过程事实

| 验证 | 结果/日志 |
| --- | --- |
| 接线前真实主窗口/存储/设置/浮动路径 | 10 FAIL，`tmp/m-cycle59-wiring-red.log`；是明确缺公共接线，不打回受限模块worker |
| 公共接线后皮肤搜索反例 | 1 FAIL，`tmp/m-cycle59-search-red.log`；三处词表修复，E7-SKIN-SEARCH-1登记 |
| 最终solution Release build | 0警告0错误、exit0；`tmp/m-cycle59-final-build.log` |
| 最终App `--selftest --skins-integration-only` | 18 PASS/0 FAIL、exit0；`tmp/m-cycle59-final-skins.log` |
| 主线独立Skins工程 | 120项通过、exit0；`tmp/m-cycle59-main-engine.log` |
| App最终全量 `--selftest` | 1406 PASS/0 FAIL、exit0；`tmp/m-cycle59-app-final.log` |

独立审核日志：`tmp/m-cycle59-engine-build.log`、engine-tests.log、probe.log、teacher-build.log、teacher-tests.log、html-review.log。浏览器初用file协议被工具拒绝，改本地只读HTTP；首次只服务skins目录使根图标404，服务实际repo根后解决，唯一剩余favicon.ico 404不是脚本故障。浏览器17项通过；样板隐藏输入上直接check及不唯一的浮动按钮定位是工装方式问题，改真实label/确切停靠按钮点击，没有放宽行为断言。

新主窗口测试初次只固定延迟未排空UI刷新，出现选择器读到旧取值；按真实Dispatcher/布局作业排空后再断言，选择值要求仍为Metro，未改成允许旧值。过程中M在自己全量自测PID50936未结束时启动另一构建，产生10警告2错误；失败日志`tmp/m-cycle59-search-red-build.log`保留，不能算通过。原自测自然退出后串行重建0警告0错误；最终构建→定向→全量串行，E7-VALIDATION-BUILD-LOCK-1登记。较早全量0失败只对应18项搜索修复前的17项版本，不作为最终源码通过证据。

真实主线M3 800×640帧：`C:/Users/mio/AppData/Local/Temp/fufucode-skins-integration-b4abbbe17ca24e379b591b896212b17f/main-material3-800x640.png`，M已看；教师M复拍与标题栏帧在`output/playwright/m-cycle59-teacher/`。HTML是浏览器样板，真实MainWindow/标题栏是Avalonia无头帧，均不等于原生输入/DPI验收。

## 未验、阻塞与下一步

Core/Pi未改，本增量未重复它们全量或云模型试跑；生产候选冻结时仍必须跑任务书全量门。未做当前源码安装/升级/卸载、原生指针/IME/DPI/实际WebView2/ConPTY、Windows blur/高对比度/多屏长稳。当前工具元数据无node_repl/CUA可执行入口；未知Sandbox PID15712/原出生保持，不接管。旧R1固定候选不包含本轮皮肤源码，不追溯认为它验证了换肤。

Aero仍教师样板；M已写完整E7-AERO独立任务书，下一低成本group/auto-deepseek-v4-1-flash/L3按教师实现资源/控件包，公共目录/窗口/设置接线仍M。Luna后续、Liquid Glass暂缓，默认皮肤/用户视觉选择另门。整项皮肤与生产六门未完成。
