# Pi 追新 SOP

## 当前执行约定（2026-10-08）

主 agent 按当前 AGENTS.md 负责追新与集成，不自动续接历史轨道或派单。当前锁定版为 **1.1.0**；用户本轮已授权升级，常规旁装、适配与验证不另设授权门。以后每次升级按以下步骤执行：

1. 从 npm 与认证 gh 查询发布版；选稳定 release，锁精确版本，不在产品启动时取 latest。
2. 旁装候选，保留当前 package/lock/完整旧运行环境；不改原始用户会话。
3. 运行版本相关的定向 RPC、审批与会话副本兼容验证。未知命名先审阅实际包/SDK，再增加严格 oracle；FAIL/SKIP 如实记录，不放宽断言。
4. 更新 product package/lock 与 FakePi 的已验证子集；确认实际安装后才执行落锁探针、必要构建/Core/Pi/App 检查。
5. 重包并检查包内真实版本/启动/完整性；生产数据回退另在副本演练，不能用 fixture 替代。
6. 更新 runtime-baseline、progress、verification 与追加 decisions；只提交本次内容，不推送。

1.1.0 探针的 `-Features` 可验证显式 builtin codemode/MCP 与内层审批；细节见探针 README。持续追新不是自动启用每个新功能：产品开关与界面需单独实施，实验性的 Durable/子代理接口先隔离核查。

## 历史轨道 SOP（不再自动执行）

main 发布基线锁定 **0.87.1**；`track-a-pi10` 按用户 A11+A12 授权独立升级到 **1.0.0**，不合并 main，待协调者完成 drill 后决定集成。本 SOP 本身不授权升级。探针 FAIL 是差异证据，由协调者裁定是否进入迁移；不得为升级放宽断言。所有操作在所属 worktree，凭据不得写入报告。

| 勾选 | 步骤 / 执行者 | 产物 / 验收 | 失败动作 |
|---|---|---|---|
| [ ] | 1. A agent 旁装指定精确版本到 `tmp/pi-<version>/`；先记录当前 package、lock、版本指纹 | 新安装目录及独立 lock；产品 `runtime/pi`、产品 lock 保持原样 | 安装失败保留日志，核对指定版本和网络；不能换 latest 或覆盖产品运行环境 |
| [ ] | 2. A agent 运行 `scripts/pi-probe/run-probe.ps1`，先当前锁定版、再旁装版 | 两份 `probe-report.json`、原始 RPC/SSE 摘录、10 类 PASS/FAIL/SKIP，fixture 双向读取及 FakePi 套件自测 | 超时/退出/断言失败保留实际帧和 stderr；SKIP 写原因；修探针实现错误后重跑相关项，不改预期来制造通过 |
| [ ] | 3. A agent 比较两份报告和命令快照 | 破坏性变更、新增能力、行为等价三档；新增命名/默认扩展、命令来源、JSONL format 分开记录 | 无有效旧版报告或输出不完整时不能给出兼容结论；补测缺项 |
| [ ] | 4. A agent 追加 `docs/runtime-baseline.md` 对应版本探针章节 | 日期、包版本、Node、参数、逐项结果、证据路径、未验证边界 | 实际 FAIL 保留，报告与文档不一致先纠正；不得更新头部当前锁定版本 |
| [ ] | 5. 协调者决策门 | 明确批准的升级切片：破坏性清单为空或迁移成本可接受；记录适配范围、退出条件和旧环境保留位置 | 有未裁定破坏性变化、审批失效、JSONL 不兼容或关键 SKIP 时挂起升级，写所需接口/修复/补测；探针 FAIL 不自动意味着放弃 |
| [ ] | 6a. 获授权的 A agent 更新产品 Pi 精确版本与 lock | 可复现安装、完整性检查；旧 Pi/Node 与升级前数据副本独立保留 | 不一致/安装失败不发布，恢复该切片产品配置；不对原始用户数据尝试降级 |
| [ ] | 6b. A agent 对齐 FakePi 契约注册表和版本字符串常量 | 逐条 RPC/事件引用 baseline 实测章节；对协议变化新增独立覆盖 | 未核实协议不猜；旧断言仍表述有效行为时不得放宽；必需语义变更由协调者评审 |
| [ ] | 6c. A agent 构建、运行 FakePi 管道/协议测试、Core 全组、App selftest；B agent 执行审批桥/演示/升级观察验证组 | 各组真实退出码和原始结果；锁定组数量与跳过理由，真实模型与合成端点分别标注 | 任一必需组失败停止发布，定位相关契约；不得减少断言或用合成结果代替真实模型 |
| [ ] | 6d. F / 协调者重包与完整性、安装演练 | `scripts/packaging/package.ps1` 和当前 F 任务书的 drill；CLI/GUI 均从包路径启动 | 包不完整/入口失败回到接线修复，不复用旧包宣称新版本已验 |
| [ ] | 6e. F / 协调者跨版本升级与回退演练，A 提供协议判读 | 脱敏真实旧会话副本：旧包读写→新包读取/续写→保留新数据副本→旧包读取恢复；同时检查 SQLite、项目归属、父子引用、设置和会话锁 | JSONL/metadata 不兼容时停止降级写入；恢复升级前数据备份或留新版恢复路径；保留新写数据，不覆盖原会话。仅 fixture 兼容不能代替此演练 |
| [ ] | 7. 协调者收尾，A 更新获授权文档 | queue/progress、升级决策、当前锁定版本头部及证据，注明回退限制 | 验收未齐全则保持进行中/待外部条件，不能发布或标完成 |

## 执行约定

- 探针使用应用管理的 Node、明确的 PiRoot、隔离临时项目和 agent dir；默认不读取用户认证。生命周期用本机合成 HTTP SSE，审批加载真实 fufucode-bridge，不能将本机端点说成真实供应商验收。
- 每次 probe 输出放独立目录，不复写旧证据。任意 PiRoot 不等于所有未知版本保证兼容：版本敏感断言未有明确预期时报告未知/SKIP，交决策门。
- fixture 是锁定版产生的脱敏 JSONL；读取既检查 RPC 成功也检查 entry 内容/结构，不能只测进程存活。双向检验在副本上执行并记录内容，原 fixture 保持不变。
- FakePi 自测区分传输/断言测试和真实 Pi SDK/扩展行为。故意损坏返回字段的负向测试必须证明报告 FAIL 与非零退出；FakePi 无法实现的真实扩展加载/SDK 兼容不能标 PASS。
- 全组参考入口：`dotnet build Fufucode.sln --no-restore`；`dotnet tests/Fufucode.Pi.Tests/bin/Debug/net10.0/Fufucode.Pi.Tests.dll`；`dotnet tests/Fufucode.Core.Tests/bin/Debug/net10.0/Fufucode.Core.Tests.dll`；`dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest`。先确认实际输出目录和当前任务书；需真实模型的组按该组配置要求执行并如实记录 SKIP。
- 本轮 A9 仅旁装与探针、FakePi 结构改造和 SOP；以上升级/重包/真实用户数据演练均在决策门以后。

## 探针调用

在仓库根目录执行（输出路径每轮使用新目录；`-Project` 仅用于明确准备的临时项目）：

```powershell
# 首次建立旧版 fixture；已有 fixture 时省略 GenerateFixture。
./scripts/pi-probe/run-probe.ps1 -PiRoot ./runtime/pi -ComparePiRoot ./tmp/pi-0.99.2 -Output ./tmp/probe-baseline-run -GenerateFixture
./scripts/pi-probe/run-probe.ps1 -PiRoot ./tmp/pi-0.99.2 -ComparePiRoot ./runtime/pi -BaselineReport ./tmp/probe-baseline-run/probe-report.json -Output ./tmp/probe-candidate-run -SelfTest
```

`probe-report.json` 为逐项事实，`probe-report.md` 为人读摘要，`probe-diff.json` 为上一版命令/来源/状态差异。返回 0 表示运行的全部探针通过；1 表示 FAIL；2 表示没有 FAIL 但有 SKIP。不能把 2 当完整兼容通过。启动/HTTP/RPC 证据留在报告目录；升级审批在报告生成之后。
