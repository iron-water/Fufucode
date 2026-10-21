# Fluent 暗色用色原则

日期：2026-10-21。适用范围：Fluent 皮肤暗色模式（当前默认）。其他皮肤有自己的主题体系，不套用本文的三档底色。

## 一、只有三类颜色

1. **底色三档**
   - `AppBaseDeep`：窗口/内容最底层深灰。
   - `AppBaseBar`：标题栏、侧栏、停靠区、卡片等一般栏的浅灰。
   - `AppBaseHighlight`：悬停、选中与高亮。
2. **主题色一种**
   - `AppAccent` / `SystemAccentColor`：UI 通用颜色，用于主按钮、选中指示、链接、用户气泡等。
3. **强调色一种**
   - `AppEmphasis`：**只用于模型输出中的 Markdown 强调标记**（当前是粗体），默认黄色。不得拿它当按钮、选中、链接或状态色。

## 二、每档底色只给三个备选

在“设置 → 外观 → 底色（Fluent 暗色）”中选择，不允许自由输入，避免把层级关系选反。

| 档位 | 备选 | 默认 |
|---|---|---|
| 窗口底色 | 深灰 `#1C1C1C` / 更深 `#161616` / 最深 `#101010` | 深灰 |
| 一般栏 | 浅灰 `#2C2C2C` / 更浅 `#333333` / 更亮 `#3A3A3A` | 浅灰 |
| 高亮 | 不太亮 `#333333` / 高亮 `#3A3A3A` / 更亮 `#484848` | 高亮 |

主题色与强调色可以任选，格式 `#RRGGBB`。

## 三、允许的其他颜色

以下不纳入三档底色，保留各自语义：

- 图标/分类色：`SemanticIcon`、`CategoryColor`、后端徽标色。
- 变更 diff：新增绿、删除红，以及对应的浅背景。
- 代码块/语法高亮：等宽代码区、自动高亮（尚未实现）。
- 终端：独立终端背景/前景与 ANSI 色。
- 状态：成功/警告/错误/信息色，以及二维码固定白底黑字。
- 文本层级：主文字/次文字/弱文字与边框发丝线。

## 四、实现约束

- 三档底色由 `WorkbenchAppearance.BaseBackground/BaseBar/BaseHighlight` 保存；`MainWindow.ApplyFluentColorOverrides` 在 Fluent 暗色下覆盖 `AppBase*` 与 Fluent surface 键。
- 主题色由 `WorkbenchAppearance.Accent` 保存，继续驱动 `SystemAccentColor` 与 `AppAccent*`。
- 强调色由 `WorkbenchAppearance.Emphasis` 保存，只映射 `AppEmphasis`；Markdown 粗体读取该 token。
- 禁止在 Fluent 暗色新增第四种不透明底色；新增 surface 必须落到 `AppBaseDeep/Bar/Highlight` 之一。
- 禁止用 `AppAccent` 表示模型输出强调，也禁止用 `AppEmphasis` 表示 UI 交互状态。
