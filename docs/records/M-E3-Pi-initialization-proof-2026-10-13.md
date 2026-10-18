# M：E3 真实 Pi 初始化证明前置增量（第45轮）

任务 E3-M-HOST｜L4｜M 自做公共实现，不新派贵测试lead。main起点 `5a7dd163eb1f1e44b5f8a5dd2d6c0551dc21e2fc`。任务范围/后续门见 `docs/tasks/hourly-E3-pi-host-wiring.md`。

## 已实现与实际意义

实际固定 Pi 包版本1.1.0。源码 `rpc-mode.js` 的 get_state 没有加载清单，get_commands 只有注册命令；`core/extensions/loader.js` 等待 factory 后 commit，异常 discard 并报加载失败。

新增 `PiPluginLoadProof` 和 `LocalPluginService.PreparePiLoadProofAsync`。在只读核对固定版本声明/完整摘要/入口/祖先无链接后，为该版本生成应用自有 `plugins/loaders/<随机身份>.ts`。原 `plugins/local` 固定副本和目录选定状态不变，外部用户源不碰；此准备接口不启动Pi或登记已加载。

包装入口通过相对路径导入原入口、检查其默认导出为函数、**await 原初始化完成之后**才注册唯一证明命令。命令包含随机操作身份及版本/摘要描述，核对引擎 get_commands 的 extension 来源与精确包装路径；包装或固定内容被改则拒收。生产宿主入口额外核对实际 get_commands 成功响应与非空同连接 Generation，旧进程回执不能给新进程确认。

这只是“该固定入口已完成初始化”的证据，不是任意插件通用加载清单、长期健康/兼容保证、恶意代码安全沙箱。`MatchesCommandData`是内容匹配器；宿主应使用带真实当前 Generation 的 `TryConfirmInitialization`。命令能够被本地受信代码注册不构成对恶意插件的隔离证明。

## 真实验证

实际运行独立套件：

```
dotnet build Fufucode.sln -c Release -v quiet
dotnet tests/Fufucode.App.Plugins.Tests/bin/Release/net10.0/Fufucode.App.Plugins.Tests.dll
dotnet tests/Fufucode.App.Plugins.Tests/bin/Release/net10.0/Fufucode.App.Plugins.Tests.dll --ui
dotnet tests/Fufucode.App.Plugins.Tests/bin/Release/net10.0/Fufucode.App.Plugins.Tests.dll --load-proof-real <本机托管Node> <本机固定Pi CLI>
dotnet src/Fufucode.App/bin/Release/net10.0/Fufucode.App.dll --selftest
```

| 证据 | 实际结果 |
| --- | --- |
| `tmp/m-cycle45-restored-build.log` | 最终原await实现的solution Release0警告0错误 |
| `tmp/m-cycle45-services-bound.log` | 原服务166 PASS + 新证明规则21 PASS，默认独立入口已纳入新规则 |
| `tmp/m-cycle45-ui.log` | 原46真实无头控件/绑定/生命周期通过（本增量未改UI） |
| `tmp/m-cycle45-proof-final-green.log` | 最终35 PASS，包括真实Pi进程JS/TS初始化、明确坏插件exit1、旧固定版本可在新进程恢复、旧Generation拒收与独立阻塞异步factory |
| `tmp/m-cycle45-app-full-bound.log` | App全量1226 PASS/0失败/exit0；此后只加独立探针断言，临时no-await实现已逐字恢复 |

真实探针使用临时工程/数据/自有插件，显式 `--provider proof --model devin/swe-2`。proof为临时离线模型描述，API指向不可用环回端口，只有 get_state/get_commands；没有 prompt、凭据复制或云请求。它是引擎插件初始化验证，不是免费云模型试跑/任务质量验证。无自有探针残留node进程，按命令行含本轮临时proof根限定核对；没有停止用户/未知进程。

规则21项覆盖命令/来源/描述/唯一性/精确路径、原入口与包装分离、摘要篡改、其他后端拒绝、当前/旧/空Generation、错误RPC类型和失败响应、准备不改目录代次或副本。真实探针另外覆盖坏显式扩展退出1/精确入口错误、不能借旧成功回执、保存旧副本重新初始化、TypeScript相对导入。独立异步factory先写entered、等待外部release文件再写completed；释放前真实get_state/get_commands不能完成，释放后才确认原入口，避免只看自己生成的字段。每次进程messageCount=0，未发模型任务。

## 失败保真与边界

- M首次编译用错 LocalPluginVersion/PluginVersionRecord 的参数类型，1错误，日志 `tmp/m-cycle45-proof-build.log`保留；修正确实类型后0警告0错误，不压制诊断。
- 首次真实探针错误假设坏显式扩展失败后Pi仍存活，实际固定1.1.0直接exit1。`tmp/m-cycle45-proof-real.log`原异常/精确入口/原因保留。改测试为实际失败语义与独立新进程恢复，不能称此为产品修复或隐藏红结果；官方源码的discard加真实exit事实一起支撑后续恢复方案。
- 另做忠实no-await反例：只将包装器的await initialize改为void initialize，独立临时输出构建0警告0错误；`tmp/m-cycle45-no-await-red.log`实际FAIL（坏factory不能归入明确候选初始化失败），exit -532462766。仅为证明测试能发现不等待factory的回归；未提交该变体。恢复原await同一行后标准solution构建和35条真实/规则检查全部通过，红工装/绿日志不覆盖。App外壳代码未在此变体中改动，原全量结果不称变体通过。
- 未进行桌面插件应用/更新/回退接线或原生UI操作。接口尚未由现有扩展页调用，所以当前页依旧离线副本管理，不能称插件可用链已完成。活动版本pending的真实安全切换、信任/草稿/项目切换/在途回执、启动失败态离线入口仍需M后续增量。
- 不改第三方运行时，不把新证据塞入R1冻结cc44eee候选。该候选新安装门外部阻塞独立记录；主线仍向前推进。

实际实现提交 `cc8f1981d3009b5965dad06e1f5815b00af960e6`（14允许文件，含R1独立审核记录，继承用户文件未暂存/未提交）。最终生产代码恢复原await，solution0警告0错误、35证明检查、166服务+21规则、App1226零失败；无新插件桌面接线验收或安装包构建。本次源提交不填成R1候选已集成SHA。收尾仅文档补录，不再改验证过的源码，独占租约正常释放后由工具终态保事实。
