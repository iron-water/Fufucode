# M：C5 原生草稿与原请求回执公共接线

公共接线实际mainSHA：`794e3380e7837baa53bca3555e6f6f05e83ee94a`。25文件提交，暂存白名单与实际索引逐项相等，未夹带用户demo或继承目录。本补录仅改文档/结构化决定，门禁所验源码没有再变。

2026-10-13 第32轮，M/L4。真实主仓库 `C:/Projects/Fucode/Fufucode`，main起点 `9c04ec9adfdeff2600484c7250c899f1a384391d`。原C5-PERSIST会话 `01a132c3-319c-7510-abe5-571ca2fc50c8`，工作树 `C:/Users/mio/.codex/worktrees/9fde/Fufucode`，L3 / `group/auto-qfmodel`。停写卡HEAD `157d03b0ade718d4259e90394e65e3de5c81d4ce` 已核对干净、范围和无活跃写入者。M独立复核192断言及原九个反例后接受限定模块，原十提交共17文件收编为 `f6a52b1ef51e1a6bbc819edeb2631999be17e65c`。原工作树、旧打回记录和红日志保留。

## 实际行为与保守假设

- 权威身份由owner在原SQLite库内持久化GUID；客户端不建库、不迁移。scope保真项目ID、完整绝对库路径和权威身份。同路径换库后原连接拒绝继续操作；旧库缺身份仍可观察，但不能绕过本地记录提交原生命令。
- 原生页新增默认折叠的“本地草稿与原回执”。支持明确保存、列表、预览、恢复创建/决定/取消对账输入。恢复必须匹配原上下文、版本及token；过期草稿保留并拒绝恢复。Unicode、候选ID及完整规范保真。
- 保守可用选择：用户明确点击保存。没有自动保存承诺，未保存输入不承诺跨重启保留。临时脱离/项目切换不偷偷恢复、升级版本或重发。
- 全部十种类型化决定先记录原ID、原JSON、CAS与pending_unknown，再调用Core，最后观察实际回执。generation冲突、文件锁、损坏/未来格式阻止无记录提交。Core成功而本地回执保存失败明确区分，保持unknown等待核对。
- “核对最近决定的原回执”和重启后unknown核对只读查询原类型/ID/完整payload匹配的不可变Core回执，再更新本地记录。查询没有回执时保留unknown，绝不调用Apply来取得结果或重发。
- MainWindow接入真实AppDataLayout根；独立持久化测试纳入solution。没有重写设置/辅助栏基线或默认关闭的极扁底部状态栏。

## 验证

以下均为本轮主线公共接线源码实际运行，exit 0；日志位于主仓库tmp。最后标准solution构建在测试进程退出后执行，无降警告配置。

| 命令 | 结果 | 日志 |
|---|---|---|
| `dotnet build Fufucode.sln --no-restore -v quiet` | 0警告、0错误 | `m-c5-persist-wire-build.log` |
| `dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest` | 1189项通过，C5 185项，0失败 | `m-c5-persist-wire-app.log` |
| `dotnet run --no-build --project tests/Fufucode.Control.Tests` | 511项通过 | `m-c5-persist-wire-control.log` |
| `dotnet run --no-build --project tests/Fufucode.Hosting.Tests` | 267项通过 | `m-c5-persist-wire-hosting.log` |
| `dotnet run --no-build --project tests/Fufucode.Core.Tests` | 340项通过；Atria选跑SKIP | `m-c5-persist-wire-core.log` |
| `dotnet run --no-build --project tests/Fufucode.Pi.Tests` | 21项通过 | `m-c5-persist-wire-pi.log` |
| `dotnet run --no-build --project tests/Fufucode.App.Automation.Persistence.Tests` | 192项通过 | `m-c5-persist-wire-module.log` |

新增21项C5门覆盖真实SQLite/Git临时环境、编译绑定输入/焦点按钮、新VM重启恢复、版本失效、双VM冲突、真实锁、损坏文件、缺权威身份、同路径换库、回执保存故障和只读unknown核对。此处重启是销毁/重建VM，不是跨进程长期运行证明。App临时根 `fufucode-selftest-9e6925ff64fb4a609339c19f503c4385/automation`；模块根 `tmp/c5-persistence-tests/20261012-213831-060`。原模块独立反例证据 `tmp/m-c5-persist-round3-{module,five,four}.log`。

执行失败如实保留：旧库字节保真fixture默认池化连接Dispose后仍占文件，`m-c5-persist-wire-pool-red.log`保留IOException；仅fixture改Pooling=false，严格库字节比较未放宽，最终全量通过。构建与本轮Core/Hosting进程并行导致apphost复制锁，`m-c5-persist-wire-apphost-lock-red.log`保留10警告2错误；等待这些测试自然退出后标准构建0警告0错误，没有杀未知进程。新测试工程首次构建未结束即调用--no-build曾因缺可执行文件退出1，构建完成后才取得实际192通过；不能计早期调用为通过。

## 状态与剩余范围

模块与本次公共接线为可验证增量，不是C5整项/生产六门验收。原生系统IME/DPI、跨进程恢复/长期稳定性、断电、多平台、新候选安装包及干净安装仍未验证。scope有4MiB及256草稿/512请求显式上限，满时拒绝而非自动删除；手动列表读取性能尚未基准实测。既有按键序列偶发根因与PID复用项继续open。下一步推进事件恢复/原生补验，再生成候选包。

主线唯一写入租约到期后先停写，确认HEAD未变且无竞争调度进程，再取得新的独占租约完成记录提交。未push、改写历史、删除工作树或登录采购；用户demo及所有继承未跟踪状态未夹带提交。公共接线真实mainSHA提交后补录。
