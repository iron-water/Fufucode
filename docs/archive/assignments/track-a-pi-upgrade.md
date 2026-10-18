# 任务书 A9+：Pi 追新基建（探针套件固化 + 追新 SOP）

> 派发：协调者 → 轨道 A（track-a-core worktree，桌面 track a 线程或 headless）
> 前置：A9 首轮 0.99.2 探针（本任务书 §3 第一步即复用其产出）；main 保持 0.87.1 直到决策门放行。
> 所有权：只写 `src/Fufucode.Core`、`src/Fufucode.Pi`、`tests/`、`scripts/`（除 bridge-verify/demo-verify/packaging/upgrade-watch 外）、`docs/runtime-baseline.md`、`docs/operations/pi-upgrade-sop.md`（新建，本文授权）、`docs/progress.md`。

## 1. 背景

Pi 上游节奏加快（0.87.1 → 0.99.2 跳 12 个 minor，0.99.x 三天三版），项目将长期追新。现状追新是"可控的半自动"：协议收口单层（Core）+ lock 锁定 + 272 项验证网 + runtime-baseline 对照源，但探针靠手工、FakePi 靠猜、步骤靠记忆。本任务书把追新固化为基建。

## 2. 交付物

### 2.1 探针套件 `scripts/pi-probe/`

把 runtime-baseline.md 的实测口径变成可执行套件：

- `run-probe.ps1 -PiRoot <目录> [-Project <临时项目>]`：对任意 Pi 安装目录跑全套探针，输出 `probe-report.json`（逐项 PASS/FAIL/SKIP + 原始输出摘录）+ 人读摘要。
- 探针项（首轮以 A9 实测脚本为蓝本，逐项固化）：
  1. 启动与 `get_state`（含模型列表非空）；
  2. `get_entries` 全量 + `since` 游标增量语义；
  3. `prompt` → 流式事件 → `agent_settled` 生命周期（用本地合成端点，不依赖真实凭据）；
  4. `steer` / `follow_up` / `abort` / `clear_queue`（含 clear_queue 返回 steering/followUp 数组结构）；
  5. `set_model` 成功与失败两路；
  6. 真实 fufucode-bridge 加载 + 审批 confirm 往返（放行/拒绝双结局）；
  7. 扩展启动失败诊断路径（坏扩展 → extension_error 可见）；
  8. 内置命名口径：RPC source info / 诊断里的扩展名字符串（`<inline:name>` vs `builtin:<name>`）逐条断言并输出实际值；
  9. `get_commands` 返回结构快照（与上轮版本 diff 输出）；
  10. 会话 JSONL 兼容：用 fixture 会话文件（0.87.1 生成一份入库 `scripts/pi-probe/fixtures/`）做旧版写→新版读、新版写→旧版读双向验证。
- 套件本身带自测（对 FakePi 跑一遍证明断言有效）。

### 2.2 追新 SOP `docs/operations/pi-upgrade-sop.md`

Checklist 形态，agent 可代执行，含决策门：

```
① 旁装新版本到独立目录（不碰 runtime/pi lock）
② run-probe.ps1 对新版跑全套 → probe-report.json
③ 与 0.87.1（或当前 lock 版本）基线 diff：破坏性变更/新增能力/行为等价三档清单
④ 报告追加 runtime-baseline.md 新章节（版本号+日期+逐项结果）
⑤ 决策门：破坏性变更清单为空或可低成本迁移 → 进入升级切片；否则挂起并写明阻塞点
⑥ 升级切片：更新 runtime/pi lock → FakePi 对齐（按 §2.3）→ dotnet 全组（build/fakepi/selftest/core-tests）→ 重出包（协调者跑 package.ps1+drill）→ 真实旧会话数据跨版本升级/回退演练
⑦ 收尾：queue/progress 记录，runtime-baseline 头部"当前锁定版本"更新
```

每步标注：执行者（agent/协调者）、产物、失败时动作。SOP 写明"探针 FAIL ≠ 立即放弃"——输出差异后由决策门裁定。

### 2.3 FakePi 契约化（结构改造，行为不变）

- FakePi 每条 RPC 命令/事件改为显式契约实现（一个 handler 一条，集中注册表），行为与 0.87.1 逐条对齐并注明锚点（baseline 文档章节）；
- 升级时只 diff 注册表，不为猜行为改散落代码；
- 本轮**不改任何测试断言**、不放宽任何检查；272 项测试必须原样全绿（这是行为不变的证明）；
- 内置命名等版本敏感字符串集中到常量层（如 `PiProtocolStrings`），升级时一处改。

## 3. 执行顺序

1. 若 A9 首轮探针已出报告：先把其脚本逻辑收编进套件；未跑则本任务书含 A9 探针（旁装 0.99.2 → 手工探针 → 报告），再固化。
2. 探针套件 + 自测（对 FakePi 与当前 runtime/pi 0.87.1 各跑一遍，全 PASS）。
3. SOP 文档。
4. FakePi 契约化重构 + 全组回归。
5. progress.md 记录（含未验证项）。

## 4. 硬约束

- 未实测不得标 PASS；探针断言不许为凑通过放宽（套件自测证明断言有效性）；
- 不动 `runtime/pi`、安装包、其他轨道路径；
- 禁 git commit/merge/push；改动留 worktree 由驱动器代验代提；
- 0.99.2 探针结论只进报告，升级决策归协调者。
