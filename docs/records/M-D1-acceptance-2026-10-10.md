# M 对 D1 的收编与验收

日期：2026-10-10。状态：已整合，G1 原生补验待完成。难度：M/G1 L4。

## 来源与整合

来源工作树 C:\Users\mio\.codex\worktrees\f80f\Fufucode，起点 83045c9；来源提交 8977a45、84846ef。核对 22 文件范围及源码，来源工作树干净，执行者交付卡声明停止写入。main 收编为 8427ddd、123a741。未 push、未改写历史、未回收工作树；主区用户原有修改和未跟踪文件未夹带。

## 独立复核

- 来源工作树实际重跑 --d1-only：28 PASS、exit 0。真实 Pi/OMP 进程与本地合成模型，不使用收费模型。
- 来源工作树实际重跑 --d1-regression-only：63 PASS、exit 0。
- 来源 solution 首次 --no-restore 因 Pi.Tests/TestApp 缺 project.assets.json 失败；正常 dotnet build Fufucode.sln -v quiet 还原后 0 警告/0 错误、exit 0。未归为产品回归。
- main 实际 dotnet build Fufucode.sln --no-restore -v quiet：0 警告/0 错误、exit 0。
- main 实际 Pi.Tests 全量：21 PASS、exit 0。
- main App 全量：630 ok、0 失败、exit 0。
- main Core 首次全量：314 PASS 后超时审批关闭联合断言失败，exit -532462766；目标文件不存在。两端定时器的旧同步假设已按 R-022 分拆为立即文件断言及有界关闭/文件断言；--bridge-only 18 PASS、exit 0；完整重跑 336 PASS、0 FAIL、1 SKIP、exit 0。SKIP 是未启用 Atria 付费云验收开关，未把该项标通过。源码依据为受管 Pi dist/modes/rpc/rpc-mode.js 的 createDialogPromise 与 Core ExpireUiRequestAsync，不放宽审批决定。工装修复提交 3b6f204。
- 来源证据：tmp/m-d1-review-tests.log、m-d1-review-regression.log、m-d1-review-build.log；main 证据：tmp/m-d1-integrated-build.log、m-d1-integrated-core.log、m-d1-integrated-pi.log、m-d1-integrated-app.log。证据均为本机隔离测试，不写用户生产数据。

## 门禁与未验证

接口与真实引擎创建/对话/允许/拒绝/停止/恢复经独立 28 项通过。原执行者报告原生选择、允许、OMP 历史恢复已验；M 未独立操作原生 UI。交付者明确未验原生拒绝/停止，G1 暂不解锁 D5 等下游，返回原 D1 会话补验。当前 M 工具未暴露 node_repl Computer Use 入口，不以回调测试代替原生证据。

默认 runtime/omp/omp.exe 尚未部署，本轮显式用本机安装的 OMP；包内与干净安装仍属于 D0/R/G3，不宣称发布可用。生产迁移、IME/DPI、多版本、云工具长任务未验证。云短文本试跑由执行者记录，M 未重复收费试跑。

## 下一步操作卡

- D1 原会话：继续本任务补验，L3（原生操作与缺陷判断）；建议 A 档处理缺陷，测试/验证执行仍显式最低成本可用模型。只补两后端原生拒绝/停止、取消选择及缺失 OMP 失败返回；运行时覆盖与私有数据，记录实际二进制/提交、操作、文件副作用、截图/日志。发现缺陷只在 D1 范围修复，小步提交并重新交付，不整合 main。
- O1 原终端 OMP：继续既有受限模块，不自行同步活跃工作树或修改共享入口。
- O3 可新增独立终端 OMP 会话，L3，建议 A 档（如已配置的 devin/swe-2），基于最新 main 独立工作树。仅 App/Features/Library、独立测试、docs/records/O3-library.md；Core 新模块另由 M 定范围，不改现有会话/元数据/宿主。G1 前不做真实聊天接线。
- G1 仍 open；D5/D2/D3/D4/O5 等等待补验及 M 判定。不自动创建新对话、不向其他对话发送消息。

补验 prompt：

```text
本会话继续 D1 补验，不开新任务。读取 AGENTS、execution-contract、session-handoff-protocol、session-difficulty、D1-backends 和主仓库 docs/records/M-D1-acceptance-2026-10-10.md。
保留当前独立工作树，核对 main 整合提交与本区改动，不 reset/clean、不切 Local。用已交付二进制与隔离测试数据，逐一原生操作 Pi/OMP 拒绝审批、运行中停止、取消后端选择及缺失 OMP 的失败返回。原生操作须记录文件无写入/停止后无继续执行及可再次发送的实际证据，不用回调测试冒充。模型验证显式最低成本可用模型，优先本地合成模型，不重复收费云试跑。
新缺陷在 D1 范围修复并定向验证、提交；工具无法操作时报告具体限制，不标通过。保留 IME/DPI 和安装边界。收尾主动更新 D1 记录并重新给完整交付卡，不整合 main、不 push、不接下一任务。
```
