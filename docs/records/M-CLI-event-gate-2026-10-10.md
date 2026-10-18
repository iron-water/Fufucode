# CLI 事件门房复核

15分钟 heartbeat 已经通过应用工具暂停，未删除自动化。旧 C:/tmp/gate/loop.mjs 是机制演示：正文正则分流、模块待接线强制打回、机器专属 exe 路径，不能直接作为生产监督器。

新增 scripts/agent-gate.ps1：文件系统事件唤醒及30秒纯本地补扫，没有交付不启动模型。按现有03/04/05事件协议筛选，忽略probe/transportTest，核对state、agent/worker PID和真实HEAD；不读取运行中正文。多交付写持久request，单实例锁，重复去重。

默认只收集。可选 Dispatch 用显式原生CodexExecutable及Model调用codex exec，stdin传提示，无shell拼接，输出保存JSONL。CLI审核者按Review入口落决定，不用正文关键词分流。dispatch claim在启动前落盘；失败或崩溃不自动重发，留needs_attention。该模式只审核、不集成、不派新任务，避免与Desktop M共享写入责任混淆。它是短生命周期CLI审核入口，尚不是旧提案中的常驻app-server M。

验证：pwsh -NoProfile -File scripts/agent-gate.tests.ps1，通过资格筛选、活跃PID守卫、probe/已审跳过、重放一致、零dispatch检查。夹具 C:/Users/mio/AppData/Local/Temp/fufucode-gate-6de0bd3d95984210949e296b0ede9b58 保留。真实主区 -Once 执行无待审，不启动模型。git diff --check 通过。

未验证：Dispatch真实模型运行、原会话回修、CLI主会话跨轮恢复、断电/并发主区写入、事件故障通知，以及长时间空闲观察。没有启动常驻门房或付费审核，没有接管Desktop会话。下阶段用显式devin/swe-2和独立临时仓库验证Dispatch，再做持久app-server thread与原OMP ResumeSession回修接线；复杂审核模型须按任务等级另选，不能把免费机制探针充当复杂审核能力证据。

启动收集（本地零模型）：`pwsh -NoProfile -File scripts/agent-gate.ps1`。单次检查加 `-Once`。实时投递尚未启用，不能声称新交付会自动进入当前Desktop线程。官方协议参考：https://learn.chatgpt.com/docs/app-server 。
