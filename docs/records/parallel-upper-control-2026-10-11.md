# 半自动上层控制交付

范围：用户要求先优化半自动控制架构并实现，底层持久执行/harness 对接与全自动更迭分开。主区写入期间持有 main-writer FileShare.None 租约，未接管任何原执行会话。

实现：`scripts/parallel_control.py` 上层 reducer 与本机状态存储、受保护 `/api/development` / development command；HTML `parallel.html/js/css`。全人工默认，半自动批准后产生执行意图，全自动仅保留身份、不启用新策略。原自动门控 `/index.html` 可访问，未切换或暂停其监督。

规则：工作包与线程解耦；独立/竞争/互补/流水成果策略；竞争必须选定已接受方案；互补证据齐全后才排集成；接受≠集成；部分打回保留其他部分；原模型/线程保持；拆分、合并、转交生成后继包；活跃写入者等待停止、未知状态冻结；公共知识提案/发布/采用分离；定向问题保留来源；命令去重与预期版本校验。协议报告仅受信 adapter 可用，HTTP 人类请求不能伪造实际执行。

验证：
- `python -B scripts/parallel_control.tests.py`：16 PASS，全部为本地规则/文件行为，无模型。含竞争非全合并、未验候选拒绝、互补证据、流水集成后解锁、部分回修、循环拒绝、运行中重组等待停止、未知运行拒绝重组、依赖重映射、组版本择优失效、契约采用使旧审核失效、幂等重放与重开。
- `python -B scripts/automation-console.tests.py`：16 PASS，包含此前受权限影响的原生批准夹具，本次环境已解除该阻塞。
- JS 语法与 git diff --check 通过。
- 真实 Playwright 隔离服务 18766，数据 `C:/Users/mio/AppData/Local/Temp/fufucode-parallel-browser-qeihn2sg`；确认初始全人工，切半自动，创建 A，批准，登记交付，明确 rework，刷新仍保存。页面不显示假线程运行。
- Chrome 初次因页面文件不足 0x5AF 失败，后续重试成功；表单检查发现 HTML pattern 的 Unicode v 模式短横线必须转义，已修正。快速模式保存时打开表单可能拿到旧版本，加入保存中不开放新对话，仍保留服务端版本检查。
- 600px 页面截图 `tmp/architecture-audit-tests/parallel-mobile.png` 已目视检查；待执行意图与底层未启用说明可见。
- 后续真实浏览器完成第2版交付 → 人类接受 → 独立集成批准事项出现；并断言600px无横向溢出。桌面截图 `tmp/architecture-audit-tests/parallel-desktop.png` 已目视检查，明确显示已接受、尚未集成。

本次新页面操作只修改隔离夹具。真实控制台后台通过已有 Stop/Start 正常重载，GET /api/development 确认 manual/revision0、runtimeDispatch=false/nativeIntegration=false；不写生产任务/审核决定/暂停标记。没有模型请求。

未完成项：原生 Avalonia tab、自动模型拆包、实际 worktree/harness 派发与回收、原会话定向消息、真实差异/基准比较、语义冲突分析、执行意图消费和真实集成。上层已可操作与落盘，不把这些下层能力冒称完成。现有组内重组保留原关系；跨多个关系组的复杂迁移明确拒绝，需后续显式新关系方案。

随本次提交收编上一轮架构审计/控制台真实性修正的未提交改动；继承 demo-verify JSON、用户意见及其他未跟踪文件均保持原状。
