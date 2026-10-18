# M1 手机远控：用户需求与交付范围核查

日期：2026-10-15。用户逐项核验交付，对“WorkBuddy页面参考最后仅交付Web UI”表示不满意。本轮只读文档/源码核查，未运行产品、模型或手机测试，未改既有验收记录与产品代码。

## 本轮判断

已有基础远控网页和服务实现；当前证据不足以认定WorkBuddy参考体验及真实手机端到端生产交付已完成。不能从用户“参考安装包的页面”自动推导必须原生APK，也不能以响应式网页/回环测试替代目标体验验收。是否交付独立安装包属于产品形态选择，不能未核实就重写Android项目。

## 证据

1. docs/tasks/new-product-capabilities.md N1从早期规划起就写Web/PWA原型，但要求正式设计前实际查看WorkBuddy页面/交互，包含项目/会话列表、附件及真实手机局域网/重连等验收。主控后续hourly-M1-remote-control.md明确指定内嵌响应式Web UI；此技术路线不是执行worker自行临时选择。
2. docs/records/hourly-dispatch-ledger.md第73轮声称用户采用PWA/Web+4位PIN；本轮未核对该条所指直接人类消息，不据此断言用户已认可最终客户端形态或对标效果。
3. docs/frontend-prototype/mobile/README.md明确“真实界面运行未验证”：当时无设备/AVD，只有安装包Flutter资产静态信息。这是历史记录，不代表今日仍无设备；本轮没有重新探测Android环境。现有M1交付记录没有补充WorkBuddy真实页面运行/逐页对照证据，因此“1:1对标”未证实。
4. App/Features/RemoteControl/RemoteControlWebAssets.cs实现配对、当前会话、审批和设备页；本轮未发现其中的项目/多会话选择及附件界面。当前自包含资源表仅/、/index.html、/app.css、/app.js，相关远控源码未找到manifest或serviceWorker注册。可确认响应式Web页面，不能仅凭CSS/JS自包含确认可安装离线PWA能力。
5. docs/records/M1-remote-control.md承认未在真机验证IME、Android返回/手势、锁屏/切网、真实WiFi跨设备访问。所谓Pixel/iPhone/Galaxy证据是桌面Chrome设备档案，不是三款真机。103断言与浏览器21项为历史交付记录，本轮未独立复跑。
6. 当前SettingsPageViewModel.Features.cs ToggleRemoteAsync在host未运行且无Adapter时创建Mock.MockChatSession并Attach；设置入口启动可能绑定演示会话。MainViewModel.ToggleRemoteControlAsync顶栏入口绑定真实Session。两个入口尚不能视为行为一致；这里只能确认代码分支，未运行复现。
7. 当前RemoteControlHost/RemoteSessionAdapter提供RequestApproval和WaitForApprovalAsync；M1记录明确真实Pi扩展UI应答接线待M。App源码检索只找到接口定义，没有实际调用这两个入口的公共宿主链路。因此单次审批模块测试不证明真实工具已受手机审批控制。
8. M1-remote-review-2026-10-14.json同时出现integrationStatus=integrated/mainSha=null及nextAction要求后续公共接线；须由M核对最新集成提交补齐可追溯状态，不从该JSON推断最终产品完整通过。

## 建议处理

- M将本轮需求核验作为尚未通过的体验/端到端验收处理，保留已交付服务与网页代码，不抹除历史模块测试事实。
- 先实际观察WorkBuddy参考页面并形成用户可评审的页面/功能映射，确认用户要手机浏览器/PWA还是独立安装客户端；没有原应用运行条件如实记录，不能再用静态资产推测宣称1:1。
- 修正真实会话绑定、审批公共接线、移动多会话/附件等范围差异，依据用户确认范围形成完整修复包，再真机验证。视觉、模块测试、实际跨设备行为、生产包装分别报告。
- 本提问线程不接管main产品写入或独立启动新移动客户端；事实与修复范围交M统一协调。
