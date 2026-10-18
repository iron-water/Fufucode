# 任务书 E1：内置浏览器（右栏 Preview tab 激活）

> worktree：`C:\Users\21894\.codex\worktrees\track-e-browser\Fufucode`，分支 track-e。开工前 `git merge main`。
> 所有权：只写 `src/Fufucode.App/Views/BrowserView*`（新文件）、`src/Fufucode.App/Infrastructure/` 下 Browser 相关新文件、csproj（NuGet 声明）、`docs/archive/progress/progress-e.md`；右栏注册只允许在 `WorkspacePanelView.axaml(.cs)` 加最小接线行（D 轨并行工作，该文件改动必须最小且只在标记处）。

## 范围

1. **WebView2 集成**：`Microsoft.Web.WebView2` NuGet（progress-e.md 记版本+许可证+体积；desktop 执行者可直接安装，协调者验证时核准）。runtime 检测：WebView2 Loader 缺失/旧版时给出可操作提示（下载 evergreen 链接），不崩。
2. **BrowserView**：地址栏+后退/前进/刷新+加载进度；两种模式——自由导航（输入 URL）与文件预览（接收本地路径：html/图片/txt/log/md 直接渲染，md 复用后续 R4 产出口径可用纯文本先行）。多 tab 可后置，单 tab 起步。
3. **右栏接线**：Preview tab 从禁用骨架激活为 BrowserView；Files/Changes tab 不动。tab 注册处加一行注释标记 `<!-- E-seam -->` 便于合并。
4. **安全边界**：不持久化浏览历史/cookie 到用户数据目录之外（默认 UserDataFolder 放 `%LocalAppData%\Fufucode\webview`）；下载走系统默认；不注入脚本到第三方页面。

## 验收

- build 0/0；selftest 0 失败——WebView2 运行时在无头 selftest 不可用的部分（真实导航/渲染）用 VM/骨架断言覆盖，真实交互列「用户实测清单」如实标注；
- 截图 `tmp/e1/`（浏览器导航态+文件预览态，实机开窗口截或手动，标注来源）；
- progress-e.md 记录：NuGet 三项、runtime 检测逻辑、实测清单。

## 硬约束

- 不复制资产文案商标；禁 git commit/merge/push（merge main 除外）；改动留 worktree；
- 与 D 轨并行：**除 E-seam 一行接线外不得触碰 WorkspacePanelView/MainWindow 其他区域**；发现必须改共享文件时，写进 progress-e.md『需协调者合并处理』，不要自行大改。
