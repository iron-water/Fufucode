# B2 浏览器扩展连接增强：交付记录

日期：2026-10-22
状态：首轮垂直增量已完成，待真实 Chrome/Edge 人工验收

## 本次完成

- 外部浏览器桥新增 `/api/extension/handshake`，记录稳定 `clientId`、浏览器版本、活动页面、标签页数量和能力清单。
- `/api/status` 区分监听、扩展流量、握手和当前客户端快照；只有握手、长轮询或结果回传能刷新在线状态，停止时清除旧客户端状态。
- MV3 service worker 增加稳定客户端 ID、握手重试、能力清单和活动标签页快照；状态弹窗增加浏览器、当前页面、标签页数量、能力和“重新握手”。
- 页面采集由 popup 经 service worker 注入/重试内容脚本，受保护页面会返回可读错误。
- 内容脚本改进 React 等受控输入框的原生 value setter、输入事件、select 类型校验和正文长度上限；Inspect 发送失败时显示真实错误。
- 桌面设置页显示浏览器身份、握手就绪、当前页面、标签页数量和能力清单。
- README 补充握手、就绪状态、鉴权和断线语义。

## 实际验证

- `node --check extensions/browser-extension-mv3/{background/service-worker.js,popup/popup.js,content/agent.js,content/collector.js,content/inspector.js}`：通过。
- `dotnet run --project tests/Fufucode.App.Browser.Tests`：162 项断言通过。
- `dotnet build src/Fufucode.App/Fufucode.App.csproj -c Debug --no-restore`：0 错误、0 警告。
- `git diff --check`：通过。

## 未完成 / 限制

- 尚未在真实 Chrome 与 Edge 中加载应用数据目录的扩展，未完成人工点击、输入、截图、导航和断线重连验收。
- 本轮继续使用现有回环 HTTP + 长轮询传输，没有切换到 Chrome Native Messaging；后续若需要更强生命周期与浏览器启动联动，可单独增加 native host 适配层。
- 工作区已有与本任务无关的用户改动，未提交、未重置、未覆盖。
