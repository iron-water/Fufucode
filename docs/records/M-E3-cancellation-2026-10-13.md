# E3 扩展安全重启取消：M 交付记录

任务 E3-M-HOST 的取消/关闭增量｜L4｜M 自做共享宿主与外壳。起点 main `2c7d492e98b38993152cc5ed05d81ae193df8b2a`；实际源码 SHA `92abe0559e06ad3d7372f83f04eee0d71a0d45f3`（5源/自测+本记录/缺陷登记，共7允许文件）。原 R1/E3 thread 停写、HEAD 分别 4097328/8c67289，未重复派发、同步或发送轮询消息。R1 继承的 runtime/node/LICENSE 改动保持。

## 发现与修复

- 真实 Pi 反例确认：已取消的调用仍释放原进程，再用取消令牌恢复失败；预取消不应改变可用的原连接。宿主应用入口现在先通过可取消的互斥门及令牌检查，未进入核心时不改启用列表、不释放进程。
- 候选初始化中取消后，旧进程已释放，但 Manager 留着候选加载路径；显式“重试”可能再次执行该候选。本增量把未确认恢复异常的运行配置还原为原活动列表，不自动开恢复进程；持久启用列表保持旧值，候选文件不删。
- 外壳原 catch 排除了 OperationCanceledException，取消后仍显示无限“正在重启”。现在明确区分：原会话仍活则重接原 Chat 并移除遮罩；旧进程已释放则结束进度、提供明确恢复/打开其他项目入口。窗口已关闭时不更新迟到 UI。
- 宿主 Dispose 与扩展 Apply 共用同一门。关闭先标记 disposed，再等待在途应用退出并释放最终 Current，避免关闭先释放旧对象、漏掉迟到的新对象。disposed 的 HasLiveSession 为 false。此门不改变用户普通活动 Apply 的 pending 策略，也不把取消未知结果当成成功。

## 实跑验证

| 命令 | 结果/证据 |
| --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet` | 0 警告、0 错误，exit 0；`tmp/m-cycle48-solution-build.log` |
| App DLL `--selftest --local-plugins-cancel-real-only` | 18 PASS、0 失败、exit 0；`tmp/m-cycle48-cancel-final.log` |
| App DLL `--selftest --local-plugins-real-only` | 原 12 项安全应用/更新/失败恢复/回退及离线恢复全部通过，exit 0；`tmp/m-cycle48-shell-real.log` |
| App DLL `--selftest` | 全量 1255 PASS、0 失败、exit 0；`tmp/m-cycle48-app-full.log` |
| Core / Pi 测试 DLL | 359 PASS + 1 可选云测试 SKIP / 21 PASS，均 exit 0；`tmp/m-cycle48-core.log`、`tmp/m-cycle48-pi.log` |

实际 DLL 位于各工程 `bin/Release/net10.0/`。运行同 App DLL 的真实探针和全量串行，不在运行时重建它。普通 dotnet 测试无模型请求；Pi 探针使用临时项目与数据、显式 `proof/devin/swe-2` 离线 provider，只有无害状态查询和自有插件初始化，没有云 prompt。Core 可选真实云开关显式为 0。

18 项覆盖：预取消原连接/代次/列表/维护保持、预取消外壳真实连接和草稿保持；真实候选进入后取消、同一在途应用重复请求拒绝、原加载配置与候选源保持、进度结束、明确重试恢复原 sessionID/草稿/空启用列表；初始化中关闭不恢复窗口/会话；真实项目信任放弃只询问一次并保原进程、未初始化候选、不留遮罩。

插件初始化用磁盘 entered 标记加真实 Node 定时器等待，只在真实初始化进入后取消。关闭与取消后，只读 CIM 核查本次专有根 `fufucode-plugin-cancel-d0e11f419b494071865a122a880fbace` 的 Node 进程，残留 0，证据 `tmp/m-cycle48-cancel-processes.json`。它不是整机全部未知进程的释放证明。

## 红证据与边界

- `tmp/m-cycle48-cancel-red2.log` 在未修产品源码上明确 5 条产品 FAIL：预取消连接、预取消配置、初始化取消配置、取消后进度、关闭中取消配置。原事实断言不放宽，最终同检查通过。
- 首工装 `cancel-red.log` 的无限未完成 Promise 没有 Node 活跃句柄，Pi 自然退出，不能拿它声称真实挂起初始化。改用自有定时器后真实 entered/未就绪门通过。保留首日志，不登记成 Pi 产品缺陷。
- 第二工装信任夹具使用 AGENTS.md，但本实现保护资源是 `.pi/settings.json` 等；实际没触发信任，末尾 get_state timeout 属夹具错。改为真实保护文件，最终原 trust decline/asked==1/旧连接不变强断言通过；不虚构“AGENTS.md 被信任门拦截”。产品五红与这项工装异常分开记录。
- Computer Use 技能及 runtime 指引已读取；当前工具目录没有 node_repl，无法按技能初始化 @oai/sky。没有自造 UI helper 或用无头冒充原生；系统键盘/IME/DPI/真实文件选择器仍未验证。
- 未验证：关闭发生在成功载入/持久写入的更晚瞬间、取消后真实加载 unknown 回执的人类核对、长时间压力/生产数据迁移，以及含本增量的新安装包。上述待验不能据 18 项一概标通过。
- 未知 WindowsSandboxServer PID15712/出生仍相符，原候选安装门继续 defer，不能停止未知实例。R1 固定 cc44eee 不含本轮，保旧工件；另行冻结新候选才能验当前插件源码。
- 主区可续期独占 PID20552；编辑/提交前活检与续期，原 demo 和全部未跟踪目录保留、不入本次暂存。无 push、历史改写、树清理、登录或付费操作。
