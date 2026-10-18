# M：E3 本地插件离线公共接线（第44轮）

任务：E3-M-WIRING｜难度 L3｜M 单写公共入口，不另派实现者。起点 main `121a515`，受管模块来源 `8c672892c34f4b7564b2d7a2da92ea4481eb7dba`。本增量交付现有扩展页内的本地副本管理；真实 Pi 加载/安全版本切换仍未接线，不能称整个插件功能验收。

## 已做

- 只在 E3 原 worker 已 completed/idle、停写确认、真实 HEAD 一致且树干净后，按 Git 对象导入其15个允许文件。保留全部原提交与原记录；不整合其他工作树、不清理、不 push。
- 扩展页内嵌 LocalPluginSectionView；M 组合根注入同一 AppDataLayout 和真实当前 TopLevel.StorageProvider 目录/入口选择器。保留原扩展、命令、信任、重启及返回路径；设置/辅助栏基线没有重做。
- 导航往返保留本地目录与选择；关闭外壳才释放 VM。自测使用隔离目录，不写真实 AppData。独立插件工程加入 solution；最终23条共享接线断言加入 App 全量及 `--local-plugins-only`。
- 额外反例实际暴露4处旧模块行为：选择器异常逃出命令、释放后迟到目录结果仍改字段、预览错误的派生属性不通知控件。另一个邻接反例暴露源目录修改后旧成功预览仍可用。M 在已收编模块上增量修复，没有要求停写 worker 越权接公共入口。
- 修复目录/入口选择的异常与取消诊断、操作互斥/迟到身份检查；预览可见性通知；源/入口修改立即失效旧预览，异步预览绑定捕获输入和序号，旧返回不写新结果。
- 首次全量1225零失败后，M实际查看该次渲染的扩展页截图，发现文字应用按钮误用了28DIP图标按钮样式（只露首字）。增加按实际文字测量的共享主题反例，一FAIL；使用已有subtle文字按钮样式，保留原共享主题。最终23条包含此新增反例；代码变化后重新跑同源全量，不拿上次1225覆盖修改。

## 验证及证据

所有命令在主仓库执行，普通本地测试未调用任何模型或云端。

| 实际命令 | 结果 | 证据 |
| --- | --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet` | 0警告0错误 | `tmp/m-cycle44-solution-final-build.log` |
| `dotnet tests/Fufucode.App.Plugins.Tests/bin/Release/net10.0/Fufucode.App.Plugins.Tests.dll` | 166 PASS | `tmp/m-cycle44-services-final.log` |
| 上述独立 DLL 加 `--ui` | 46 PASS，真实控件无头绑定/尺寸/生命周期 | `tmp/m-cycle44-ui-final2.log` |
| `dotnet src/Fufucode.App/bin/Release/net10.0/Fufucode.App.dll --selftest --local-plugins-only` | 23 PASS，0失败 | `tmp/m-cycle44-local-plugins-final2.log` |
| 同一 Release App DLL 加 `--selftest` | 最终1226 PASS、0失败、exit0；修文字前1225零失败另外保留 | `tmp/m-cycle44-app-full-final.log`；之前 `tmp/m-cycle44-app-full.log` |

忠实红证据 `tmp/m-cycle44-local-plugins-red.log` 为4 FAIL；`tmp/m-cycle44-adjacent-red.log` 为1 FAIL；`tmp/m-cycle44-label-red.log` 为1 FAIL。相同断言保留，不放宽，不覆盖红日志。服务套件的临时目录清理失败输出如实保留，不把遗留临时目录当成已清理。

23条集成断言覆盖真实共享页/编译列表、离线预览与固定副本、返回导航、持久重开、未知宿主的 pending 移除保文件/用户源、异常/取消/关闭迟到/换源迟到/操作互斥与派生属性通知/文字标签完整尺寸。它们不等于系统输入、原生文件对话框或像素验收。M实际查看两轮无头截图，最终应用标签完整显示；最终截图在 `C:/Users/mio/AppData/Local/Temp/fufucode-selftest-73086f6067224e5aac96b23b15b45069/evidence/h3-shots/extensions-dark.png`。临时证据路径属于本机验证记录，不是产品硬编码。诚实区分渲染检查与系统原生证据。

## 未做、原因与下一步

- 没有注入伪宿主。当前界面可预览、导入和管理本地固定副本；真实应用/回退不可用，未知宿主移除只记 pending。下一增量由 M 按 `docs/tasks/hourly-E3-pi-host-wiring.md` 实现真实 Pi 宿主适配与显式安全版本切换，不交回受限 worker。
- 源码核查实际 Pi RPC `get_state` 不包含扩展加载清单，`get_commands` 只包含命令。既有 ActiveExtensionPaths 是启动传入并通过恢复的路径集合，不能直接当成独立、全面的实际加载证明。真实宿主适配须依据已固定引擎/桥能力提供诚实加载事实，不虚构 `get_extensions` RPC。
- 应用运行中更新、失败恢复旧版本、回退、实际加载、启动失败态离线入口、原生选择器/键盘/IME/DPI未验证。既有服务回调夹具通过不替代这些验收。
- R1-REVALIDATE 原 qf/cb9f 会话继续固定产品 `cc44eee`；已发送协调卡，M 只跑构建/文件/无头控件，原生 Sandbox 槽仍 R1 独占。新插件代码不追溯加入这次冻结候选。
- main 写入持有可续期独占租约，编辑/提交前检查实际 PID 并续期；最终正常释放后补证据。原租约超时历史保留，不能据本轮通过抹掉历史。

实际实现提交 `1144ac1c802f1277b89ba032587fca9bf8d59244`（29允许文件，用户继承改动未暂存/未提交）。绑定原任务/run/真实来源HEAD的收编决定为 `docs/records/E3-local-integration-2026-10-13.json`，integrationScope 仅已接受模块与现有页离线副本，不包含真实Pi宿主或整项生产验收。旧pending_wiring审核记录保持历史。main租约提交前仍实际存活并续期，收尾正常释放的工具记录保留；监督超时历史不删除。
