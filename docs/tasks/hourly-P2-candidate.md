# P2 固定版本桌面候选包

推荐模型：`group/auto-qfmodel`。难度 L3：打包脚本、运行时和安装证据边界需要联合判断；纯模型试跑只允许显式 `devin/swe-2`。

产品固定起点：`e64dd1b15e1cfdc1de99afae7d4cc3c32c2f047e`。新工作树含任务书提交，但产品来源按此 SHA 记录，包装修复额外列 SHA。P1/V3 已有原生补验记录，不代表 IME/DPI/全部生产门禁通过。B1 尚未集成，不是本包依赖。

读取 AGENTS.md、AI_DEVELOPMENT_PLAN、execution-contract、session-handoff-protocol、development-topology-v2、production-delivery §8，以及 M-V3-citation 与 V3 原生记录。所有命令显式指定自己的工作树目录。

目标：交付能解压启动、保留固定来源和校验值的候选 ZIP/安装程序，真实验证安装启动卸载；外部环境阻塞单独记录，不阻止候选构建。

允许：scripts/packaging/**、labs/packaging/**（只任务相关报告/许可）、docs/records/P2-candidate.md。产物和临时日志放 out/、tmp/，不提交二进制。不改 App/Core/Pi/共享解决方案/权威进度，不改第三方 reference、不改用户数据和全局运行时配置。确需其他修改写协调卡，继续独立工作。

步骤与验收：
1. 核对实际 HEAD/干净状态和已有运行时。工作树忽略的 Node/Pi 可能缺失：只读检查主区已托管版本，可复制到本工作树并记录版本/校验/来源，不引入机器特定路径到代码。不得全局 npm 安装、改 provider 或下载付费资源。
2. 读取并使用真实 package.ps1、build-installer-version.ps1、integrity.ps1、verify-clean-start.ps1 参数；修复实测阻塞，小步提交。App/CLI 自包含构建 0 警告 0 错误；App 全量 --selftest、Core/Pi 适用现有测试完整运行并记录命令/退出码，不将构建视为行为验收。
3. 候选在新临时目录解压、临时数据根启动/恢复，验证包内 Node/Pi/桥接发现；OMP 的外部/打包依赖真实记录，不假称干净机器双后端可用。针对打包内容检查旧机器/worktree硬编码，而非全仓防错扫描。
4. 构建版本化安装包并记 SHA256、大小、来源 SHA。安装/卸载仅在干净 Windows Sandbox 或已有明确隔离环境执行，不能在用户宿主机器安装/卸载产品。使用现有 run-sandbox/sandbox-acceptance；历史 Sandbox 失败不直接当当前失败，真实尝试并保存新诊断。环境不可用不启用系统功能/修系统/重启：标 blocked，并完成可做验证。原生验收只有你一条线操作，不干扰其他用户窗口。
5. 交付记录列实现/实跑/未验/阻塞，安装启动卸载逐项证据，候选不叫生产正式发布。不得 push、改写历史、清理工作树、登录、采购、公开部署。结束后提交允许文件、交付卡列完整SHA/路径/实际命令/共享接线需求，停止写入，不自行开下一任务。

保留继承改动，未明确的选择按最保守可用实现并记录。无需等用户确认；局部阻塞继续本包其他步骤。
