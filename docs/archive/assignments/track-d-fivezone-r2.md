# 任务书 D-Wave5-R2：右栏 tab 化工作视图 + 底栏终端 + 左栏动态状态

> 前置：R1 已合并（main@616fcdc，五区骨架/左栏/中央就位）。开工前先 `git merge main`。
> 依据：docs/design/frontend-design-guideline.md §3（右栏）§4（底栏）§1.4 缺口清单、H-S06/S07/S08/S09、C:\Projects\Fufucode\tmp\codex-atlas\（尤其 02-changes-card.png）、docs/decisions.md 双轴。

## 范围

### 1. 右栏：tab 化工作视图（guideline §3，H-S06/S07）

- tab 条 + 至少 Files / Changes 两个可用 tab（Review/Preview 骨架位可后置，列出即可）；
- **Changes tab 对接 A8 契约**：消费 Core 的 `ChangeRangeQuery`（workspace/session 双口径，DTO 见 docs/design/cli-design.md A8 章节）——文件列表行=路径+new/deleted/binary 标记，参照图集 02 的加减行数着色样式；Core 缺的字段（如每文件 ±行数统计）列「交 A 清单」，不自己写 Core；
- 右栏宽/窄行为与分割柄按 H-S07；中央选文件后的联动本波可不做（记缺口）；
- R1 删除的 ChangesPanelViewModel 按新契约重建（旧版已按 R1 处置删除，不复用其实现）。

### 2. 底栏：可加入的终端窗口（guideline §4，H-S08/S09）

- 唤起入口 + tab 条 + 创建/关闭/隐藏 + 拖高；重开保留输出；
- **技术选型先写后做**（progress-d.md 记录理由与风险，三选一）：①托管外部终端（HWND reparent，脆）；②Pseudo Console 自渲染（最重）；③WebView2+xterm.js（推荐评估起点）。Pty 驱动建议 ConPTY（P/Invoke），不开新 NuGet 依赖时可用现有包则列「交 A 清单」；
- agent 执行命令与交互终端的实例归属提示（H-S09）本波只做静态容器+基础收放，运行关系列缺口。

### 3. 左栏动态状态补齐（R1 缺口）

- 会话行状态 glyph（运行中/失败/未读）+ 行尾动态菜单位；组织/排序菜单可后置记缺口；
- 状态数据源对接 Core 既有会话状态（不新增契约）。

## 验收

- build 0 警告 0 错误；selftest 0 失败；无头截图存 `tmp/w5r2/`（右栏展开+底栏展开两个状态）；
- guideline §3/§4 逐组件已过/未过清单 + H-S06/S07/S08/S09 覆盖标注写 progress-d.md；
- Changes tab 用 mock 数据自证渲染路径（真实 Core 数据对接留到协调者终验，不假标）。

## 硬约束

- 只写 `src/Fufucode.App/` 与 `docs/archive/progress/progress-d.md`；Core/Pty 依赖缺口列「交 A 清单」；
- 不复制 Codex/UniGetUI/ZCode 资产文案商标；终端区不做自研 shell；
- 禁 git commit/merge/push（merge main 同步除外）；可自由构建+selftest 迭代；
- 新 NuGet 包需要时先在 progress-d.md 列名与理由（协调者会核实许可与体积），不擅自安装。
