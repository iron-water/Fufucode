# Fufucode Assistant - 浏览器双向协同插件 (Chrome / Edge MV3)

基于 Google Chrome / Microsoft Edge **Manifest V3** 规范构建的扩展程序，实现外部浏览器与 Fufucode 桌面客户端的无缝双向通信。

## 核心特性

1. **一键引用**：提取当前网页的核心正文或用户鼠标划选片段，并自动携带网页标题与来源 URL，推送至 Fufucode 当前对话；
2. **元素拾取器 (Inspect Tool)**：在任意网页上悬停查看高亮边框与标签，点击自动提取全局最优唯一 CSS 选择器，方便智能体后续进行自动化控制；
3. **安全 PIN 配对**：与 Fufucode 局域网服务通过 4 位动态 PIN 码安全握手，具备单客户端防暴破锁定保护。

## 本地安装步骤

1. 打开 Chrome 访问 `chrome://extensions/`（或 Edge 访问 `edge://extensions/`）；
2. 开启右上角的 **“开发者模式” (Developer mode)**；
3. 点击 **“加载已解压的扩展程序” (Load unpacked)**；
4. 选择本项目根目录下的 `extensions/browser-extension-mv3/` 文件夹；
5. 在浏览器工具栏固定该插件图标，启动 Fufucode 桌面端并在设置中查看 4 位 PIN 码完成配对。
