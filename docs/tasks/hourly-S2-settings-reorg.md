# S2-SETTINGS 任务书：设置面板三级架构重构 & 侧栏条目治理 (D2 + D3)

难度：L3 ｜ 推荐模型：`group/auto-deepseek-v4-1-flash`（显式 max 思考强度）或 `group/auto-hy4-preview-f`
任务类型：受管特种兵独立功能包 ｜ 起点 HEAD：`043fd2c37a3c7f817781009f4b881f9513efeb1d`
工作树绝对路径：`C:/Projects/Fucode/worktrees/s2-settings`
分支：`agent/s2-settings`

## 1. 允许修改路径
- `src/Fufucode.App/Views/SettingsPage*`
- `src/Fufucode.App/Views/WorkbenchSectionsView*`
- `src/Fufucode.App/ViewModels/SettingsPageViewModel*`
- `src/Fufucode.App/Features/SettingsExperience/**`（新建辅助视图与模型）
- `tests/Fufucode.App.Settings.Tests/**`（新建独立测试工程）
- `docs/records/S2-settings-reorg.md`（本任务记录文档）

## 2. 严禁触碰路径
- 严禁修改 `MainWindow.Workspace.cs`（M 统一负责主窗口与侧栏接线）。

## 3. 功能目标与验收标准
1. **设置左侧栏三级架构 (D2)**：
   - 一级【基础类】：外观（appearance）、关于（about）、网络（network）、键盘（shortcuts）、模型（models）、储存（storage）；
   - 一级【大功能】：浏览器（browser）、终端（terminal）、文件管理（files）、自动开发（automation）；
   - 一级【其他】：作为一个独立的一级分类存在，内部容纳所有二级小功能配置（通知、配置、用量、辅助工具等）；
   - 交互式升降级：支持在设置中将二级小功能“升级”到一级侧栏，也可将大功能“降级”收纳进“其他”，层级偏好持久化保存在配置中；
   - 关联跳转：各设置页底部提供“相关设置”卡片，点击平滑跳转到对应关联设置页。
2. **侧栏条目治理 (D3)**：
   - 通知、用量等小功能默认退出侧栏，保持侧边栏主路径极简；
   - 梳理“文件预览”页签，准备内嵌预览能力协议。
3. **独立单元测试**：
   - 编写 `tests/Fufucode.App.Settings.Tests` 验证：
     1. 三级架构分类与层级正确解析；
     2. 小功能升级到一级、大功能降级到“其他”的持久化存取与恢复；
     3. 相关设置跳转命令流转；
     4. 搜索框支持跨层级搜索过滤。
   - 构建 `dotnet build tests/Fufucode.App.Settings.Tests` 达到 0 警告、0 错误。
   - 延后 App 全量 1480 断言与原生挂机测试（按 v3 战略）。

## 4. 人类授权与交付回传协议
- 本任务获用户直接授权：
  > 授权来源：主agent (提问) 01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07
  > 用户原文：“我明确授权：你向本项目受管执行会话派单、返工和协调；这些执行会话向你所在的主控线程主动回传交付、阻塞和协调消息。派单时附本授权原文和真实主控threadId。”
  > 主控线程 ID：`01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07`
- 任务收尾时必须在 `docs/records/S2-settings-reorg.md` 中完整记录：状态、真实 HEAD、改动、测试结果、未完成项、接线需求；
- 提交文档并确认 `git status` 干净后停写（`writerStopped: true`）；
- 使用 `send_message_to_thread` 向主控线程发送唯一最终交接卡（严格单个 JSON，禁止任何闲聊文字）。
