# FUI

全屏终端中的 FUMO 会话。它是一个独立可执行文件：不启动 OMP、不包装 CLI，也不需要打开桌面应用。模型和凭据沿用 Fufucode 设置；没有端点时，发送会报错，不会换用其他后端。

```powershell
fui                       # 当前项目，新会话
fui --continue            # 当前项目，最近一次 FUMO 会话
fui --project C:\work\demo --session <ID>
```

`Enter` 发送，`Alt+Enter` 或 `Ctrl+J` 换行；`PgUp` / `PgDn` 翻看记录，`Esc` 停止当前回合，`Ctrl+D` 退出。`Ctrl+Z` 撤销，`Ctrl+Shift+Z` 或 `Alt+Z` 重做，`Ctrl+Y` 粘贴最近删除的文本，`Alt+Y` 轮换删除历史。工具确认使用 `F2` 允许、`F3` 或 `Esc` 拒绝。按 `F1` 看完整按键与本地命令。长审批参数可在对话区翻阅。

## 在工作台里操作

`Ctrl+P` 打开可搜索的命令面板；方向键选项、`Enter` 确认、`Esc` 返回草稿。输入 `/` 可看到命令，`Tab` 补全。`//` 发送字面斜杠；`/skill:name` 仍由 FUMO 处理。

| 要做什么 | 操作 |
|---|---|
| 管理会话 | `/new` 新建；`/resume` 搜索同项目 FUMO 会话；`/tree` 按父子分支树展开、折叠与搜索后恢复；`/rename 名称` 改名；`/fork` 从选中的用户消息分叉；`/export [路径]` 导出 Markdown，不覆盖已有文件。切换后可找回本次打开期间原会话的未发草稿与附件。 |
| 切模型、查看用量 | `/model` 从实际端点目录选择；`/thinking` 选当前模型可请求的等级；`/usage` 显示历史中报告的数量，未知不按字数估算。`/skills` 填入技能调用；`/capabilities` 浏览已注册能力，不执行工具。 |
| 在运行中补充输入 | `/steer 文本` 插话，`/queue 文本` 追加下一轮，`/queue` 查看队列。若正在写草稿，可从命令面板选插话或跟进，再按 Enter 提交。 |
| 查看子代理活动 | 状态栏显示运行中的子代理数量；`/agents` 列出 FUMO 实际上报的作业、状态和工具计数，选择后查看只读事件记录。插件未启用或没有事件时显示空态，不伪造实时转录。 |
| 浏览项目和带入上下文 | `/preview` 选择 UTF-8 文件，只在本机显示；`/diff` 查看 Git 文本差异。`/attach [相对路径]` 或在草稿内输入 `@路径前缀` 再按 `Tab`，选择 UTF-8 文本或 PNG/JPEG 图片附到下一条消息（允许纯图发送）；`/context` 查看，`/detach` 移除。每回合最多 4 个附件、合计 10 MiB，单个文本文件最多 64 KiB；发送前复核内容、安全规则和当前模型的图片输入能力，文件已改或模型不支持图片时会拒绝发送并保留草稿。 |
| 阅读和找回输入 | `Ctrl+F` 或 `/find` 搜索并跳到对话里的消息；`Ctrl+R` 或 `/history` 找回输入但不会自动发送。`/tools` 展开/折叠工具，`/thoughts` 显示/隐藏思考。`Ctrl+U` / `Ctrl+K` / `Ctrl+W` 删除至行首/尾/前一词并写入 kill ring。 |

### Markdown 呈现

FUI 把终端 Markdown 作为只读呈现层：GFM 风格表格按显示宽度对齐，支持左/中/右列，中文与 emoji 不按字符数计算；`[文字](URL)` 显示为 `文字 (URL)`，引用和列表有可读前缀。未闭合代码围栏内的管道和链接保持原文，表格在流式行未完成时使用已锁定列宽，避免已画内容抖动。不执行链接、不发射 OSC 8；嵌套表格、链接标题和其他完整 GFM 能力尚未实现。原始消息和历史文件不被改写。

### LaTeX 公式

在支持 SIXEL 的终端中，FUMO 回复里的公式会按数学字体排版成图。`$E=mc^2$` 和 `\(E=mc^2\)` 随正文显示。独立公式可用 `$$…$$`、`\[…\]` 或标为 `math`、`latex` 的围栏。比如让 FUMO 解释下面的求根公式。

```latex
\[
x = \frac{-b \pm \sqrt{b^2-4ac}}{2a}
\]
```

公式背景透明。FUI 启动时查询 SIXEL 能力、字符格像素大小及前景色、背景色，按当前配色生成字形和抗锯齿边缘。终端未报告颜色时采用白色字形。仅支持文字的终端显示公式源码。已知终端支持 SIXEL 但没有响应探测时，可在启动前设置 `$env:FUI_GRAPHICS = 'sixel'`；设为 `'off'` 可查看原文。普通代码围栏、行内代码、草稿、搜索和 Markdown 导出都保留原始 LaTeX。无法排版、超过 512 个字符或窗口过窄的公式也显示原文。

例如，先输入 `请解释 @src/`，按 `Tab` 选中源码文件，确认状态栏出现「附件 1」后按 `Enter`。FUI 只附上选中的文件，不扫描并发送整个项目。

## 审批与后台操作

回合运行时可以搜索会话和模型目录、预览文件、查看 Git 差异或查用量。要切换会话、模型或思考等级，须等当前回合结束。声明了 Git 内容过滤器的文件会在差异预览中说明原因。

工具请求审批时，FUI 收起选择器，停止当前后台预览。`F2` 允许屏幕上这一个请求，`F3` 或 `Esc` 拒绝，`Ctrl+C` 停止当前回合。窗口只能显示按键或工具名时，放大终端，显示请求参数后才能允许。长参数可以用 `PgUp`、`PgDn` 在对话中查看。审批关闭后，未发送的文字和附件仍可编辑。

带附件按 `Enter` 后先在本机复核文件。复核期间按 `Esc`、编辑草稿或遇到工具审批，都会取消这次发送。再次按 `Enter` 才会重新提交。

`--app-data <目录>` 可隔离设置和会话，也可设 `FUFUCODE_DATA_ROOT`。会话 ID 可在 `/session` 查看，或用同一项目、同一数据根的 `fufucode sessions list --json` 查询。管道、批处理、设置管理和 JSON 输出仍使用底层 CLI `fufucode`。

从仓库根开发运行：

```powershell
dotnet run --project src/Fufucode.Fui -- --project C:\work\demo
dotnet run --project tests/Fufucode.Fui.Tests
pwsh scripts/packaging/publish-fui.ps1          # 可选，自包含，不改 App/CLI 安装
```

默认 Windows 发布位置为 `out/fui/win-x64/fui.exe`。复制这个文件即可单独安装 FUI；无需复制调试符号。需要可交互的 VT 终端，建议 Windows Terminal。
