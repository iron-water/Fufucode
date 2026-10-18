# 审阅素材来源

此目录仅供用户要求的“旧版 / 返工版 / UniGetUI 同位参考”设计审阅。`previous/` 是本轨返工前的 81 个文件原样留档及原生成脚本，哈希在 `inventory-before.json`；`compare/` 是逐项对比板，`comparisons.json` 记录每板实际引用路径。两者均不得作为产品资源打包。

参考来自只读克隆 `C:/Projects/Fufucode/reference/UniGetUI`：

- `media/UniGetUI_1.png` / `_2.png` / `_8.png`：实际深浅界面、标题栏、工具栏、任务卡片和 Operations 面板截图。
- `media/assets/devolutions-unigetui-icon.svg`、`splash-light.png`、`splash-dark.png`：实际品牌源与 Splash。
- `src/SharedAssets/Assets/Symbols/add_to.svg`、`expand.svg`、`collapse.svg`：源图形渲染，标注为源码渲染，不标为运行截图。
- `src/UniGetUI.Avalonia/Assets/Images/Empty_inbox.png`：实际绑定于 `PackageBundlesPage.cs` 的空集合图；`AbstractPackagesPage.axaml` 给定最大 180 px。
- `src/SharedAssets/Assets/Images/alert_laptop.png`：仓库错误语义素材；未找到现快照的绑定，故不声称是实际页面同位空态。
- `InstallerExtras/installer-banner.png` / `unigetui-256.png`：由 `UniGetUI.iss` 的 `WizardImageFile` / `WizardSmallImageFile` 引用。

参考图形与商标归原权利人。当前本地克隆根 `LICENSE` 为 MIT（Copyright 2025 Martí Climent），原文见 [UNIGETUI-LICENSE.txt](UNIGETUI-LICENSE.txt)。单幅参考素材若有独立权利声明，应以原声明为准；本轮仅将其置于明确标注的审阅对比中，未将任何参考路径拼接进生产 SVG。

由于两个产品的功能不同，UniGetUI 无新建会话、对话/扩展空态、独立 Pause/Resume 图标或自定义 256×256 完成页图形。同位类比和无法对照的部分在每板上注明。还原态与四种交互态缺运行截图，当前对照源码，未伪造实测截图。

预览中文字由本机 Windows 微软雅黑绘制为像素；未分发字体文件。小书匠提供的对话/电脑/包裹 emoji 源已查看其格式与色彩，最终未采用，以保持生产图形的中性 Fluent 风格。
