# M：E3 真实 Pi 安全应用/更新/回退公共接线（第46轮）

任务 E3-M-HOST｜L4｜主区起点 `15a385940e36bf3b6d0d274f436fcbbf8bab1862`。M单写公共入口/宿主，原E3 worker停写，未派新实现者或昂贵测试lead。R1原qf停写确认，本轮只查状态和未知Sandbox PID15712；仍未重验新版安装，没有接管/停止未知实例。

## 实际行为

- 原扩展页的本地版本模块已注入 `PiLocalPluginHost`。身份取真实会话Backend与实际CLI包版本；没有Pi/精确版本不假可用。当前Pi空闲时动作显示“安全重启并应用”，保现有页/返回/设置/辅助栏基线，不新建窗口或插件市场。
- 新 `ILocalPluginRestartHost` 是明确安全重启契约。原普通Apply接口对活动版本仍只登记pending，未放宽原断言或伪造空快照。显式安全请求绑定原snapshot的项目/会话/连接代次，回调再次检查原宿主和空闲条件；最终通过真实get_commands初始化证明确认原固定入口。
- Pi proof包装器另存同根 `plugins/loaders` 相对身份元数据，支持从启用列表重开与实际初始化证明核对；原固定副本不改。未知/损坏/被篡改/旧直接托管入口都保持unknown，不能当成成功空清单。证明命令从用户命令目录隐藏，不把内部协议细节当可用用户动作。
- 应用/版本切换/回退通过既有项目信任与原sessionID安全重启链。候选失败真实恢复旧启用列表与原会话，再核对加载快照；旧副本/源/草稿保留。快照/回调跨await身份改变拒收，不对其他项目执行。
- Core新增空闲维护保留：原子确认connected/idle、无queue/审批，再保留当前连接；新普通/steer/follow-up请求均在RPC前拒绝，避免检查空闲后又有发送请求。双重维护拒绝，结束后恢复可发送。窗口切项目/会话与重启互斥，复用已有链而不是偷偷停止运行任务。
- M真实外壳额外测试发现旧既有缺陷：候选加载失败后RealSessionHost已恢复旧进程，但MainViewModel仍引用已释放旧Chat。忠实八条路径测试中此项FAIL，修复失败返回分支重新AttachRealSession；绿测试确认恢复后的UI连接Connected。这不是只改本地选定字段或宽松断言。

## 实跑验证

| 命令/证据 | 结果与证据等级 |
| --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet`，`tmp/m-cycle46-restored-build.log` | 最终恢复运行态门后的0警告0错误 |
| 插件独立DLL默认入口，`tmp/m-cycle46-services.log` | 原服务166 + 证明规则21 PASS |
| 同DLL `--ui`，`tmp/m-cycle46-ui.log` | 46无头真实控件/绑定/生命周期通过 |
| 同DLL `--host-real <托管Node> <固定Pi CLI>`，`tmp/m-cycle46-host-final2.log` | 19项真实Pi生命周期/版本/恢复/维护/元数据检查通过 |
| 同DLL `--load-proof-real <Node> <CLI>`，`tmp/m-cycle46-proof-real.log` | 35证明规则/真实Pi初始化检查通过 |
| App DLL `--selftest --local-plugins-real-only`，`tmp/m-cycle46-shell-real-final.log` | 8项真实共享外壳+Pi进程路径通过；控件在无头Window中运行，不是系统原生指针/IME证据 |
| App DLL `--selftest`，`tmp/m-cycle46-app-full-final.log` | 最终产品代码1226 PASS/0失败/exit0；之后仅新增Core断言，临时运行态门变体已逐字恢复 |
| Core测试DLL，`tmp/m-cycle46-core-final.log` | 最终359 PASS/1 optional Atria SKIP，exit0 |
| Pi测试DLL，`tmp/m-cycle46-pi.log` | 21 PASS，exit0 |

真实19项包含精确Pi1.1.0/显式最低成本probe模型、初次应用、普通活动pending不重启、实际版本更新、坏factory失败实际旧版恢复、真实回退、活动移除保目录、原sessionID/草稿保持、旧host拒收、维护拒follow-up/重复保留、元数据损坏不是成功空快照。八条真实外壳通过原页面VM命令进入实际MainWindow安全重启/绑定，覆盖版本1→2→坏3恢复2→回退1与返回聊天。

探针只用临时project/data、自有插件与离线proof provider，显式选定 `proof/devin/swe-2` 并核对实际get_state model.id；没有云prompt/API采购或复制用户凭据。Core的Atria真实云开关显式关闭。不把直接VM/回调夹具当真实引擎加载；也不把无头外壳+真实Pi当原生Windows输入。

## 忠实红/未验证

- `tmp/m-cycle46-shell-real-red.log`：八检查中“恢复后的聊天VM仍Connected”一FAIL；最终同断言全绿。不改期望状态、不把实际宿主恢复代替外壳恢复。
- 新Core空闲维护10条默认断言涵盖未连接/双维护/发送拒绝/恢复可发/真实mock运行中拒绝且不停止/排队保持。忠实只去运行态门的临时变体，6 PASS后在运行保护处一FAIL，日志 `tmp/m-cycle46-maintenance-red.log`，exit -532462766；恢复原运行态门后默认Core全量359 PASS，临时变体未提交，不放宽断言。该mock检查不发模型请求，不冒称真实云长任务验收。
- M首构建两个API接线类型错误保 `tmp/m-cycle46-build.log`，已修正确类型；不会压制警告。
- M一次在自有全量DLL进程仍运行时重建同输出，被自身.NET Host PID39904锁定，MSB3026/MSB3027日志 `tmp/m-cycle46-shell-build.log`保留。改独立tmp输出等待自有进程正常结束后再标准构建，最终零警告错误；此为M验证编排错误，不是产品文件锁回归，也没有杀未知进程。
- 原生指针/键盘/IME/DPI、跨版本安装/迁移、生产数据、关闭/取消中途的长任务组合仍未验；启动失败/无会话时的离线入口仍待后续补齐。当前默认App全量仍不默认启动这条real-only模型/进程路径，真实额外入口与默认门分列。
- 未实现OMP/Codex/DSH插件兼容、市场、长期运行健康/恶意代码沙箱。本版本初始化证明只确认受信入口factory完成，不能给任意插件通用兼容保证。
- R1冻结cc44eee候选不包含这些后续插件/维护源码；本轮没构建新包或重复安装门，完整生产六门未齐。

实际实现SHA `3eabe3b66568fc809634a03aa4bfc7978cfcc92c`（24允许文件，继承用户文件未暂存/未提交）。事实记录 `E3-Pi-host-integration-2026-10-13.json` 绑定此源码，真实Pi安全应用/更新/回退接线已完成，但列明startup离线/原生/长任务/新包未验，productAcceptance仍pending。这不是新的worker交付卡或替换R1旧候选接受记录。收尾只补文档，不更改验证源码；独占租约最终正常释放由工具记录保事实。
