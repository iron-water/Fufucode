# 无人值守交付队列：首轮落地

用户授权M全权开发，并要求仅受管agent完工后接收交付卡，允许多卡积压。2026-10-10实现scripts/agent-queue.ps1及执行协议补充；不接管已有用户会话。

## 实测

- 本机OMP 18.8.5帮助确认print、model、session-dir、resume、max-time、auto-approve参数；已配置magpie的group/auto-gemini-3-8-flash。
- PowerShell解析通过。免费devin/swe-2无工具探针probe-01：exit0、QUEUE_PROBE_OK、probe_passed，worktree无改动。
- 免费无工具transport-01/02均exit0，结束后分别生成delivery.json并保持unreviewed；第二份到达不覆盖第一份。首份运行中观察delivery不存在。两份均为机制夹具，不是产品交付。
- S1已运行时在同worktree启动resume-probe-01，命名Mutex拒绝第二写入者，state=supervisor_error且未启动第二OMP。这验证排他，不验证resume成功。
- 实测证据在主区tmp/agent-queue各运行目录；没有提交模型会话、凭据或运行日志。只提交脚本及文档。
- Codex create_worktree返回Not a git repository（当前应用绑定旧路径）；用真实仓库git worktree add -b agent/s1-unattended创建C:/Projects/Fucode/Fufucode-agent-s1，基线7349405，干净独立。没有删除或重置任何既有工作树。

## 已派任务

S1，L2，推荐group/auto-gemini-3-8-flash；实际OMP解析使用magpie/group/auto-gemini-3-8-flash。选择依据是受限视觉样稿和多状态展示，不改生产契约。允许docs/frontend-prototype/skins和docs/records/S1-skins.md。起点7349405。主区未提交人类风格意见只读，不夹带。

已通过队列Launch启动隐藏Worker/OMP；job=s1-01，最初Worker43852、OMP36024（PID只作历史证据，未来不能据此直接操作进程）。最终prompt位于tmp/agent-queue/s1-01/prompt.md。该任务尚在运行，本轮未读取其交付卡或验收代码。

后续事实更新：s1-01在2026-10-10T16:35:14Z退出1，工具调用请求被供应商拒绝：unknown variant AUTO，期望auto/any/none，state=failed_or_missing_card，没有delivery、没有代码改动。这是实现前路由兼容错误，不是代码打回；保留原卡/日志，未改全局供应商配置。M改用启动备选group/auto-deepseek-v4-1-flash（L2不变），通过ResumeSession恢复同一任务的保存会话，唯一新运行目录s1-02，仍在原受管worktree。后续只检查PID/state，不读取运行中正文。此兼容回退不改变已有代码打回应保留主模型的纪律。

## 主会话唤醒

codex-app-tools的automation_update创建heartbeat成功，id=fufucode，ACTIVE，每15分钟唤醒当前主会话；设置文件读回核对target_thread_id=01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07。只在新审核结果、真实故障或外部决策时通知，无变化安静。忽略probe/transportTest。运行记录按review.json去重。

使用[OpenAI Docs自动化说明](https://learn.chatgpt.com/docs/automations?surface=app)核查背景约束。定期唤醒已经配置，不代表第一次定时运行、真实修复恢复、收编闭环、重启恢复已经验证。电脑/App不可用时不能承诺持续审核。

## 尚未验证

真实S1成果、任务范围遵守、原生/浏览器截图、一次完整审核打回收编、ResumeSession真实修复、看门狗强制超时、异常重启后的恢复、多个生产任务并行均未完成验收。脚本卡片存在判断只作传输筛选，M必须独立复核实际提交与行为。G1未通过，不解锁D5；风格选择仍待用户；无push/发布/安装或真实用户数据清理。
