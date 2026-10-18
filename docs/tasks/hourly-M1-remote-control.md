# M1-REMOTE 任务书：手机局域网远程控制 (WorkBuddy 对标 + 4位PIN安全配对)

2026-10-15用户更新：最终交付改为参考WorkBuddy代码结构/视觉资产与真实页面的独立Android App，并在本机模拟器实际安装运行，详见 [新范围](M1-android-workbuddy.md)。下文WebUI模块保留为历史已实施范围，不再作为手机App整项完成标准。

难度：L3 ｜ 推荐模型：`group/auto-deepseek-v4-1-flash`（显式 max 思考强度）或 `group/auto-hy4-preview-f`
任务类型：受管特种兵独立功能包 ｜ 起点 HEAD：`c86254fecbe79c7bcd393c49141d51a37859385e`
工作树绝对路径：`C:/Projects/Fucode/worktrees/m1-remote`
分支：`agent/m1-remote`

## 1. 允许修改路径
- `src/Fufucode.App/Features/RemoteControl/**`（新建目录与源码）
- `src/Fufucode.Core/RemoteControl/**`（新建轻量服务与契约）
- `tests/Fufucode.App.RemoteControl.Tests/**`（新建独立测试工程）
- `docs/records/M1-remote-control.md`（本任务记录文档）

## 2. 严禁触碰路径
- 严禁修改 `MainWindow*`、`SettingsPage*`、`WorkbenchSectionsView*` 等共享外壳文件；公共 UI 入口由主控 M 统一收编接线。

## 3. 功能目标与验收标准
1. **轻量内置服务**：
   - 基于 .NET 10 内置网络库（`HttpListener` 或 `System.Net.WebSockets`）实现自包含局域网服务，无需外部第三方重量级运行时。
   - 提供服务生命周期管理：`Start(int port = 0)`（自动分配或指定端口）、`Stop()`、状态查询（IP、端口、连接数）。
2. **移动端 Web UI (对标 WorkBuddy)**：
   - 内嵌单页响应式 Web 界面（自包含 HTML/CSS/JS），1:1 对标 WorkBuddy 移动端版式，针对手机触控优化（深色/浅色自适应、底部导航、紧凑卡片）。
3. **4 位 PIN 码安全认证**：
   - 服务启动生成 4 位随机数字 PIN 码与配对 URL；
   - 手机浏览器扫码或访问时，先进入输入 PIN 码界面；验证通过后颁发短期安全令牌（Token），未认证连接无法获取会话数据或发送指令。
4. **移动端核心控制**：
   - 实时流式查看当前主会话消息时间线与工具调用进度；
   - 发送文本消息 / Follow-up 指令到会话；
   - 工具调用待审批卡片显示，支持手机端点击“批准”或“拒绝”；
   - 一键紧急停止按钮（终止当前在途 Agent 任务）。
5. **验证纪律（v3 功能优先原则）**：
   - 编写 `tests/Fufucode.App.RemoteControl.Tests` 验证：
     1. 本地回环端口启动、停止与状态暴露；
     2. 正确 PIN 认证通过、错误 PIN 拒绝；
     3. 会话数据读取、消息投递与审批回调流转。
   - 构建 `dotnet build tests/Fufucode.App.RemoteControl.Tests` 达到 0 警告、0 错误。
   - **不执行** 1480 断言的 App 全量自测与原生桌面测试（延后至里程碑收尾集中执行）。

## 4. 人类授权与交付回传协议
- 本任务获用户直接授权：
  > 授权来源：主agent (提问) 01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07
  > 用户原文：“我明确授权：你向本项目受管执行会话派单、返工和协调；这些执行会话向你所在的主控线程主动回传交付、阻塞和协调消息。派单时附本授权原文和真实主控threadId。”
  > 主控线程 ID：`01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07`
- 任务收尾时必须在 `docs/records/M1-remote-control.md` 中完整记录：状态、真实 HEAD、改动、测试结果、未完成项、接线需求；
- 提交文档并确认 `git status` 干净后停写（`writerStopped: true`）；
- 使用 `send_message_to_thread` 向主控线程发送唯一最终交接卡（严格单个 JSON，禁止任何前置或后置闲聊）。
