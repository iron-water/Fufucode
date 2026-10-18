# 任务书 D-Wave5-R：五区前端重做（R1 骨架+左栏+中央）

> 派发：协调者 → 用户在 Codex Desktop 新建线程（建议命名 track d）粘贴 prompt 执行。
> worktree：`C:\Users\21894\.codex\worktrees\track-d-app\Fufucode`，分支 track-d。
> 前置已同步：H 五区规划（main@99af3a5）、A8 契约、双轴定案均已入 main；驱动器已把 main merge 进本 worktree。
> 后续波：R2（右栏+底栏）、R3（设置+跨区）在 R1 合并后另派，执行者可换。

## 权威依据（按序读）

1. `docs/design/frontend-design-guideline.md` —— 五区骨架唯一布局权威（§0 总览/§1 左栏/§2 中央，R1 相关），含组件清单与 H-S04/S05 场景锚点。
2. `docs/decisions.md` —— 双轴：功能对齐 Codex Desktop，审美沿 UniGetUI。
3. `C:\Projects\Fufucode\tmp\codex-atlas\` —— Codex 实机参照图集（00 全局/01 侧栏/02 变更卡/03 输入区）。
4. `docs/research/UNIGETUI_DESIGN_STUDY.md` —— 视觉规范延续（动画/高亮/材质/图标）。

## 半成品处置（先做，记录进 progress-d.md）

worktree 现存上轮中止的 6 个未交付文件：`CommandPaletteViewModel.cs`、`ChangesPanelViewModel.cs`、`NotificationService.cs`（新增）、`SidebarViewModel.cs`、`StatusBarViewModel.cs`、`MainWindow.axaml.cs`（修改）。逐个对照五区规格评估：符合 R1/R2/R3 对应区规格的保留改造（如 ChangesPanel 归右栏 Changes tab、StatusBar 用量口径对接 A8 usage 契约），不符合的删除。处置清单（留/改/删+理由）写进 progress-d.md。

## R1 范围

1. **五区骨架**落 `MainWindow`：左栏/中央/右栏（本波仅骨架位，可收起）/底栏（本波默认隐藏，仅留容器挂点）/设置保持独立界面入口。骨架分区尺寸与分割行为对照 guideline §0 与图集 00。
2. **左栏**（§1）：任务列表与一般选项，组件清单逐项对齐，悬停/选中/焦点沿用 Wave4 令牌。
3. **中央**（§2）：对话区完整形态——会话头、活动块层级（用户/助手/工具/思考）、composer 各状态（H-S04/S05）；排队/steer 时机反馈与草稿恢复提示按 §2 缺口清单补。
4. 本波不做右栏内容、底栏终端实现、设置独立界面（R2/R3）。

## 验收（本波自证 + 协调者终验）

- `dotnet build` 0 警告 0 错误；`--selftest` 0 失败；selftest 无头截图存 `tmp/w5r1/` 作为布局证据；
- 对照图集 00/01/03 与 guideline §1/§2 逐组件列已过/未过清单（写 progress-d.md）；
- H-S04/S05 逐条标注覆盖状态；做不到的项写明原因，不假标。

## 硬约束

- 只写 `src/Fufucode.App/` 与 `docs/archive/progress/progress-d.md`；Core/CLI 缺契约列「交 A 清单」不自己写；
- 不复制 Codex/UniGetUI/ZCode 资产文案商标；
- 禁 git commit/merge/push（`git merge main` 分支同步除外）；改动留 worktree 由协调者代验代提；
- 桌面执行者允许构建与运行 selftest；如需 Codex 实机对照，用后台 UIA/截图，不抢用户前台。
