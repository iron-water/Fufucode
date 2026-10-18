# E7-ENGINE：Avalonia可选皮肤模块与资源基础

推荐主模型：`group/auto-deepseek-v4-1-flash`，难度L3。理由：有已接受S1输入、资源覆盖和生命周期路径可限定实施；共享组合根/设置/持久化由M。备选`group/auto-qfmodel`仅失败后协调，不自行换模型。模型验证lead/试跑显式devin/swe-2；此包普通编译/机制测试不需模型请求。

## 起点与目标

从创建后的独立树执行，不切回main。读共同协议、本任务、E7-style-brief、S1-skins、skins/TOKENS-MAPPING、frontend-design-guideline、floating-workbench和真实App.axaml、现Styles/Customization/WorkbenchAppearance。S1兼容假设逐条用现源核对，不能照抄旧推断。

独立完成**真实Avalonia皮肤资源模块**，而不是再做HTML原型。现有Fluent为default，新增Metro与Material3两套可选资源/控件状态，明暗可切换，应用范围可局限Window/Control且支持浮动容器同选项。不等用户选首个新默认、不等Opus教师交付再开始；教师新成果尚未交付只能使用已接受S1及当前Fluent约定，M后续增量套用教师规范。Aero/Luna/LiquidGlass本包不实现。

## 允许写入与所有权

- 新 `src/Fufucode.App/Features/Skins/**`：明确SkinId/描述/加载/应用租约、资源与控件样板API。
- 新 `src/Fufucode.App/Styles/Skins/**`：Metro/M3语义资源及scoped控件样式；Fluent恢复用现真实资源，不复制一套业务页面。
- 新 `tests/Fufucode.App.Skins.Tests/**`：独立工程/必要机制与无头编译真实控件测试。
- `docs/records/E7-skin-engine.md`：接线说明/实际证据/卡；本包局部finding写这里。

不改App.axaml/App.axaml.cs、MainWindow*/Settings*、WorkbenchAppearance存储、原Styles/*.axaml/Icons、现SelfTestRunner/solution、Core/Pi、共享计划/进度/决策/全局失败登记，其他任务路径不碰。src新文件自动进入既有App工程，不需改csproj。新测试按O3既有实践剥离WebView2 WPF/WinForms传递引用，不靠NoWarn藏警告。主外壳设置选择、Appearance迁移、所有window接线和全量自测注册由M独立处理。

## 必须实现

1. 有明确皮肤枚举及目录；不把light/dark误作不同skin；不存在ID可解释地fallback Fluent且不覆写持久输入。稳定Semantic App*资源映射，可保系统accent或明确样板固定accent策略，不隐藏主题错误。
2. API接受当前Control/TopLevel，明确skin+ThemeVariant，仅替换自身资源/样式，dispose/恢复只移除自身引用，不能清空别人的Resources/Styles。连续Fluent→Metro→M3→Fluent不泄漏/积累，不污染另一个窗口；临时挂载/浮动页面不销毁业务VM。
3. 皮肤作用于真实Avalonia按钮/TextBox/ComboBox/菜单/ListBox/页签/设置卡/浮层的状态；Metro角/平面/选择状态、M3圆角/容器/状态层要有区别，不能只是换三种颜色。遇复杂内置ControlTheme应给已编译可用的限定样板而不是批量复制全部Fluent模板。默认Fluent原值保持。
4. 语义色包含正文/次要/边框/hover/pressed/focus/selected/disabled/error/success/warning、terminal/diff的独立背景/前景；亮暗对比可读。用系统Segoe UI fallback，Roboto不擅自采购/下载捆绑。
5. 在独立无头Window组装当前项目的代表性真实控件/设置行/页签/terminal风格静态展示区，产出两skin×亮暗截图及状态检查；不能把HTML screenshot当Avalonia输出。Window尺寸1100×800和800×640至少覆盖一组，125%/150%仅工装缩放说明、真实系统DPI另验。
6. 接线卡：M该在哪里new、如何apply/reapply/theme变化/新浮动窗口、如何dispose/recover、哪些App/Fluent语义key要补消费、哪些字体/阴影需要教师确认。资源热切换不能由StaticResource旧别名看起来能换就算成功，要用运行时真实控件属性/帧证明。

## 验收及退出

App build零警告零错误，新测试独立Rebuild零警告零错误；新默认/失败/连续切换/亮暗/双窗口互不污染/资源复原/存储不变测试全绿，真实控件compiled-binding与渲染帧实际目视检查。核心协议无改不重复Core/Pi/云自测；原生键鼠/IME/DPI/浮动HWND/WebView2必须如实未验。不得宣称完整皮肤生产可用或代替M全量与原生门。

只临时数据，不读写真实ui.json/Desktop.db；不push/同步main/清理工作树/登录采购。逐增量提交，记录scope与actual command/exitCode、截图、本包findings及公共接线。完成终答JSON delivery.card并停写，不自行下一任务。模块待接线是有效交付。
