# 轨道 G 任务书：Fufucode 图像与品牌资产

> 执行者：codex。本任务产出**身份级视觉资产**——应用图标、品牌标识、空态插画、安装器素材。小型 UI 图形符号不在范围（已由 Fluent 图标字体解决）。开工前 `git merge main`，通读 `AGENTS.md` 与本文件。

## 设计方向（用户已定，无需征询）

**最大程度贴近 UniGetUI 的视觉语言**，并以 Codex desktop 的产品设计为辅助参考。前任 agent 已整理架构与 UI 模式层面的研究（`docs/research/UNIGETUI_DESIGN_STUDY.md`）——你的第一项工作是**接着它做资产维度的延伸研究**：

- 研读 `reference/UniGetUI` 中图标与安装器资产的构成（图标源文件、`UniGetUI.iss` 引用的向导图、色板与渐变手法），产出资产设计笔记到 `docs/research/design-notes-g.md`（本轨独占）：图标构图语言、配色与渐变、空态插画的风格、安装器图形规格。
- 研读 Codex desktop 的产品视觉（公开可得范围内），提炼可借鉴的点。
- **红线**：贴近的是风格、构图与气质，**不得复制 reference/UniGetUI 的任何美术资产文件**（GPL 许可与原创性）；所有产出必须原创或注明来源许可，AI 生成注明工具。

## 文件范围（只允许写这些）

- `assets/`（新建，本轨独占）
- `docs/archive/progress/progress-g.md`（自建）、`docs/research/design-notes-g.md`（自建）

**不碰** `src/`（接线是轨道 D 的事）、`scripts/`、`reference/`（只读）。

## 生产清单（研究笔记就绪后即可开工，按此顺序）

1. **应用图标**：贴近 UniGetUI 图标气质的原创设计——Windows 多尺寸 `.ico`（16/24/32/48/64/128/256）、SVG 源文件、PNG 导出（亮/暗预览）。
2. **空态插画**：对话空态、扩展空态、错误/中断态，SVG + PNG（@1x/@2x），亮/暗变体。当前占位可用 mock 模式实看：`dotnet run --project src/Fufucode.App -- --mock`（无需 Pi 与凭据）。
3. **安装器素材**（消费方：轨道 F，M7）：Inno Setup 向导侧图与横幅（标准尺寸）、安装完成页图形，规格对齐 UniGetUI 的安装器图形做法。
4. **窗口/任务栏小尺寸变体**：与图标一致。

## 技术与协作约束

- 全部资产登记进 `assets/manifest.md`（路径/尺寸/用途/消费方/来源声明）；SVG 优先，PNG 为导出产物。
- 风格与轨道 D 已落地的 Fluent 视觉基座（H2）协调：先读 `docs/archive/assignments/track-d-m5.md`、`docs/decisions.md` [轨道D]；用 mock 模式实看当前界面。
- 消费流：**你只生产文件**——接线（csproj/axaml）由轨道 D 做，安装器消费由轨道 F 做。反向请求流：检索 `docs/archive/progress/progress-d.md`/`../progress/progress-f.md` 中的"交轨道 G：<需求>"。
- 交付说明写入 progress-g.md：每批列出"哪个文件 → 建议接在哪里"。

## 验收

- 设计笔记完成且被用户认可（"接近 UniGetUI"由用户裁决）。
- manifest 完整、来源声明齐全；SVG 源文件与导出一一对应。
- 经轨道 D 接线、用户实看后无原则性异议。

## 诚信规则（同 AGENTS.md）

- AI 生成注明生成工具与提示词要点；含第三方元素必须声明许可。
- 未完成项如实记录，不虚构交付。

## 本轮派单（2026-09-30，协调者）

在 track-g worktree（track-g 分支）执行；遵守派发纪律（不 git/不构建）。

**交轨道 G：自绘标题栏图标集**——轨道 D 的后续清单已将系统标题栏改为自绘（UniGetUI 做法）定为高优先。请产出标题栏三态（正常/悬停/按下）× 按钮（最小化/最大化/还原/关闭）图标，Fluent 线条风格，与 `docs/research/design-notes-g.md` 及已落地资产同一画笔语言；SVG 源 + 多倍率 PNG 导出，登记 `assets/manifest.md`，在 progress-g.md 写明建议接法（App 侧按 UniGetUI 的自绘 TitleBar 布局）。
**诚信**：只贴近 UniGetUI 风格，禁止复制其美术资产（GPL）。
