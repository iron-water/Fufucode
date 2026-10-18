# E1 Git 工作树与本地审查模块

推荐主模型完整ID：group/auto-qfmodel。难度L3：真实Git范围、固定快照、结构化发现项与Avalonia页面；公共App/执行宿主接线由M单写。备选group/auto-deepseek-v4-1-flash仅供明确故障后评估，不自动换模型。测试lead与必要模型探针显式devin/swe-2。

起点：创建前main任务书提交，产品基线7df4c71，不继承用户未提交内容。读取AGENTS、AI_DEVELOPMENT_PLAN、execution-contract、session-handoff-protocol、development-topology-v2、complete-feature-modules代码审查段、D4-git-snapshot记录、GitWorktreeService/GitDiffSnapshot/DiffQuery真实代码。旧D4快照已集成，不重写它或重做批量适配。

目标：用户选择项目与明确审查范围→固定快照→执行/取消审查→查看可定位发现项→变更后提示过期→复制/导出；工作树列表/显式创建/打开请求可用。交付自包含模块，M接共享入口，不把待接线作为失败。

允许：新src/Fufucode.Core/Features/LocalReview/**、新src/Fufucode.App/Features/LocalReview/**、新tests/Fufucode.LocalReview.Tests/**，必要的src/Fufucode.Core/GitWorktreeService.cs局部扩展，docs/records/E1-git-review.md。不改Automation/**、GitDiffSnapshot/DiffQuery、MainWindow*/Settings*/AppDataLayout/组合根/Styles/共享sln/旧scripts/权威进度/全局failure-registry；需共享修改交精确协调卡并继续独立步骤。不触碰C3公共写入范围。

内部顺序和验收：
1. 真实Git工作树只读列表、状态、分支/HEAD；显式创建使用既有服务，从已提交HEAD创建，不复制脏改动、不自动信任新目录、不改变当前会话。打开用宿主请求接口。不实现强删、reset、clean、自动提交/合并/push；生产工作树不移除。非Git、缺git、路径空格/Unicode、取消/失败都有可操作诊断。
2. 审查范围至少工作区差异/暂存差异/明确提交或基准范围。提交/基准合法性真实验证，不从输入拼shell；固定来源/HEAD/文件字节/实际行定位用既有快照，发现过期拒绝当作当前精确定位。若快照API缺范围能力，先提供精确协调卡，不静默改为整个工作区或只审某个默认文件。
3. 本地审查运行契约复用已验证harness/注册runtime，用窄ILocalReviewExecutor接口及可接线适配。模型响应不是完成证明：核对终态，严格解析优先级/路径/有效行/解释/证据，拒绝快照外路径/无效定位/截断输出；错误/取消/无发现分别展示，不能用固定夹具假扮真实审查结果。不自动修改文件、不自动接受/集成。真实模型最小探针只允许显式devin/swe-2；无凭据或runtime阻塞写明，不索取登录并继续无模型Git/视图步骤。
4. 页面提供选择范围、执行/取消、结果过滤、过期标记、跳转请求、复制/Markdown或JSON导出、历史与错误；耗时Git/文件IO不阻塞UI。App数据根由宿主注入，只测试临时目录，不改用户desktop.db。工作区切换和临时Detach不串上下文/重复订阅；dispose停止本模块运行，但普通停靠移动不把运行误取消。
5. 独立测试使用真实临时Git仓库/子进程，覆盖工作区/暂存/范围、Unicode/new/delete/binary、快照后变化、取消、坏响应、范围外路径、退出重开与无头真实编译绑定。Core/App/模块测试构建0警告0错误，必要行为通过。共享入口与原生焦点/IME/DPI/真实跳转由M接线后补验，未验不得当通过。

每个可验证增量提交。只能为临时Git夹具commit，不能替用户仓库提交。原生验证串行，需要工具则按computer-use技能，不接管用户窗口；模块尚未挂外壳就明确未验。无需等确认；局部阻塞继续其它步骤。结尾完整交付卡列真实worktree/起点/HEAD/提交/范围/命令/证据/未验/接口接线要求，停止写入，不自行开下一包。不得push、改写历史、删除工作树、登录、采购或公开部署。
