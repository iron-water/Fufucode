# R1-REFRESH：当前主线固定版本候选包

任务：R1-REFRESH｜难度：L3｜推荐主模型：`group/auto-qfmodel`｜理由：复用已经跑通的包装链路，联合核对新模块、运行时和安装证据；公共源代码修复仍由M承担。备选 `group/auto-glm-5-3-flash` 仅原模型不可用时协调，不自行切换。所有真正模型probe显式 `devin/swe-2`，不启动昂贵测试lead；本包原则上不需要云模型。

用户时区2026-10-12开工，UTC2026-10-13。产品冻结SHA `4de5151272c1766415c837060a09d99617e5554e`，包含C5第七增量实现 `c417db8c62c8192874f7685419baae96099213c9`。新树从本任务书提交开始，其相对产品SHA差异只允许派工文档。不得同步未来main。历史候选e64dd1b的成果与证据保留；此次不是重跑历史版本，更不是整项生产验收。

## 权威、允许范围与保护

完整读AGENTS.md、AI_DEVELOPMENT_PLAN、hourly-main-coordination、development-topology-v2、execution-contract、session-handoff-protocol、session-difficulty、production-delivery §8、P2-candidate与M-P2-review-2026-10-11，以及本任务全文。开工核对实际cwd/HEAD/detached或分支/起点/继承状态，命令显式workdir。本执行会话由M授权创建，唯一写入位置为创建后的隔离工作树，不能回主区写源码。

允许修改：`scripts/packaging/**`（仅实际包装缺陷）、`labs/packaging/**`（本轮新报告）、`docs/records/R1-refresh-candidate-2026-10-12.md`。日志/产物只在自己的tmp/out且不提交二进制。App/Core/Pi/Hosting/Cli源码、solution、共享自测/设置/Styles/MainWindow、全局进度/决策、reference均禁止修改。源产品缺陷发协调卡到自己的记录，保留真实红日志，继续可独立部分；不得放宽断言、NoWarn或删用例来造绿。共享接线由M修，不因worker受限而打回让它越权。

本树若继承已跟踪out/build或旧ZIP，现有package.ps1会递归删除/覆盖默认输出。**先核对，未经隔离保护不得运行该破坏性默认路径。** 可在允许包装目录增量提供新输出根参数，并贯通archive/installer/verification（保留默认向后兼容），或把构建暂存到本树新临时目录；不移动/覆盖用户继承工件。所有删除只能针对自己本轮创建且已核对绝对边界的构建暂存。旧候选 `out/candidates/1.4.2-e64dd1b` 不触碰，M收集本轮产物时用独立含4de5151的目录。

## 内部步骤与验收

1. 只读核对实际托管Node/Pi版本、锁文件与许可。工作树缺忽略runtime时，可以只读复制主区 `C:/Projects/Fucode/Fufucode/runtime/node`、`runtime/pi/node_modules` 到自己对应运行时位置，记录来源SHA/版本/哈希；主区不得写入。不得全局npm安装、改provider或把开发机路径写进产品。OMP目前历史候选未托管，重新核对实际情况，未托管如实记录；不擅自npm下载新后端或改用户配置。
2. 阅读真实package/build-installer-version/integrity/archive/verify-clean-start/run-sandbox参数，先解决本树继承输出保护，再标准还原/构建、发布App+Cli self-contained win-x64。构建/发布0警告0错误；本固定源码App全量--selftest、Core/Pi和新增Control/Hosting/持久化独立套件全量（真实工程名先rg确认），真实命令/exit/count/log齐全。测试与构建串行避免本任务apphost锁，不杀未知进程。已有Atria凭据选跑不启用，注明SKIP；无模型测试无需模型参数。
3. 新目录解压新包，包内integrity完整校验；PATH剥离全局Node/Pi依赖，临时数据根/临时Git，实际包内App全量自测、Pi get_state/桥接发现、CLI新进程恢复。新包包含哪些组件/新页/宿主程序集按清单和实际入口核对，不因包含DLL而称全生产闭环可用。C5本地草稿和原回执的新VM验证来自包内全量自测，另有条件再做跨进程，只做过哪层就写哪层。
4. 本轮版本化安装程序/ZIP记录尺寸、SHA256、payload清单hash及完整源SHA，包装修复提交额外列出。版本按现有元数据选择保守candidate，不修改产品版本源码、不冒称正式发布。只针对包装脚本/载荷文本和启动器检查旧机器/旧worktree硬编码；避免无关全仓防错扫描。
5. 真正干净安装/启动/卸载仅Windows Sandbox或明确现有隔离环境，先核对已有Sandbox实例，不停止未知实例。原生UI操作只有本包一条线，不触碰用户窗口。必须用提交态新安装包保存实际Sandbox results/info/log；窗口创建/退出不能冒充浏览器交互、IME/DPI或完整聊天验收。联网、WebView2、升级/数据保留、OMP等实际限制逐项列清。系统功能/工具缺失或他人Sandbox占用时标具体外部blocked，不修系统/重启/登录；继续本包其他验收，不能拿旧Sandbox证据当新包通过。

## 收尾与交付

每个实际包装修复小步提交，记录失败→修复→真实通过，不修改禁止范围。记录列固定来源/实际worktree/继承状态、实现提交、所有实跑命令/退出码/日志、产物绝对路径/哈希、未验/blocked与原因、公共协调需求。旧自测绿和静态载荷检查不等于系统原生验收。先交候选，不以缺界面IME/DPI阻止可独立构建，更不把它升级生产六门通过。

最终主动给完整交付卡（同记录），列任务与软件、完整起点和交付HEAD、提交范围、实际验证/未验项/保护情况、状态和原会话是否仍写代码。最后明确停写，保树/产物/日志，不push、不改写历史、不清工作树、不登录/采购/公开部署，不开始下一任务。创建/结果不明由M对账，worker不自行重复启动。

受管Codex Desktop传输补充：记录内保留完整人类交付卡，最终回复按agent-event-protocol仅输出一个`delivery.card` JSON对象，至少含schemaVersion=1/type/taskId=R1-REFRESH/status/summary/commits/changedFiles/verification(passed,failed,notRun)/evidence/integrationNeeds/blockers/writerStopped；额外列实际worktree/base/head和产物哈希。status使用submitted/module_ready/blocked/incomplete，不能自填accept/integrated。M将绑定真实threadId、终态turn和HEAD审核，不伪造OMP事件或对没有CLI manifest的Desktop任务调用旧Review脚本。此补充由M向创建后的同会话发送，不要求worker同步未来main。
