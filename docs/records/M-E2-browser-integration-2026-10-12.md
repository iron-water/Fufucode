# E2-B 主线审核与公共接线

M真实main起点7d7426fdda3423dddf3f4a4db259474dd0fa8da3。受管原chat01a13085-8c79-7602-a35e-9119100e6a9f、原L4/group/auto-deepseek-v4-1-flash、树1dd7，最新交付0fe7d9889315959edbb1876f7545ae608cf80f32。继承用户状态保护，不push/重写历史/删除工作树、未知会话不接管。

## 终态证据与审核

Codex app监督工具连续报Transport closed，没有因此判worker失败/重启或重派。只读已知本地rollout的task_started/task_complete元数据确认真实修复turn01a13101-3a92-70b2-a094-cc344d72adc9于2026-10-12T16:53:10.009Z完成，之后无新task_started；没有先读活写正文。树HEAD0fe7d98干净、无树专属App/dotnet/pwsh进程后才读最终卡；卡的完整SHA与HEAD一致。旧gate/workflow原PID不在，main仍唯一M写入。元数据及最终卡摘录由tmp/m-observe-thread.py、tmp/m-read-completed-card.py取得，不打印推理/工具正文或会话凭据。

M独立原工装38项全通过：15无头/23真实WebView2 SDK，tmp/m-e2-browser-rework2-green.log；原树tmp/browser-review-6696d75140bc4ac0b9f55282bf3de8f9，原ID选择/Stop即时状态/Stop再发实际title都已修，无放宽断言。M独立模块49项全通过/exit0（原43+执行者6实际反例），源tmp/m-e2-browser-rework2-directed.log。新增差异2提交/4文件允许，历史全部13提交/14文件符合任务范围。

decision=accept模块；旧三项相邻缺陷verified。依据已验证源码/行为才正常cherry-pick全13提交至main9c46d1fff30633160c747346f0826da067fb945e；原树/保存会话/全部证据保持，不回改固定P2候选。来源0fe7d98不是main集成SHA，公共接线实现SHA完成验证后补录。

真实公共接线mainSHA：1f5e281532698360f797afa66e59f934b6b64253；结构化decision=accept/integrationStatus=integrated见E2-browser-review-accepted-2026-10-12.json。后续仅证据补录，不改变已验证源码，不标整项E2/G3完成。

## 公共接线

13个BrowserSurfaceActions投影到共享动作/快捷键设置目录及Browser作用域，按钮/命令面板/设置共享真实能力原因。浏览器操作在既有菜单分组，图标按动作含义选择；快捷键默认来自同一目录，不再复制第二张默认表。未设置是默认、改绑旧手势失效、禁用不退回默认。

WebView2是独立原生子窗口，仅Avalonia KeyDown无法覆盖网页焦点。BrowserViewHost订阅实际controller.AcceleratorKeyPressed，仅处理KeyDown/SystemKeyDown，按实时Ctrl/Shift/Alt/Meta传给共享表；匹配处理在Dispatcher后续执行，避免在SDK同步回调里调用导航。事件在controller释放时解除。旧浏览器默认手势若已改绑/禁用，会标Handled拦截WebView2旧行为；普通文字/Ctrl+C仍交网页。此回调接线及M检查不等于实际系统键盘/IME注入通过，后者仍未验。

普通Dock或通用浮动宿主使用同一个BrowserView；原生按键绑定实际发出事件的view，不误投递另一预览页。公共菜单优先保留返回焦点对应浏览器，否则主browser页；模块关闭时动作不可用，再启用重建时同一工厂重新接线并恢复全部tab。命令面板遮蔽仍只作用主窗口WebView，不破坏其他浮动窗口。

17项BrowserIntegration真实控件检查注册到App默认全量及--browser-integration-only：13个动作真实目录/作用域、无假None手势、历史不可用、原生回调路由、新tab真实创建、改绑旧默认抑制/新绑定生效、禁用无回退、普通输入放行、浮动同view/同表、重停靠身份、模块停用/恢复、虚拟键映射。定向0失败，tmp/m-e2-browser-main-target3.log。首次测试代码误用并不存在的FloatPageForTesting/ToggleCommand导致4编译错误，改用真实FloatDockPage/DockFloatingRegion/IsEnabled；不更改产品接口制造通过。第一次两项失败是无头夹具在浮动时Dispatcher.Post尚未被抽取，诊断显示action实际在重停靠后才执行；测试明确RunJobs执行已排队回调，保留tabs=4与恢复4原断言，非产品修复或任意延时放宽，红/诊断日志保留。

## 最终门与边界

主线solution默认--no-restore构建0警告0错误/exit0，11.80秒，tmp/m-e2-browser-acceptance-build.log；App默认--selftest全量996条ok/0失败/exit0，含17 BrowserIntegration，tmp/m-e2-browser-acceptance-full.log。随后主区--native-module-selftest退出0，tmp/m-e2-browser-main-native.log：两个controller create=1/1、release=0/0、reparent=2/2，真实父HWND回到main、DOM保留、整栏浮动/隐藏重开、查找取消/重开、下载取消/无落盘和模块停用/恢复全部PASS。不是以源树绿色代替main结果。

主线独立模块 `dotnet run --project tests/Fufucode.App.Browser.Tests`49项全通过/exit0，tmp/m-e2-browser-main-module.log，未降全局警告。38项工装也在主区接线后复验全PASS（15无头/23实际SDK）/exit0，tmp/m-e2-browser-main-review.log、主区tmp/browser-review-907b32f2ce6a4411ae718c931331e97b。日志HEAD9c46d1f是产品提交前父HEAD，接线源码当时为M未提交的本次变更，完成后原样提交，不把父HEAD误当已含接线；其临时项目不纳入发布包，不改变SDK/profile路径。

原生用户指针/IME/DPI/多显示器、真实网站登录态、高级Agent浏览器操作/下载管理仍未验或范围外。浏览器基础增量不等于Codex全部能力或E2整项终验；固定P2包未包含本次功能，不冒称新安装验收。Core/Pi源码本轮未改，不重复不相关模型/付费云验证。监督通道故障单列，记录可供恢复后向原chat发送停写保留通知，不改其模型/身份或自动开始下一任务。

收尾已尝试向原chat发送实际集成SHA/停写保留操作卡，但send_message_to_thread仍Transport closed，明确未送达，未另开会话/改用未知传输重复投递。原task_complete与树仍保存；通道恢复后先对账再发送只读操作卡，新派工可暂留空槽。用户继承demo结果与全部未跟踪文件最终仍未暂存，未夹带提交。
