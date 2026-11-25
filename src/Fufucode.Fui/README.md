# FUI

全屏终端中的 FUMO 会话。它是一个独立可执行文件：不启动 OMP、不包装 CLI，也不需要打开桌面应用。模型和凭据沿用 Fufucode 设置；没有端点时，发送会报错，不会换用其他后端。

```powershell
fui                       # 当前项目，新会话
fui --continue            # 当前项目，最近一次 FUMO 会话
fui --project C:\work\demo --session <ID>
```

`Enter` 发送，`Alt+Enter` 或 `Ctrl+J` 换行；`PgUp` / `PgDn` 翻看记录，`Esc` 停止当前回合，`Ctrl+D` 退出。`Alt+V` 粘贴剪贴板图片。`Ctrl+Z` 撤销，`Ctrl+Shift+Z` 或 `Alt+Z` 重做，`Ctrl+Y` 粘贴最近删除的文本，`Alt+Y` 轮换删除历史。工具确认使用 `F2` 允许、`F3` 或 `Esc` 拒绝。按 `F1` 看完整按键与本地命令；四个安全动作可在 `/keys` 中重绑。长审批参数可在对话区翻阅。

## 在工作台里操作

`Ctrl+P` 打开可搜索的命令面板（可在 `/keys` 改键）；方向键选项、`Enter` 确认、`Esc` 返回草稿。输入 `/` 可看到命令，`Tab` 补全。`//` 发送字面斜杠；`/skill:name` 仍由 FUMO 处理。宽度至少 80 列时，选择器显示左右双栏：左侧搜索和选择，右侧查看当前项的详情；窄窗口自动回到单栏。

| 要做什么 | 操作 |
|---|---|
| 管理会话 | `/new` 新建；`/resume` 搜索同项目 FUMO 会话；`/tree` 按父子分支树展开、折叠与搜索后恢复；`/rename 名称` 改名；`/fork` 从选中的用户消息分叉；`/export [路径]` 导出 Markdown，不覆盖已有文件。切换后可找回本次打开期间原会话的未发草稿与附件。 |
| 切模型、查看用量 | `/model` 从实际端点目录选择；`/thinking` 选当前模型可请求的等级；`/usage` 显示历史中报告的数量，未知不按字数估算。`/skills` 填入技能调用；`/capabilities` 浏览已注册能力，不执行工具。 |
| 在运行中补充输入 | `/steer 文本` 插话，`/queue 文本` 追加下一轮，`/queue` 查看队列。若正在写草稿，可从命令面板选插话或跟进，再按 Enter 提交。 |
| 查看子代理活动 | 状态栏显示运行中的子代理数量；`/agents` 列出 FUMO 实际上报的作业、状态和工具计数，选择后查看只读事件记录。插件未启用或没有事件时显示空态，不伪造实时转录。 |
| 浏览项目和带入上下文 | `/preview` 在本机直接预览 UTF-8 文本或图片（支持 SIXEL 直接渲染）；`/diff` 查看 Git 文本差异。`/attach [相对路径]` 或在草稿内输入 `@路径前缀` 再按 `Tab`，选择 UTF-8 文本或图片附到下一条消息（允许纯图发送）；`/paste-image`（`/paste` 为别名）或 `Alt+V` 直接粘贴 Windows 剪贴板图片；`/context` 查看并预览待发图片缩略图，`/detach` 移除。每回合最多 4 个附件、合计 10 MiB，单个文本文件最多 64 KiB；发送前复核内容、安全规则和当前模型的图片输入能力，文件已改或模型不支持图片时会拒绝发送并保留草稿。 |
| 阅读和找回输入 | `/outline` 按用户、助手标题或摘要、工具、错误和审批生成大纲，搜索后跳到原文；`/transcript` 用 `Tab` 切换全部/用户/助手/工具/错误/审批，宽屏双栏、窄屏单栏，`Enter` 定位；`/copy` 选择最近或任意助手回复、任意用户消息、当前或指定代码块、工具结果、URL 和会话 ID，并写入 Windows 文本剪贴板。`/links` 搜索回复中的链接目标并明确复制。`/info` 显示可证明的会话与用量事实，`/retry` 通过 FUMO 发送上一条用户文本并创建真实新回合。`Ctrl+F`（可改键）或 `/find` 快速搜索；`Ctrl+R` 或 `/history` 找回输入但不会自动发送。`Ctrl+O` / `Ctrl+T` 或 `/tools`、`/thoughts` 切换工具与思考呈现。`Ctrl+U` / `Ctrl+K` / `Ctrl+W` 删除至行首/尾/前一词并写入 kill ring。 |

大纲在流式输出期间按节流刷新，选项身份使用稳定行位置，追加内容不会把当前选择指向另一条消息。复制成功显示 Unicode 字符数和 UTF-8 字节数，失败显示系统剪贴板原因；复制是本地操作，不发送消息、不改会话。FUI 不发射 OSC 52：终端协议没有可靠完成回执，无法证明远端剪贴板真的写入，因此不在失败时伪装成功。

### Markdown 呈现

FUI 把终端 Markdown 作为只读呈现层：GFM 风格表格按显示宽度对齐，支持左/中/右列，中文与 emoji 不按字符数计算；`[文字](URL)` 显示为 `文字 (URL)`，引用和列表有可读前缀。代码围栏支持 C#、PowerShell、JSON、JavaScript/TypeScript、Python、Bash、Rust、Go、SQL 和 diff，并按真彩色、256 色、16 色和 `NO_COLOR` 降级。未闭合围栏保持已出现的代码行稳定，原始消息和历史文件不被改写。

`/links` 索引回复中的 Markdown 链接、标题链接、`www.` 与裸 HTTP(S) URL；选择后显示完整目标，再选择“复制 URL”写入 Windows 文本剪贴板，不自动打开外部站点。`/settings` 的 hyperlinks 策略为 `auto/on/off`；`auto` 只在终端能力已知时发射 OSC 8，`on` 强制尝试，`off` 或 `NO_COLOR` 只显示可读文字。链接和显示文本会过滤控制字符，非 HTTP(S) 目标不会获得 OSC 8。

Mermaid 围栏支持常见 `flowchart` / `graph` 的 `TD`、`TB`、`LR` 节点、`-->` 连线与条件标签；超出该子集时明确显示“此 Mermaid 子集不支持”并保留围栏源码。嵌套表格和其他完整 GFM/Mermaid 能力仍未实现。

## 设置与诊断

FUI 使用独立设置文件 `<data root>/fui/settings.json`，不读写 GUI 与底层 CLI 的 `settings.json`。文件损坏时回退默认值，在对话区和 `/doctor` 显示真实原因；下一次成功修改会保留 `.before-repair-*.bak`。写入使用同目录临时文件后替换。

`/settings` 提供分组、搜索、当前值与即时切换；`/theme`、`/terminal` 是快捷面板。可配置主题（auto/dark/light/mono）、思考与工具呈现、状态栏分段、紧凑卡片、滚轮、超链接、桌面通知，以及命令面板/搜索/工具/思考四个按键。冲突、输入编辑键和审批键会被拒绝，坏按键配置不会阻止启动。

`/keys` 展示并修改当前安全按键。`/doctor` 输出终端家族、尺寸、UTF-8/code page、bracketed paste、mouse、Sixel、颜色、hyperlinks、notifications、multiplexer、data root、FUMO shell 与 FUI build/version；未探测项明确写未知。tmux、Zellij、screen、Windows Terminal、kitty 等能力按实际宿主降级说明，不把 BEL 或复用器吞掉的 OSC 当成成功 toast。

回合完成或失败时，FUI 优先使用 Windows Terminal 的 OSC 777，或其他终端的 OSC 9（tmux 走 passthrough）；不支持文本通知时只发 BEL。可用 `FUI_NOTIFICATIONS=off`、`FUI_NO_NOTIFY=1` 或设置 `desktop-notifications=off` 关闭。通知失败不会改变回合结果。

### 图片直接渲染（输入与 AI 回复）

在支持 SIXEL 的终端（含 Windows Terminal 1.22+、xterm.js SIXEL 等）中，FUI 直接以透明背景、240 色中位切分 + Floyd-Steinberg 抖动的方式渲染位图与矢量图，无需离开终端：

- **输入与历史**：`/attach`、`@`、`/paste-image`（`/paste` 为别名）、`Alt+V`、`/preview` 与 `/context` 会立即显示缩略图或大图；已发送的用户消息及 `--continue` / `/resume` 恢复的会话历史（含原生 `.assets`）都会直接渲染对应图片。模型原生支持 `PNG` / `JPEG` / `WebP` / `GIF` 时按原样发送，其余已解码格式会在发送前自动无损转码为 `PNG`。
- **AI 回复与工具输出**：自动识别并渲染 Markdown `![alt](路径)`、HTML `<img src="...">` 以及内联 `data:image/...`（Base64 与 URL 编码 SVG）；流式输出未闭合的 `data:image/...` 会折叠为 `[图片加载中：…]`，不刷屏泄露原始 Base64。仅读取项目内且安全中心放行的路径；绝不发起远程 HTTP 拉取（`http://`/`https://` 显示 `[外链图片：…]`），越界路径、`.git`、符号链接和代码块内的图片语法保持原文。
- **支持格式**：
  - **内置光栅 / 矢量**：`PNG` / `APNG`、`JPEG`（含 EXIF 方向校正）、`WebP`、`GIF`、`BMP` / 无头 `DIB`、`ICO`、`CUR`、`WBMP`、`SVG` / `SVGZ`（加载前做 XML 安全审计：拒绝 `<!ENTITY`、`<script>`、`<foreignObject>`、`on*` 及外部 `href`/`src`/`url(...)`）。
  - **内置纯 C# 解码**：`QOI`、`TGA`（未压缩 / RLE、8/15/16/24/32 位）、`Netpbm`（`PBM`/`PGM`/`PPM`/`PNM`/`PAM`，P1–P7，8/16 位）、`Farbfeld`（`.ff`）、`Radiance HDR`（`.hdr`，未压缩 / RLE，Reinhard 色调映射）。
  - **Windows WIC 自动扩展**：内置 `TIFF`、`JPEG XR`（`.jxr/.wdp/.hdp`）、`DDS`，并自动接入系统已安装的 `HEIC`/`HEIF`、`AVIF`、`JPEG XL`（`.jxl`）、`DNG` 与相机 `RAW` 解码器。终端层固定走 SIXEL 静态帧输出（动画格式渲染首帧；高色深/HDR 自动色调映射为 8-bit sRGB 后量化）。

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

`--app-data <目录>` 可隔离 FUI 设置和会话，也可设 `FUFUCODE_DATA_ROOT`。会话 ID 可在 `/session` 查看，或用同一项目、同一数据根的 `fufucode sessions list --json` 查询。管道、批处理和 JSON 输出仍使用底层 CLI `fufucode`。

从仓库根开发运行：

```powershell
dotnet run --project src/Fufucode.Fui -- --project C:\work\demo
dotnet run --project tests/Fufucode.Fui.Tests
pwsh scripts/packaging/publish-fui.ps1          # 可选，自包含，不改 App/CLI 安装
```

默认 Windows 发布位置为 `out/fui/win-x64/fui.exe`。复制这个文件即可单独安装 FUI；无需复制调试符号。需要可交互的 VT 终端，建议 Windows Terminal。
