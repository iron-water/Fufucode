# K1-SKINS 任务书：全套皮肤深度视觉资产化与美化重构 (D1 + 前端风格.md)

难度：L3 ｜ 推荐模型：`group/auto-deepseek-v4-1-flash`（显式 max 思考强度）或 `group/auto-hy4-preview-f`
任务类型：受管特种兵独立功能包 ｜ 起点 HEAD：`2993c20032f840bfe0233774691c32961a07a1f6`
工作树绝对路径：`C:/Projects/Fucode/worktrees/k1-skins`
分支：`agent/k1-skins`

## 1. 允许修改路径
- `src/Fufucode.App/Styles/Skins/**`
- `src/Fufucode.App/Features/Skins/**`
- `tests/Fufucode.App.Skins.Tests/**`
- `docs/records/K1-skins-matrix.md`（本任务记录文档）

## 2. 严禁触碰路径
- 严禁修改 `MainWindow.Workspace.cs` 等共享外壳文件；外壳换肤租约机制已就绪。

## 3. 功能目标与验收标准
1. **全套独立视觉资产表 (D1 + 前端风格.md)**：
   - 覆盖全部 5 套风格：Fluent 2（Win11默认）、Metro（Win8平面直角大色块）、Material 3（胶囊形与色调分层）、Vista Aero（四段实色玻璃渐变+连体水晶按钮）、Luna（Windows XP 经典圆润与高对比）。
   - 为每套皮肤建立独立资产字典，**彻底剔除各非 Fluent 皮肤中的 Fluent 视觉残余**（如 Metro 强制直角零圆角、M3 强制药丸胶囊、Aero 强制双线高光实色反光）。
2. **消解反人类交互与堆砌文字说明**：
   - 彻底治理“到处堆砌文字说明”的低品味问题：收敛多余的长篇文字介绍，收入 ToolTip、悬浮气泡或折叠小帮助按钮中；页面保持留白与高级感。
3. **独立单元测试**：
   - 编写或扩展 `tests/Fufucode.App.Skins.Tests` 验证：
     1. 5 套皮肤目录解析完整且默认回退 Fluent 保真；
     2. 各皮肤独立资源字典加载与释放彻底（租约释放后不污染全局）；
     3. 非 Fluent 皮肤下的各控件特征断言（圆角、笔刷、渐变）；
     4. 连续多次热切换不积累冗余样式。
   - 构建 `dotnet build tests/Fufucode.App.Skins.Tests` 达到 0 警告、0 错误。
   - 延后 App 全量 1480 断言与原生挂机测试（按 v3 战略）。

## 4. 人类授权与交付回传协议
- 本任务获用户直接授权：
  > 授权来源：主agent (提问) 01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07
  > 用户原文：“我明确授权：你向本项目受管执行会话派单、返工和协调；这些执行会话向你所在的主控线程主动回传交付、阻塞和协调消息。派单时附本授权原文和真实主控threadId。”
  > 主控线程 ID：`01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07`
- 任务收尾时必须在 `docs/records/K1-skins-matrix.md` 中完整记录：状态、真实 HEAD、改动、测试结果、未完成项、接线需求；
- 提交文档并确认 `git status` 干净后停写（`writerStopped: true`）；
- 使用 `send_message_to_thread` 向主控线程发送唯一最终交接卡（严格单个 JSON，禁止任何前置或后置闲聊）。
