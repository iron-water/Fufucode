# Fufucode Assistant - 浏览器双向协同插件 (Chrome / Edge MV3)

基于 Google Chrome / Microsoft Edge **Manifest V3** 规范构建的扩展程序，实现外部浏览器与 Fufucode 桌面客户端的无缝双向通信。

## 核心特性

1. **一键引用**：提取当前网页的核心正文或用户鼠标划选片段，并自动携带网页标题与来源 URL，推送至 Fufucode 当前对话；
2. **元素拾取器 (Inspect Tool)**：在任意网页上悬停查看高亮边框与标签，点击自动提取唯一 CSS 选择器；
3. **智能体控制**：长轮询桌面端命令，在真实登录态里执行导航、点击、输入、滚动、悬停、按键、下拉选择、等待、DOM 读取、截图、JS 求值、页签管理；
4. **安全 PIN 配对**：4 位 PIN 换长期令牌，5 次错误锁定；扩展会读取同目录 `config.json` 自动配对。

## 安装（推荐：从应用数据目录加载）

1. 启动 Fufucode，打开 设置 → 浏览器 → 设备自带浏览器扩展；
2. 点“打开扩展目录”，应用已把扩展和 `config.json`（端口 + PIN）写在这里；
3. Chrome 打开 `chrome://extensions/`（Edge 为 `edge://extensions/`），开启开发者模式；
4. “加载已解压的扩展程序”，选择上面打开的目录；
5. 扩展自动重连后，设置页状态变为“扩展已连接”。

也可以直接加载仓库里的 `extensions/browser-extension-mv3/`，在弹窗底部填写应用设置页显示的实际端口，再用 PIN 配对。

## 连接与动作协议

扩展安装后先用 PIN 换取长期令牌，然后向桌面端发送 `POST /api/extension/handshake`，上报稳定 `clientId`、浏览器版本、活动标签页、标签页数量和能力清单。桌面端只有在收到握手和后续轮询/结果流量后才把扩展标记为“已就绪”；弹窗会分别显示未连接、需配对、等待握手和已就绪。

桌面端 → 扩展：`GET /api/next`（最长 25 秒长轮询）返回 `{id, action, arguments}`。
扩展 → 桌面端：`POST /api/result` 回传 `{id, ok, result | error}`。
扩展 → 桌面端：`POST /api/extension/handshake` 更新连接快照。

所有接口只监听回环地址，并要求 `Authorization: Bearer <token>`；PIN 错误达到上限后短暂锁定。扩展重启会复用 `clientId`，桌面端重启会清除旧握手状态，避免把过期客户端显示为在线。

| action | 参数 | 说明 |
|---|---|---|
| `state` | `tabId?` | 当前页签地址、标题、主机 |
| `navigate` | `url` | 导航并等待加载 |
| `click` | `selector`, `confirmed?` | 敏感按钮返回 `needsConfirmation` |
| `type` | `selector`, `text`, `clear?` | 输入并派发 input/change |
| `scroll` | `x`, `y` | 滚动页面 |
| `hover` | `selector` | 悬停元素 |
| `key` | `key`, `selector?`, 修饰键 | 派发按键；Enter 需确认 |
| `select` | `selector`, `value` | 选择下拉项 |
| `wait` | `selector`, `timeoutMs` | 等待元素出现 |
| `get_dom` | `selector?`, `maxText`, `maxHtml` | 读取文本与 HTML |
| `screenshot` | `format` | 当前可视区截图 |
| `evaluate` | `script` | MAIN world 执行脚本 |
| `tabs` / `activate_tab` / `open_tab` / `close_tab` | `tabId`, `url` | 页签管理 |
