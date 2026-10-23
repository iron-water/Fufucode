# B2 浏览器内完整工作台 v1

日期：2026-10-22
分支：`feature/browser-workbench`
提交：`c368b323`
审阅 label：`feature/browser-workbench@c368b323`
审阅目录：`C:\Projects\Fucode\Fufucode\out\review\feature-browser-workbench`

## 本次交付

- 新增桌面优先的浏览器工作台页面：会话列表、流式时间线、工具卡、审批卡、停止、连接诊断和当前浏览器上下文栏。
- 工作台直接复用现有 `service.remote-control` 的 PIN、Bearer token、WebSocket snapshot/增量、会话目录、会话切换、消息、停止和审批协议。
- 扩展新增 `side_panel` 声明，并在 popup 增加“打开完整工作台”入口；service worker 负责聚焦/创建工作台 tab、读取活动标签页上下文和投递网页引用/浏览器动作。
- 远控服务增加 `/api/service-info`，区分手机远控服务与外部浏览器动作桥，降低端口误填造成的协议误接风险。
- 远控插件静态资源输出包含 `extensions/browser-extension-mv3/workbench/`，审阅构建已确认 `index.html`、`workbench.css`、`workbench.js` 存在。

## 验证

- `node --check`：workbench、service worker、popup 全部通过。
- `dotnet build src/Fufucode.Plugins.RemoteControl/Fufucode.Plugins.RemoteControl.csproj -c Debug --no-restore`：0 错误、0 警告。
- `dotnet build src/Fufucode.App/Fufucode.App.csproj -c Debug --no-restore`：0 错误、0 警告。
- `dotnet run --project tests/Fufucode.App.RemoteControl.Tests`：139 项通过。
- `dotnet run --project tests/Fufucode.App.Browser.Tests`：162 项通过。
- `git diff --check`：通过。
- `pwsh fucode/feature-publish.ps1`：发布成功并启动独立审阅窗口 PID 26616。

## 未完成 / 真实验收边界

- 本轮未在真实 Chrome/Edge 中人工加载审阅构建扩展；side panel、工作台 tab、真实网页引用、断线重连和审批交互仍需人工门验收。
- 工作台首切片使用现有远控 WebSocket 协议，仍保留手机远控的明文 HTTP/局域网认证边界；Native Messaging、TLS、事件游标和更细粒度 BrowserContext/session generation 属后续切片。
- 当前 feature 工作树已提交且干净；没有覆盖主工作区其他未提交改动。主控需按项目流程审核后再决定是否合入 main。
