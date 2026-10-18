# 插件拆分与文件预览独立复核

观察HEAD：`4a83f22`；日期2026-10-16。依据用户要求复核M是否实现最新插件范围与文件预览要求。本轮只读源码/提交/主控记录，未重跑编译、测试、安装或原生窗口，不采信历史测试为本轮通过。

## 结论：整体未完成

1. 文件预览独立功能tab已在源码层移除，存在实质实现。`6219004`移除MainWindow.Workspace独立preview注册；KnownPages/默认右栏不含preview；FilesContent内有FilesPreviewContainer、PreviewBrowser、文件身份栏与CloseFilePreview返回；文件/变更/产物路由改为files。更新的WorkbenchTabsSelfTest检查实际文件路径与返回列表，并非只有改标题。原生内容显示/输入/错误恢复本轮未运行，不标产品验收通过。
2. 旧布局目前是过滤preview并选择首个剩余页、删除空浮动区，未见将旧preview身份/浮动位置/固定状态/当前文件上下文映射到files的迁移。可避免未知页，却不等于任务要求的保留上下文迁移。该部分需要反例和实际恢复证据，不能仅称平滑迁移。
3. 插件真实拆包未完成。`4883579`仅新增43行，涉及市场目录、设置分类、聊天后端菜单；没有独立插件项目、构建或安装载荷改动。PluginStateStore.Install在PluginMarketplace.cs:287仍只WriteState并SaveLocked；Uninstall同样只改状态。新增service/automation/theme ID在所核源码中只出现于目录定义，未接这些功能生命周期。
4. 可选实现仍默认编译：Core/Plugins/BrowserUsePlugin及ComputerUse系列仍在Core；Core.csproj仍直接引用Pi项目；App/Features/Automation及RemoteControl仍内置；MainWindow.Workspace直接new自动开发页面/VM；MainWindow.TurnCompletion直接new ReplySpeechService；MainWindow.Customization直接将CheckSchedules接入计时器；SkinCatalog仍直接列全部皮肤。市场新增“未安装”不证明其实现未发布/未初始化，目录项不能替代真实插件。
5. 原生打包脚本仍publish App+Cli，无上述独立插件分包/加载步骤；没有足够证据验收默认包已剥离。工作面板及用户列出的六类例外无需拆包，不应为完成表面数量移走例外。

## 对主控报告的核对

最新最终答复称PREVIEW-FIX和六后端菜单闭环已合并、全量自测无失败，并未明确声称全部物理拆包完成。`docs/records/hourly-dispatch-ledger.md`第112轮也写“下一步：EXTRACT-1…物理动态包加载”。因此是局部完成，不能向用户概括为此次全部需求已完成；双槽空闲也不是没有待办。

## 后续要求

M继续原任务：真实插件载荷获取/校验/原子部署/贡献注册/更新回滚/停用与卸载，按用户指定例外保留内置，补旧preview布局上下文迁移。先独立包Browser Use/Computer Use再按其余清单推进；自动开发整体可插件化。验收须证明默认未安装的实现/专属依赖不在默认包、未安装无工具/服务初始化、真实装卸有效，不能以更改目录和绿自测代替。

stoppedWriting=true；仅新增本复核记录，未改生产代码、工作树、运行任务或机器状态。交接发送结果以本线程工具回执为准。
