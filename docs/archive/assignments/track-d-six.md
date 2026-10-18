# 任务书 D6：窄宽重叠修复 + PreviewFile 接线（线程无关版）

> 背景：原 track d 线程所在 Codex Desktop 会话文件已丢失。本文件使任何 AI 执行者可脱线接手。
> worktree：`C:\Users\21894\.codex\worktrees\track-d-app\Fufucode`（分支 track-d）。**开工先 `git merge main`**。
> 前置已合并：R1-R4 全部（五区总管线完成）、E1/E2（浏览器轨道）、A13 契约包、D6前置（568bcd3，Preview tab 已点亮）。

## 协调者批复（原 track d 线程挂起事项）

D 在 R4 实机自验后提出：给 `<views:BrowserView>` 加 x:Name 并在 code-behind 接线预览调用，超出其当时两文件边界，申请纳入。**已批准**：
- `src/Fufucode.App/Views/WorkspacePanelView.axaml.cs` 纳入本轮可改文件；
- x:Name 已由 E 侧契约固定为 `PreviewBrowser`（`BrowserView.PreviewControlName` 常量，E2 常驻自测断言依赖此名）——seam 上已存在，**不要改名、不要删**；
- 接线方式按 D 自己的方案：订阅面板选中变更/双击，调用 `previewBrowser.PreviewFile(file.FullPath)`；VM 侧零改动。

## 任务范围

1. **D-R4-1 窄宽重叠修复**：R4 实机自验发现——窗口宽约 1024 且右栏抽屉模式下，右栏与 composer、标题栏动作区重叠。修复布局约束（断点/抽屉切换时各区不互相覆盖），无头截图取证（正常宽/窄宽/抽屉展开三态）。
2. **PreviewFile 接线**：Files tab 选中文件/双击 → E1 内置浏览器预览态。走上述已批边界；`BrowserView.TryPreviewFile(panel, path)` 也可用（E 侧公共入口，二选一，选型理由写 progress）。
3. 完成后：`dotnet build` 0 警告 0 错误、`--selftest` 0 失败、截图存 `tmp/w5d6/`、guideline 对照增量记录——全部如实写进 `docs/archive/progress/progress-d.md`（新增 D6 节）。

## 必读

`docs/archive/progress/progress-d.md` 尾部『阻塞』节（D 自己的分析）→ `docs/design/frontend-design-guideline.md` §3（右栏）→ `docs/decisions.md`（双轴）→ `docs/archive/progress/progress-e.md`（E 侧契约细节）。

## 硬约束

只写 `src/Fufucode.App/` 与 `docs/archive/progress/progress-d.md`；Core/CLI 缺口列「交 A 清单」；不复制 Codex/UniGetUI/ZCode 资产文案商标；禁 git commit/merge/push（`git merge main` 同步除外）；改动留 worktree 由协调者代验代提；未实测不标通过。
