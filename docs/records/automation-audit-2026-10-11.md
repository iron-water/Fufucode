# 架构审计与控制台简化交付

基线：main `894097ceabae8ff82defd9fbc8dadc56bf044cca`。本次主写入 FileShare.None 租约持有期间修改；保护原 demo-verify JSON、用户意见文件、工作树及会话。

交付：公开设计研究及通用适配契约，见 [架构审计](../research/automation-architecture-audit-2026-10-11.md)。不引入新的模型角色或生产运行依赖，不迁移现有任务。

界面改动：
- 第一层仅当前推进情况、可展开状态/阻塞和四组阶段入口；完整图、运行中、自由缩放与二级线程图保留。
- 移除将控制门禁/规划条目混入百分比的进度条；不把旧集成结果显示成 M 此刻已完成工作。
- M 与 worker 运行状态要求活进程证据；连接断开显式标记上次快照，停止活动动画。
- 阻塞详情优先当前未解决记录，已 done 的历史 blocked 不再混入；节点增加原审核 findings，保留人类意见、模板、拓扑/模型编辑、撤销重建入口。

验证：
- `python -B scripts/automation-console.tests.py` 首次受到沙箱默认 Temp 路径访问问题；改用仓库内隔离临时目录，仅改变测试 tempdir。
- 16 项测试：15 PASS，1 ERROR。错误是原有原生批准测试调用 Task Scheduler Stop 返回 E_ACCESSDENIED；没有跳过、放宽断言或当通过。两项新增测试验证 inflight 不代表活进程、历史已解决阻塞不混入当前状态。
- `node tmp/architecture-audit-tests/ui-check.cjs`：12 项 DOM 行为通过，包括未确认在线、死 inflight、活 M 运行筛选、四阶段、二级展开、节点详情、人类要求入口、断线提示。临时 linkedom 仅装在隔离测试目录，未加入产品依赖。
- 两份 JS `node --check`、`git diff --check` 通过。
- Playwright CLI Chrome 启动因 crashpad/管道退出；浏览器连接工具要求审批而本次策略为 never，调用被拒绝。未进行真实浏览器布局/截图验收，DOM 不冒充浏览器证据。

运行现场：控制台 GET 可达；四个监督器 PID 文件存在、时间 11:56Z，但 API processAlive 均 false，CIM/任务计划检查受限制；不能据此确认重启后持续监督。固定 M ready、上次结束 10:03Z，无本次模型调用。U1 done=894097c，U3 exhausted，D0/O6 等原阻塞未修改。

应用边界：现有控制台会读取更新后的静态 HTML/JS/CSS，刷新可见；Python API 新增 manager.processAlive/blockers 需控制台后台正常重载后使用。本次未冒险重启未知生命周期服务；旧 API 时前端退化为未确认状态和审核待处理说明。核心任务监督、原模型绑定、暂停标记及生产卡不改动。

源码提交：本次环境明确将 `.git` 设为只读，不绕过限制写入索引或提交。改动保留在工作区，不编造新 SHA。
