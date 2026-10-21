# Fluent 暗色用色系统收敛

日期：2026-10-21。范围：Fluent 皮肤暗色模式（当前默认）。

## 结果

- 新增 [color-principles.md](../design/color-principles.md)：三档底色、主题色、模型输出强调色。
- 三档底色各只给三个备选：
  - 窗口：深灰 / 更深 / 最深
  - 一般栏：浅灰 / 更浅 / 更亮
  - 高亮：不太亮 / 高亮 / 更亮
- 主题色（`Accent`）只服务 UI 通用颜色；强调色（`Emphasis`，默认 `#FFD400`）只用于模型输出 Markdown 粗体。
- Fluent 暗色 surface 键收敛到 `AppBaseDeep/AppBaseBar/AppBaseHighlight`；运行时由 `MainWindow.ApplyFluentColorOverrides` 覆盖。
- 主题色/强调色可任选；链接、侧栏 accent 跟随主题色。
- 图标、diff 红绿、代码/终端、状态色、QR 固定白底黑字保留为已声明例外。

## 改动

- `WorkbenchAppearance`：新增 `BaseBackground/BaseBar/BaseHighlight/Emphasis` 与三组备选。
- `FufucodeTheme` / `FluentTokens` / `MicaOverrides`：Fluent 暗色 surface 收敛到三档底色；新增 `AppEmphasis`。
- `MarkdownTextBlock`：`**strong**` 使用 `AppEmphasis`。
- 设置外观页：主题色、自定义主题色、强调色、三档底色选择。
- `docs/settings-design-spec.md`、`docs/design/project-principles.md`、`docs/README.md` 已接入原则。

## 验证

- `dotnet build Fufucode.sln -c Debug --no-restore -p:UseAppHost=false -p:OutDir=<isolated>`：41 项目，0 错误，0 警告。
- `Fufucode.App.Settings.Tests`：122 项断言全部通过。
- `Fufucode.App --selftest --r05-only`：0 项失败，含主题色、暗色 Fluent、状态栏与设置页渲染。

未做：真实显示器上的视觉验收、非 Fluent 皮肤改造、代码语法高亮（尚未实现）。
