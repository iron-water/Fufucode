# E7-AERO：按教师样板实现可选Aero资源与控件包

推荐主模型：`group/auto-deepseek-v4-1-flash`，难度L3，建议max。理由：教师已给具体色值/状态/已编译caption样板，可限定到资源与控件模块；生产组合根/设置/浮动外壳接线仍M自做。备选`group/auto-qfmodel`只协调后采用，不自行换模型。所有验证lead/模型试跑显式`devin/swe-2`；普通本机编译/无头机制无需模型请求，不继续消耗Opus额度。

## 开工与目标

从创建后真实隔离树开工，核对cwd/HEAD/分支/继承改动。全文读共同执行协议、本任务、`docs/design/skin-teacher-reference.md`、`docs/frontend-prototype/skins/teacher/README.md`、AeroCaption*.axaml及对应截图、E7-style-brief、真实SkinCatalog/SkinLease与现App/Workbench样式。教师与Metro/M3模块已由M接受并收编，参考书四包仅建议；以本任务的合并范围和成本纪律为准。

交付**独立可编译、可应用与释放的Avalonia Aero资源/控件包**。复用已有Metro/M3租约机制与教师caption，不改业务外壳，不再造一套HTML，不做全应用重写。仍保持Fluent默认；Aero Night是可选的教师暗色方案，不宣称用户已视觉确认；全无DWM模糊时也须用可读实色/渐变工作。

## 唯一允许写入

- 新 `src/Fufucode.App/Styles/Skins/Aero/**`：语义token字典、scoped组件样式、必要独立ControlTheme。
- 新 `src/Fufucode.App/Features/Skins/Aero/**`：独立Aero应用/释放入口、必要新控件或局部资源适配；不得修改既有SkinCatalog.cs/SkinLease.cs。
- 新 `tests/Fufucode.App.Aero.Tests/**`：独立编译/状态/切换/帧测试及本包证据。
- `docs/records/E7-aero-module.md`：实际身份、命令、证据、局部findings、M接线卡、完整交付卡。

不修改MainWindow*/Settings*/WorkbenchAppearance/WorkbenchSections、原Styles/Icons、原Skins两个文件/Metro/M3、App/Program/SelfTestRunner/solution/csproj、Core/Pi、教师样板与共享文档。新src自动进入App工程；新测试按已有Skins工程真正剥离无用WebView2托管引用，不用NoWarn。必要资产优先沿教师Path/原项目图标，不重复批量绘制、下载采购字体或复制商业素材。

## 内部步骤与必须行为

1. 根据教师表生成Aero亮/暗语义层（App*/Fluent桥接，正文/次要/错误/警告/成功/禁用/选中/terminal/diff）与组件形状、边框、字体、渐变。写明哪些严格复用教师值、哪些依据Avalonia限制降级；内容纸/编辑器/终端/diff不透明且无背景渐变，不拿窗口glass覆盖正文。
2. 实际移植教师caption资源/ControlTheme与active/inactive、normal/hover/pressed/keyboard-focus/disabled、min/max/restore/close。保46×32命中区和现caption按钮的Button/自动化名称/Click语义；板下11px真实无头点击仍激活。不修改生产拖拽/双击逻辑或冒称原生DWM已验。
3. 实现真实Button/iconButton/accent、TextBox、ComboBox、dockTab/TabItem、ListBox选中项、settingsRow、菜单/对话框/浮层的限定外观与状态。用DynamicResource及需要的模板部件选择器，不复制整个Fluent模板。生产个别局部值优先级的缺口列入接线卡，不偷偷改旧视图。
4. 提供真实Control/Window级应用/释放入口；按引用只撤销自己追加的资源/样式/类/显式Theme，保原VM/内容/他人资源，失败不留下半挂层，重复释放幂等。可复用既有SkinLease设计而不改其文件。系统Default、Light、Dark、热切换和双窗口独立行为要真实验证。M日后添加目录Aero入口及主/浮动窗口公共接线，不由worker越权。
5. 用真实编译Avalonia树分别构建亮/暗代表窗口与状态画廊，至少1100×800与800×640，真实CaptureRenderedFrame目检。HTML截图仅教师参照；不能把CSS或静态画图当Avalonia截图。验证actual控件/模板属性与状态选择器，对比关键色/渐变stop、形状、命中尺寸；字体栅格差异单独说明。

## 验收与收尾

App与独立测试工程Rebuild 0警告0错误；真实状态/热切换/重复释放/双窗/外部资源/失败回滚检查全绿。至少一个真实邻接行为反例先红再修绿，留证据，不为凑红改坏任意代码。所有测试只临时数据，不读写真实ui.json/desktop.db；有条目未验如实notRun。

记录资源URI/应用/释放的实际API，给M说明如何挂现CaptionAssetButton、新浮动容器，哪几个原视图StaticResource/局部值须调整、哪些桥接key仍未消费。只限模块验收，不宣称整个Aero或皮肤整项生产可用；原生DPI/IME/真实窗口/系统blur、高对比度、跨显示器均另验。

逐增量提交，只包含本包；继承改动保护，不同步/整合main、不push/改写历史/清树/登录付费部署，不自行派下一任务。原模型返工，不自行续用贵教师模型。完成后记录完整交付卡、终答唯一JSON delivery.card含真实全SHA、范围、passed/failed/notRun、接线需求与writerStopped=true，然后停止。
