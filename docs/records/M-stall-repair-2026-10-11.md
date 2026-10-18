# 门控停滞修复与四个概念边界

现场并非暂停：policy auto，gate/workflow paused=false。但U1有效rework与恢复claim不一致，另CHECK的解析失败使审核路径等待无结果；运行中视图还漏M。

本地决定对账现只采用已明确授权恢复的claim与一致Review事件/记录，重新核实HEAD、停止进程及M就绪；不重审、不写review.json。生产08:51:16UTC自动修复U1 claim，08:51:17UTC派原devin/swe-2/原worktree/原保存会话，08:54:51UTC重新交付bc86f5a6ba1ebe701accecaea8a03a6092b449a5。

失败CHECK报告作为失败证据交M独立补验，未标通过、未重发原请求。未知dispatching继续冻结。M0第二轮已审核接受并实际集成，原生Reconcile在范围、继承及独立验证后记录c5c3d5e902ce7d291f1e580e94dc1fc82b20e4da，日志 tmp/agent-flow/verify-M0-3ac17def541e487c8131f9222753f2d5.log。继承保护曾被本轮诊断文件触发，处理过程见R-069；没有放宽断言。

运行中视图纳入inflight固定M；审核与集成步骤按lastRunId区分，避免执行中的M显示为待门禁。真实浏览器已显示M集成线程（当时PID58608）；此后M完成，当前运行数按真实进程变化。

Wait-AgentFileWatch默认30秒纯本地补扫，并保留文件事件立即唤醒。无待处理交付时没有模型请求；持久claim与决定继续控制去重。不以周期补扫自动重发未知投递。

验证：14项控制台测试、gate隔离测试（含验证失败进入独立审核而不造成功结果）、原生恢复/已有决定对账/重放测试、本地超时及实际文件事件唤醒通过。最终隔离证据：Temp/fufucode-gate-6c67afb5f0bc4c80a8041a9e921276aa、Temp/fufucode-manager-recovery-184ea1aad40944bb8fb0046cba487b64、Temp/fufucode-watch-e26b1700e4304beba63b51667aebf2c0。全部零模型测试调用；真实开发会话原模型不变。

用户四处概念修正已写入 docs/tasks/automation-control-model.md：局部契约经M批准版本化落盘；集成失败先归因后分配写入者及重验路径；WAIT本地对账/漏事件/租约/循环/超期边界；项目候选指纹、独立最终验收及M决定。除已有入口及本次本地补扫，其余新增完整机制是实现要求，不宣称已由画图实现。
