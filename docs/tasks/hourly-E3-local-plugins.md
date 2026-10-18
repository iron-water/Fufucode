# E3-LOCAL：插件本地固定版本与失败恢复

任务：E3-LOCAL｜难度：L3｜推荐主模型：`group/auto-deepseek-v4-1-flash`｜理由：文件发布、版本回退、真实加载状态和独立Avalonia组件联动，采用成熟本地包流程；公共会话/宿主接线由M负责。备选 `group/auto-qfmodel` 仅协调，不自动换模型。模型probe/验证lead显式 `devin/swe-2`，普通本地测试不调用模型。

产品起点 `637bd9193df3536ae05ead09f1c094878ea5b89b`；派工提交只增加本任务书/台账。日期2026-10-13。用户明确继续往后开工，本包与R1候选包装并行，不等待其安装完成；R1仍冻结4de5151，本包未来成果不冒称已进入该固定包。先核对实际工作树、分支或detached、起点与继承状态；完整读取AGENTS、AI_DEVELOPMENT_PLAN、execution-contract、session-handoff-protocol、agent-event-protocol、session-difficulty、development-topology-v2、complete-feature-modules C2和本任务。

## 已核实起点与所有权

现有 `ExtensionsPageViewModel`、`ExtensionsPage.axaml` 和 `ProjectExtensionService` 已提供发现、enabled/active/pending、加载错误与安全重启。缺固定版本托管包及更新/回退。复用其语义，不重写原页，不另造插件市场或声称所有后端互通。先读真实接口，不能用规划名虚构现有API。

唯一可写：新 `src/Fufucode.App/Features/Plugins/**`、独立 `tests/Fufucode.App.Plugins.Tests/**`、自身 `docs/records/E3-local-plugins-2026-10-13.md`、忽略tmp证据。可新增服务、模型、可嵌入现有扩展页的UserControl/VM；禁止改Core/Pi/Hosting/Cli、现有ExtensionsPage/VM、MainWindow/Integration/Settings/Styles、AppDataLayout/desktop.db/solution/共享自测、全局progress/decisions/failure-registry、包装或另一任务目录。M承担现有扩展页和公共动作接线；模块待接线是有效交付，不要求越权。

## M冻结的首版行为

1. **仅本地导入固定版本，首个后端Pi。** 输入可以是本地插件目录，显式选择相对入口。本产品使用描述文件 `fufucode-plugin.json`，字段至少id/displayName/version/backend/entryRelativePath/supportedHostVersions；它是Fufucode本地包描述，不伪装成Pi/OMP原生格式，不擅自生成或改引擎配置。实际Pi入口支持按已安装固定源码/ExtensionCatalog核对；不确定时要求明确入口，不猜能力。兼容规则为声明的精确宿主版本列表与实际注入宿主身份匹配；缺身份/未知版本标unknown并拒绝应用，不假称兼容。OMP/Codex/DSH首版明确unsupported。
2. 在调用方提供的AppDataLayout根内独立 `plugins/local` 保存本产品副本与目录状态；不操作源目录或用户外部扩展。稳定插件ID/版本/后端、来源和摘要逐字绑定，版本同名内容不一致要明确拒绝，不能覆盖固定版本。路径名称采用安全编码/内部ID，不能把显示名称直接拼可执行路径。导入先校验再复制到本轮新暂存，再原子发布；拒绝越界入口、符号链接/reparse遍历、恶意描述、损坏文件，保留原源/既有目录。大小/数量上限保守明确，错误可返回，不自动删除历史。
3. 列表显示来源/固定版本/宿主兼容、当前选定/待应用/实际加载/加载错误，这些状态分别来自目录与**调用方提供的真实宿主快照**，不能把文件存在或enabled当active。宿主缺失照常浏览/安装本地副本，应用不可用。插件是本地执行代码，现有项目信任仍由宿主负责；权限文案不冒充OS沙箱。
4. 用户明确提出更新到另一固定版本，保留旧版本与配置。活动路径由宿主快照核对，存在活动使用或身份不确定时只登记pending，不能原地改/删除加载目录、杀进程或启动新会话。应用/候选验证通过回调契约交M接入已有安全重启；无回调不假成功。更新验证失败保旧选定版本和文件，可显式回退；更新成功只记选定或确认的宿主实际加载事实，不伪造运行状态。回退也经同一宿主门，历史与失败诊断保留。
5. 移除分清本产品副本与外部引用，只允许明确移除自己托管且未活动使用的版本；活动/未知先pending，失败保目录状态。不能删除外部源、内置审批桥或用户其他插件。目录状态持久化与并发修改有预期generation/独占写入/原子文件与失败保真；损坏/未来版本文件不能静默重建覆盖。新VM重开可以恢复真实目录和pending，不自动执行更新/重启/模型请求。
6. 提供可嵌入现有扩展页的管理UserControl与VM：本地导入/预览、版本选择、兼容/状态、更新/回退/移除与错误返回。选择器/文件夹打开/宿主快照/安全应用通过明确注入委托或接口，不硬编码主区路径或另起后端。外壳接线交M，不复制窗口/侧边栏或修改已完成布局基线。实现按最保守可用选择，假设写记录，不停下来问。

## 验收与交付

先明示API和验收，按可验证增量实现提交。独立自运行测试工程沿用当前仓库runner风格，用真实临时文件/本地插件目录验证导入和重开、固定版本/摘要、兼容未知/不支持、更新失败旧版本/源内容保持、pending不动活动目录、显式回退、并发旧generation拒绝、真实文件锁/损坏保真/入口越界与目录链接、移除只限托管非活动；不只验证自造字段能serialize。无头真实Window编译绑定验证列表/选中/输入/失败返回与临时脱离/重挂，同一VM生命周期不丢状态。不用VM直接调用替代所有控件行为。

App标准构建0警告0错误、独立套件全部通过；需要新增工程依赖时只在自己的csproj，不能加入警告压制。全量App自测在本树可运行即跑并保存命令/退出码；缺忽略运行时可只读复用主区托管Node/Pi，不能把本机路径写进产品，不能复制凭据或改全局provider。若做真实Pi加载探针，只有自有临时插件与数据根，不发模型任务，参数仍显式最低成本devin/swe-2；不能把回调夹具验证冒称生产插件实际加载。实际加载、安全重启、原生IME/DPI/外壳接线仍由M对应验证，不因暂缺外部工具停止独立模块。

保护全部继承变更，不夹带提交，不push/reset/改写历史/删除worktree/登录采购/公开部署，不安装市场包或自动下载latest。新增缺陷写自己的记录和协调卡，由M登记全局；最终完整人类卡写自身记录，最终回复只一个JSON delivery.card：schemaVersion1/type/taskId=E3-LOCAL/status(submitted/module_ready/blocked/incomplete)/summary/commits完整SHA/changedFiles/verification(passed,failed,notRun)/evidence/integrationNeeds/blockers/writerStopped=true，附真实worktree/base/head。列清M接线契约与原生未验，不能自填accept/integrated。提交完整卡后停写，保树/产物/证据，不开始下一任务。
