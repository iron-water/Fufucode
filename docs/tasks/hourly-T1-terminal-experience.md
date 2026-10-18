# T1-TERMINAL 任务书：底部终端深度体验优化 & ConPTY / IME 增强

难度：L3 ｜ 推荐模型：`group/auto-deepseek-v4-1-flash`（显式 max 思考强度）或 `group/auto-hy4-preview-f`
任务类型：受管特种兵独立功能包 ｜ 起点 HEAD：`c86254fecbe79c7bcd393c49141d51a37859385e`
工作树绝对路径：`C:/Projects/Fucode/worktrees/t1-terminal`
分支：`agent/t1-terminal`

## 1. 允许修改路径
- `src/Fufucode.App/Features/Terminal/**`
- `src/Fufucode.App/Views/Terminal*`
- `tests/Fufucode.App.Terminal.Tests/**`
- `docs/records/T1-terminal-experience.md`（本任务记录文档）

## 2. 严禁触碰路径
- 严禁修改 `MainWindow.Workspace.cs`、`MainWindow.axaml` 等共享外壳文件；外壳停靠已就绪。

## 3. 功能目标与验收标准
1. **解决首字符丢失问题（修复 echo -> cho 缺陷）**：
   - 彻底修复 R2 验收中暴露的“点击输入后立刻输入字符导致第一个字符丢失”的时序问题。在 `TerminalScreenControl` / 输入接收端确保焦点确立与字符捕获原子性同步，按键不丢首字。
2. **中文输入法（IME）无缝支持**：
   - 支持 Windows 原生 IME 候选框正常跟随光标，合成文本（Composition）未提交时在输入条内提供明确临时显示，提交（Commit）后完整追加到命令缓冲区，不出现乱码或字符分裂。
3. **剪贴板复制粘贴增强**：
   - 完整支持 Ctrl+C（有选区时复制选中终端文本到系统剪贴板，无选区时发送 SIGINT 中断信号）；
   - 支持 Ctrl+V / Shift+Insert（从系统剪贴板粘贴文本至光标处，处理换行安全）；
   - 右键弹出便捷菜单：复制（有选区时可用）、粘贴、清屏。
4. **命令历史与提示符平滑导航**：
   - 方向键上/下平滑切换历史执行过的命令，保持光标位置正确；
   - 提示符与当前输入区分明晰。
5. **验证纪律（v3 功能优先原则）**：
   - 编写或补充 `tests/Fufucode.App.Terminal.Tests` 验证：
     1. 首字符保护测试（模拟快速连续输入不丢字符）；
     2. 剪贴板复制/粘贴往返；
     3. 历史记录导航回填。
   - 构建 `dotnet build tests/Fufucode.App.Terminal.Tests` 达到 0 警告、0 错误。
   - **不执行** 1480 断言的 App 全量自测与长时间挂机测试（延后至里程碑收尾集中执行）。

## 4. 人类授权与交付回传协议
- 本任务获用户直接授权：
  > 授权来源：主agent (提问) 01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07
  > 用户原文：“我明确授权：你向本项目受管执行会话派单、返工和协调；这些执行会话向你所在的主控线程主动回传交付、阻塞和协调消息。派单时附本授权原文和真实主控threadId。”
  > 主控线程 ID：`01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07`
- 任务收尾时必须在 `docs/records/T1-terminal-experience.md` 中完整记录：状态、真实 HEAD、改动、测试结果、未完成项、接线需求；
- 提交文档并确认 `git status` 干净后停写（`writerStopped: true`）；
- 使用 `send_message_to_thread` 向主控线程发送唯一最终交接卡（严格单个 JSON，禁止任何前置或后置闲聊）。
