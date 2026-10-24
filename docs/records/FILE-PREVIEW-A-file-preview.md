# FILE-PREVIEW-A 文件面板预览第一阶段

## 状态

模块交付，已在 `feature/file-preview-power` 完成实现与定向验证；未合并 `main`，未推送。

## 实现

- 新增 `src/Fufucode.App/Features/FilePreview/`：预览类型/请求结果契约、provider 接口与 registry、`SafeFileReader`、统一 `FilePreviewHost`、内置文本/Markdown/Mermaid/结构化/CSV/图片/二进制 provider。
- `SafeFileReader` 统一执行项目根边界、逐级链接解析、大小上限、BOM/编码探测、取消和可读错误；SVG 拒绝脚本/外部资源，图片限制 64M 像素总量。
- `WorkspacePanelViewModel` 删除 `IsTextPreview`、`IsDiagramPreview`、`IsBrowserPreviewVisible` 手写分支，改持有统一 Host，并暴露类型、路径、大小、编码/格式、截断和错误元数据。
- Files 列表支持目录懒展开/折叠、相对路径过滤和 300 项上限提示；统一工具栏包含返回、前进/后退、查找/换行入口、复制路径、源码复制和系统打开。
- Mermaid 继续使用现有 `RichMarkdownView`，Markdown 保留代码块并传递相对图片基目录；JSON/XML/YAML 输出树；CSV/TSV 输出首行表头；二进制输出 Hex 与签名。
- 新增 `tests/Fufucode.App.FilePreview.Tests/`，覆盖 BOM、空/损坏文件、超大文件、路径越界、符号链接、JSON/CSV/Markdown 和取消。
- 更新 workbench/tabs/D6 无头自测，使其验证统一预览 Host 和懒加载目录契约。

## 实际验证

- `dotnet build Fufucode.sln --no-restore -v:minimal`：退出 0，52 项目，0 错误，5 个既有 Fufu XAML 资源 warning。
- `dotnet build tests/Fufucode.App.FilePreview.Tests/Fufucode.App.FilePreview.Tests.csproj --no-restore -v:minimal`：退出 0，0 错误，0 warning。
- `dotnet run --project tests/Fufucode.App.FilePreview.Tests/Fufucode.App.FilePreview.Tests.csproj -c Debug --no-restore`：11 项通过。
- `dotnet run --project src/Fufucode.App/Fufucode.App.csproj --no-restore -- --selftest --workbench-only`：0 失败。
- `--selftest --tabs-only`：0 失败。
- `--selftest --r2-only`：0 失败。
- `--selftest --d6-only`：0 失败。
- `--selftest --changes-shots-only`：0 失败；证据目录 `tmp/changes-shots`。

## 未运行/未验证

- 未运行全量 `--selftest`、`--r2-only` 以外的真实后端/模型链路验证。
- 尚未完成浏览器无头截图验收文本/Markdown/图片/JSON/CSV/Hex 六种预览的逐项截图；当前已有真实控件自测和 provider 单测。
- 当前“查找”和“换行”按钮已作为统一工具栏入口，文本内查找状态与换行偏好仍需后续增量接线。
- 图片缩放/平移/旋转/棋盘背景、CSV 真正虚拟化和 YAML 深层编辑式树交互仍为后续完善项；本阶段保持只读安全预览。

## 交付边界

- 不新增 NuGet 包，不上传文件内容，不执行 HTML/JS，不支持 PDF/Office/压缩包/音视频/Jupyter。
- 工作树保持，不合并 `main`，不 push，不自行删除工作树。
- 源码写入已停止；临时测试目录由自测清理。
