# E7-NATIVE：当前三皮肤与浮动外壳原生验收

测试lead显式`devin/swe-2`（免费），难度L3；验证例外不用group实现模型。固定产品SHA `7deef34974fb1af0b1c6723d8587f0aa13a3b1b3`。本包仅独立验证、缺陷复现与记录，不能修改产品/模块/自测或替M接线；与E7-AERO资源实现并行，但原生桌面操作由本包独占串行，不操作用户其他窗口。

## 输入与范围

先核对创建后隔离树cwd/HEAD/继承状态，读AGENTS、共同协议、hourly-main-coordination、M-E7-skin-wiring-2026-10-14、E7-skin-integration-2026-10-14.json、floating-workbench和computer-use技能。主控工具元数据没有node_repl，并不证明新执行会话也没有；先按技能检查本会话真实工具并做最小安全可用性核查，再决定是否blocked，不能用headless冒充原生。

允许写 `docs/records/E7-native.md`、`tmp/e7-native/**`与`output/e7-native/**`证据。生产源码只读；固定产品含Fluent/Metro/M3设置与浮动接线，不含将来Aero模块。新工作树起点含M后续记录/测试改动时，按固定SHA另输出专用构建：不得reset/checkout覆盖继承内容，不偷偷同步main，必要时从固定提交导出到自身tmp下独立构建并记录源来源。至少明确产品SHA、实际构建路径/命令与运行参数；新文档提交不改变产品来源。

只临时数据根与独立测试目录，不读写真实ui.json/desktop.db、不碰用户Sandbox/浏览器profile/终端进程。禁止收费模型调用；需要会话则复用V3-native记录的本地合成HTTP流与真实Pi/OMP CLI方法，不把mock证据写成真实模型。仅自身临时应用/本地夹具可关停，按PID/创建时间/路径精确核对。

## 顺序与验收

1. 自己的零警告零错误Release构建启动真实Windows窗口，截图/AX/真实鼠标与键盘一起取证。读出默认Fluent、状态栏关闭、设置导航/返回，与当前已完成Windows设置及辅助栏结构一致。不能把旧图或静态源码当当前native证据。
2. 在设置页使用真实控件选择Fluent→Metro→Material3→Fluent，明暗/跟随系统（不修改用户全局Windows主题，可用App显式选择；真实OS主题事件若不能验证列notRun）、设置搜索“皮肤”、撤销/重做、关闭应用再启动同测试根恢复。对比截图中按钮/输入/设置卡/菜单/页签至少一组；拒绝只读JSON而不检查窗口显示。
3. 按真实菜单浮动单页签与整辅助栏，换皮肤/明暗，看主/浮动窗口是否一致；关闭浮動窗口为隐藏、重开/停靠保原页/草稿及原终端内容。验证菜单/ComboBox弹出层、焦点环/Tab键路径，浮动后返回设置及主聊天，不出现无法返回。
4. 800×640窄窗、125%/150% DPI仅在可以局部控制自身窗口/显示测试环境且不改用户全局设置时验证。做不到保notRun。设置选择/菜单项不越界遮挡，极扁状态条仅主动打开时出现，与底部终端不同。切皮肤不得自动开启或清浮动布局。
5. 真实中文输入法/焦点与终端/浏览器：检测本机可用输入法，只有真实输入且回读文本才能标IME通过。用临时PowerShell终端验证中文输入、复制/粘贴草稿、浮动/停靠内容保留。浏览器只用本机HTTP无登录页面，验证返回/前进/地址输入/切页与皮肤外壳无布局损坏。无真实WebView2/ConPTY/IME条件分别记blocked/notRun，不把存在控件当全功能通过，不外网登录采购。
6. 不要求覆盖完整产品G1/G2/G3、全部插件/快捷键/存储。每项按passed/failed/notRun写实际操作/读回/截图；缺陷给确定复现和源状态，由M修，worker不改断言/产品。停止后再提交证据卡，M独立验卡/证据，不从绿色声明猜已验。

## 停止与交付

工具不可用或某项外部阻塞立即记录可核事实，继续其余不依赖的验证；原生全缺则交blocked卡，不循环等待/启动贵验证lead。新发现不向未知会话发消息；需要协调给卡。

记录实际工具/provider、产品SHA与构建命令、应用/夹具PID、数据根、逐项证据、未验原因、本包文件/实际提交。只提交本包docs记录和明确证据清单；不改共享progress/decisions/failure-registry，不push/改写历史/删除worktree。不自行开下一任务或重新派发。

最终严格唯一JSON delivery.card：taskId=E7-NATIVE，commits实际完整SHA，passed/failed/notRun分开，blockers事实，writerStopped=true。这是原生验证交付，最多关闭明确测试路径，不能宣称用户视觉确认或完整生产安装验收。
