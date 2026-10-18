# 最新安装包与插件成分独立核查

日期2026-10-16；观察main `56f95bd`。仅核文件、ZIP、完整性清单及源码；未安装执行Setup、未做干净系统验收。本轮不修改产品。

## 实测

- out/Fufucode-Native-Setup.exe：82,755,872字节，78.92MiB；本机文件时间07:53:56（America/Tijuana）。SHA256 `cff6b9cbe3e4bac4e51e216032edbac0148df0abd3eced51ed2feb0a2faf6fa1`。
- out/FufucodeNative.zip：116,482,290字节，111.09MiB；452项，解压312,979,738字节，298.48MiB。
- ZIP与out/build-native载荷大小/数量一致。integrity.ps1 Verify核451个清单文件通过，无missing/changed/extra；这是载荷清单验证，不证明Setup内部/签名/安装启动通过。
- app/227,701,421字节（217.15MiB），cli/85,180,853字节（81.23MiB）；另有启动器、README、清单。
- 主ZIP无Fufucode.Plugins.*.dll、无node.exe、无APK；仍含Fufucode.Core/Pi程序集、自包含.NET、Avalonia/渲染字体、SQLite、WebView2桥接等。Pi.dll是托管协议代码，不等于Node/Pi外部运行时。
- PDB总106,734,444字节（101.79MiB）；Skia/HarfBuzz两个符号文件占104,951,808字节。仍有可优化发布内容，未擅自删除。

| 独立ZIP | 字节 | MiB | 插件自身DLL字节 |
|---|---:|---:|---:|
| agent.browser-use | 17769655 | 16.95 | 19456 |
| agent.computer-use | 17777793 | 16.95 | 29696 |
| service.speech-synthesis | 17768464 | 16.95 | 14336 |
| service.remote-control | 17842376 | 17.02 | 232448 |
| automation.cron | 17766018 | 16.94 | 8704 |
| skin.aero | 19394634 | 18.50 | 5120 |
| automation.auto-dev | 17817814 | 16.99 | 5120 |

七ZIP合计126,136,754字节（120.29MiB）。插件包重复附带Core/Pi/SQLite等依赖；例如auto-dev携22种runtimes目录，包括Linux/Mac/wasm和Windows多架构。不是每项业务本身都需17MiB，也不证明必须将这些共享依赖复制入每个插件。宿主契约程序集应共享绑定，专属依赖按目标平台/兼容策略发布，不能盲删。

## 未通过的关键范围

1. 自动开发“整体已拆”不成立。AutoDev工程仅csproj、manifest、357字节AutoDevPluginPackage.cs；入口只声明ID/version并CreateToolHandler返回null。自动开发专属DAG/AutoAdvancementEngine/BackgroundHost仍编译于Core，监控页面仍在App。不是因null工具处理器本身判错，而是找不到该包提供服务/页面/策略及实现迁移。
2. Aero同样只有入口及依赖，CreateToolHandler返回null；App/Features/Skins/Aero与SkinCatalog真实实现仍内置，其余非基线皮肤也仍在App。缺少资源/皮肤贡献与卸载回退链，不能只核默认包没有AeroSkin.dll就通过。
3. 外部后端仍未全部物理分包，Core.csproj仍直接引用Pi；无对应独立分发包。不是用户例外项，可继续开发，不能宣布全拆完成。
4. 设置市场InstallOrUninstallPlugin仍直接调用PluginStateStore.Install/Uninstall，未走PluginPackageManager.InstallPackage/UninstallPackage。存在管理器不等于用户安装入口已闭环。包管理器源路径无真实包时还能生成manifest后继续置安装状态，激活失败可静默返回。缺载荷不应显示安装成功。
5. package-native先生成核心Setup/ZIP，再生成out/plugins-dist；Inno仅包含build-native，独立包未进入安装源。离线本地/在线市场获取及正式安装后能找到源的链路须验证，不能用仓库旁的分发目录替代干净安装。

建议M继续修复真实迁移/贡献接线与安装回执，纠正第116轮“自动开发整体/全景拆分完成”说法，并按用户清单逐项核。用户明确例外的工作面板、文档提取、系统通知、用量统计、存储诊断、高级定制保留内置。

stoppedWriting=true；本轮只新增此记录，无产品、设备或临时进程改动。
