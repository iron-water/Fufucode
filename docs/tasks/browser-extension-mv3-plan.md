# B2-EXTENSION 浏览器扩展插件（Chrome / Edge MV3）开发计划与协议规范

日期：2026-10-14  
状态：已规划待开工（归属大功能矩阵）  
对应用户指示：对标 Codex 浏览器插件与 WorkBuddy 扩展能力，学习 Playwright/CDP 思路，实现外部浏览器与 Fufucode 的双向互联。

---

## 1. 架构定位与对比

| 维度 | 内置 WebView2 浏览器（B1 已交付） | 外部浏览器插件（B2 本计划） | Playwright 无头驱动（对比参考） |
|---|---|---|---|
| **宿主环境** | Fufucode 应用内嵌进程 | 用户真实使用的 Chrome / Edge | 独立启动的隔离浏览器进程 |
| **控制机制** | CDP 原生调用（`CallDevToolsProtocolMethodAsync`） | MV3 Content Script + WebSocket/HTTP 桥接 | CDP / DevTools WebSocket 直接控制 |
| **适用场景** | 智能体直接浏览、页面拾取与内置闭环 | 用户日常网页浏览、一键引用、真实登录态抓取 | 自动化批量测试与爬虫脚本 |
| **认证隔离** | 独立隔离会话，保护宿主数据安全 | 复用用户现有登录状态与 Cookie | 全新临时上下文，无既有登录态 |

---

## 2. 扩展工程结构（`extensions/browser-extension-mv3/`）

```
extensions/browser-extension-mv3/
├── manifest.json                  # MV3 规范清单（Chrome / Edge 通用）
├── icons/
│   ├── icon-16.png
│   ├── icon-48.png
│   └── icon-128.png
├── background/
│   └── service-worker.js          # 后台 Service Worker：维持本地 WebSocket/HTTP 心跳与命令中继
├── content/
│   ├── inspector.js               # 页面 DOM 拾取器：悬停遮罩、选择器计算、点击传回
│   └── extractor.js               # 页面正文提纯（Readability 算法简化版）
├── popup/
│   ├── popup.html                 # 插件弹窗界面：连接状态、PIN/Token 状态、快捷动作
│   ├── popup.css                  # 遵循 Fufucode 紧凑现代风格
│   └── popup.js                   # 弹窗交互逻辑
└── README.md                      # 开发者加载说明与安装指导
```

---

## 3. 本地端点与通信协议规范

Fufucode 在内置本地端口（默认 `54321`，与 RemoteControlHost 协调或复用统一服务层）开放 `/api/extension` 专用通道：

1. **握手与认证（Handshake & Auth）**：
   - 扩展首次连接需向 Fufucode 申请配对令牌（或在弹窗输入本地 PIN 码）；
   - 请求头携带 `X-Fufucode-Extension-Token: <token>`；
   - 每次心跳验证状态，防止外部恶意网页探测。

2. **核心协议消息（JSON 契约）**：
   - `extension.handshake`：上报浏览器名称、版本、活动标签页 URL。
   - `extension.page.capture`：扩展将当前页面的 DOM 文本、选中片段与元数据推送给 Fufucode 对话输入框或资料库。
   - `extension.inspector.start`：Fufucode 触发扩展进入高亮审查模式，用户点击网页任意元素后，返回唯一 CSS 选择器。
   - `extension.agent.action`：Fufucode 智能体向扩展发送控制指令（如点击选择器、输入文字、滚动页面）。

---

## 4. 交付与验收标准

1. **MV3 规范校验**：通过 Chrome 与 Edge 的 `开发者模式 -> 加载已解压的扩展程序`，0 警告 0 报错；
2. **本地双向通信**：扩展后台 Service Worker 自动检测 Fufucode 本地服务运行状态，图标变绿表示已连接；
3. **页面拾取可用**：点击“引用到 Fufucode”，文本与当前 URL 丝滑注入当前活动会话时间线；
4. **测试与安全门禁**：协议编解码、Token 校验与防注入测试 100% 独立断言通过。
