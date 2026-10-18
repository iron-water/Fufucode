# CONV-2 交付记录：AUTO-OPERATE 纵向包（v4 自动工作台页面）

日期：2026-10-15。任务：`docs/tasks/conversation-workbench-v4.md` 的 AUTO-OPERATE 包。
工作树：`C:/Projects/Fucode/worktrees/conv2-auto-operate`，分支 `agent/conv2-ui`。

## 事实状态

- 状态：`module_ready`（模块可独立运行与验证；共享入口/组合根接线与产品验收未完成）。
- 起点 HEAD：`0e5fd05eb4a0c847814b7978a01cdfa19410300e`。
- 本包只改 `src/Fufucode.App/Features/Automation/**`、`src/Fufucode.App/SelfTest/Automation*`、
  `tests/Fufucode.App.Automation.V4.Tests/**`、`Fufucode.sln` 与本文档；未改 Core、共享壳、设置页或他人文件。
- 未推送、未改写历史、未创建 main 集成、未删除任何工作树。

## 已实现

1. **v4 目录投影层（纯只读）** `Features/Automation/AutomationControlProjection.cs`
   - `AutomationV4Projection`：SHA/工作树/运行时长/时间戳格式化，以及交付-接受-整合-验证四态文案。
   - 观测缺失一律显示“未知”，不用空串伪装成已知值；项目入口策略默认自动但只在首次进入时标记。
2. **四页签原生工作台** `AutomationControlPageView.axaml` + `AutomationControlViewModel.V4.cs`
   - **总览**：主控对话（消息含已保存/已投递/处理中生命周期文案与真实时间）、右侧停靠“正在工作 / 需要你决定 / 最近成果”，
     顶部“当前安排”条由宿主快照推导，不作前端猜测。
   - **执行线程**：列表 / 依赖图双视角切换；列表卡片展示真实后端、模型、工作树、起点/当前 SHA、最后事件、停顿原因与预计下一动作；
     依赖图保留线程节点及内部任务/成果端口，画布由 `AutomationControlPageView.axaml.cs` 按真实快照绘制。
   - **成果与验收**：按交付部分展示已提交/已接受/已整合/已验证/未验收，含提交、运行、证据、接线需求与审核结论；
     旧版本标注“已过期”，终验面板按目标列缺口并明确“发布是另一个获授权动作”。
   - **活动记录**：活动时间线、本地草稿与原回执诊断区、运行健康、既有待办决定区与取消对账区。
   - **委托与预算抽屉**：默认并发 2、自动审核/集成、实现/验证模型、联网与付费开关、周期本地补漏；预算未配置时
     明确写“未配置显式上限”，不编造价格或余额；越权与普通工具审批分开说明。设置按项目落盘到
     `automation-v4-delegation.json`（核心库目录内），真实读回。
   - **旧版“全人工默认填表”不再是入口**：连接数据库只作为显式“连接工作台…”动作；任务规范/知识/关系等表单移到活动记录诊断区。
   - **响应式**：窄于 1000DIP 时主操作行下移、单列堆叠，不横向溢出；依赖图仍在自己的滚动视口内保持 260 宽节点。
3. **新项目默认自动、旧状态不被覆盖** `Features/Automation/AutomationProjectPolicy.cs`
   - `AutomationEntryPolicy`：首次进入→“自动开发（默认）”；缺后端→“需配置执行后端”且不启动空模型；
     已有项目缺目标→“等待目标”；其余→“按原模式继续”并明示“不被默认变更覆盖”。
   - `AutomationWorkspaceMarker`：逐项目只读标记，仅是本模块自己的文件，不写入 U1 数据库，不改任何已有模式、暂停或在途身份；
     读不到标记时按“已见过”处理，宁可沿用旧状态也不把老项目当新项目重新默认。
4. **自动模式入口如实呈现核心结论**：页面提供“暂停新任务/继续（自动模式）”入口，提交核心 `SetMode(Automatic)`；
   Core 目前仍拒绝该策略时，界面显示核心真实原因 `Automatic policy is not enabled.`，不伪造切换成功、不删除异常。

## 实际验证

| 验证 | 命令 | 结果 |
|---|---|---|
| 解决方案构建 | `dotnet build Fufucode.sln -v q --nologo` | 0 个警告 / 0 个错误 |
| 新定向测试 | `dotnet run --project tests/Fufucode.App.Automation.V4.Tests` | 59 项断言通过 |
| 既有持久化定向测试 | `dotnet run --project tests/Fufucode.App.Automation.Persistence.Tests` | 192 项断言通过（无回归） |
| 原生工作台无头自测 | `dotnet run --project src/Fufucode.App -- --selftest --automation-only` | `Automation page failures: 0` |
| 差异白空格 | `git diff --check` | 无输出、退出 0 |

### 新定向测试覆盖（真实临时 SQLite + 真实 Git + 真实后台宿主与子进程）

- 前置：真实 `BackgroundHost` 独占槽位，真实子进程执行并停写，注册交付进入可审队列 → 真实审核 → 真实显式授权 → 真实集成标记。
- 状态投影：连接真实库后线程/成果/审核结论/整合/摘要全部来自真实记录；未连接与连接失败时投影保持为空并明确未知。
- 双视角切换：页签切换、列表默认、图/列表互斥、视角文本、切换不改数据。
- 自动委托抽屉：默认并发 2 与最低成本模型、范围/预算/权限文案、真实落盘与按项目读回、可收起。
- 主控输入：提交后清空、不产生核心命令与版本变化、同项目切换保留未发送草稿。
- 默认策略：新项目默认自动、未给目标不空转、缺后端显示需配置、已有项目沿用原模式且不改核心状态。
- 核心拒绝：`Automatic policy is not enabled.` 被如实显示且模式保持半自动不变。
- 失败路径：连接不存在的库如实报错，投影不保留伪造数据。

### 未执行 / 未验证

- 原生真机 UI 验收（125%/150% DPI、中文 IME 实际输入、键盘返回、弹出层、800×640 与 1100×800 实机截图审阅）
  仅做到无头自测与两档宽度渲染截图；**未做人工视觉验收**。
- 主控对话目标→核心目标版本写入、自动规划/派工/集成闭环属于 AUTO-LOOP 与共享接线，**本包未实现也未验证**。
- 页面写命令统一由宿主执行、GUI 与 CLI 单一权威写入者、后台/登录自启生命周期：**未验证**。
- 旧 HTML/脚本入口退役、生产队列迁移：**未执行**。

## 公共接线需求（交给主控）

1. `AutomationControlViewModel.RebuildProjection()`/`ApplyEntryPolicy(dataRoot)` 当前由页面在连接后调用；
   组合根若要提供项目级宿主快照，应在 `MainWindow.Workspace.cs` 的 automation 模块构造后调用 `ApplyEntryPolicy`。
2. 主控目标入口（提交目标→核心目标版本）尚未存在于 Core 公共契约；页面当前只保存请求并明确显示“未产生核心命令”。
3. `SetMode(Automatic)` 仍被 `AutomationStore.Control.cs` 拒绝；AUTO-LOOP 落地后本页即可真实启用自动模式。
4. `AutomationWorkspaceMarker` 与委托设置文件默认写入 `%LOCALAPPDATA%/Fufucode`（自测用 `Program.SelfTestDataRoot`）；
   若要并入项目级设置，需主控指定权威路径。

## 交接

- 真实 HEAD 与提交见最终交付卡；工作树保留原样供主控独立复核。
- `stoppedWriting=true`：本包所有编辑、Git 与构建进程已结束，不再修改工作树。
