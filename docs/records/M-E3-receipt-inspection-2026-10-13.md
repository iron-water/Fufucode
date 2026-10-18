# E3 原回执与当前加载只读核对

任务 E3-M-HOST 的原回执核对增量｜L3｜M 自做模块/共享验收。起点 main `0593b9e1bff1b811e4a03d319c409d3e214c8115`；实际源码 `52a8f3c3a1f33d6b59c8d39623bc27ea4fb0628b`（8源/自测+记录/登记，共10允许文件）。原 R1/E3 均 notLoaded、latestTurn completed/cursor 未变，无新交付，不重新创建/投递；未知 Sandbox PID15712/出生仍相符。

## 实际行为

原服务已经持久保留 cancelled_unknown 与近20条回执，但扩展页不显示原标识/原版本/原结果，用户无法按原请求核对。先加真实编译页面反例，`tmp/m-cycle50-receipt-red.log` 一 FAIL，另两条确认原 unknown 保真和目录字节不变。

本增量在同一插件模块显示最近应用回执，可选择、复制原 token/版本/结果/说明。新增“只读核对当前加载”，始终绑定所选插件和原 token；没有原 token 拒绝，不改用最新回执。观察显示核对时间、目录代次和可用的实际宿主上下文。

`InspectReceiptAsync` 只读目录与宿主快照，检查原固定版本、精确入口、兼容与摘要，并重新读取目录状态。期间目录内容改变则 Stale；缺宿主/身份不符/原副本被篡改等保持 Unknown。只能观察“目前加载原入口”或“目前未加载原入口”，不认证原请求曾成功/从未执行，不改 Selected/Pending/原回执，不自动 Apply/Restart/模型请求或解除在途保护。同包另一文件不能当原入口已加载。

VM 在 await 后核对当前选择与 token；选择改变/关闭时不把旧结果放入新选择。普通刷新/临时页切换保持既有模块生命周期；原动作及原 pending 契约保留。本轮不增加“重发原请求”或“解除在途保护”按钮。

## 实跑验证

| 命令/产物 | 结果 | 证据 |
| --- | --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet` | 最终0警告0错误、exit0 | `tmp/m-cycle50-frame-build.log` |
| App DLL `--selftest --local-plugins-only` | 原52+新15机制/无头检查全部通过 | `tmp/m-cycle50-frame.log` |
| App DLL `--selftest --local-plugins-real-only` | 原12+新2=14真实Pi外壳检查通过、exit0 | `tmp/m-cycle50-shell-real.log` |
| App DLL `--selftest` | 最终1270 PASS、0失败、exit0；之后仅测试截图增加强制渲染tick，原15断言不变并定向再过 | `tmp/m-cycle50-app-final-full.log` |
| Core / Pi DLL | 359 PASS+1可选云SKIP / 21 PASS、均exit0 | `tmp/m-cycle50-core.log`、`tmp/m-cycle50-pi.log` |
| 插件独立DLL默认入口 / `--ui` | 原服务166+proof规则21 / 无头UI46全部通过、exit0 | `tmp/m-cycle50-services.log`、`tmp/m-cycle50-ui.log` |

DLL 使用各工程 `bin/Release/net10.0/`。同 App DLL 构建、真实Pi及默认全量串行；独立Core/Pi/模块测试可并行，但不并发重建任何运行DLL。真实Pi仅临时数据/自有插件、显式 proof/devin/swe-2 离线 provider，没有云 prompt 或昂贵测试 lead；Core可选真实云开关显式0。

新15默认断言包括 cancelled_unknown 不改、Loaded/NotLoaded 与原结果分开、同包错误入口、篡改/不兼容保持unknown、缺token拒绝、检查目录字节与Apply次数不变、真实编译列表/按钮/只读结果、滚动后的实际viewport几何、并发目录变更Stale及选择改变迟到拒收。此组host为明确夹具，不能冒称真实加载。真实Pi额外2条通过既有安全应用实际初始化证明核对，并保原catalog精确字节/sessionID不变，属于真实外壳+Pi证据。

初次新增真实测试少命名空间，`tmp/m-cycle50-solution-final-build.log` 1编译错误保留；viewport工装少Avalonia扩展命名空间，`viewport-build.log` 2编译错误亦保留，增加实际using后最终零警告错误，没有降级/抑制。M实际查看最终`C:/Users/mio/AppData/Local/Temp/fufucode-plugin-receipts-503f3b1fc7894b119ede3881ee22485d/receipt-page.png`：原回执/token/结果可见，但结果面板仍未出现在捕获像素内，即使layout/BringIntoView/强制tick后的几何断言通过。此差异登记E3-RECEIPT-PIXEL-1 open；不把几何或绑定通过冒称结果面板像素已验，亦未确认原因是捕获延迟还是可视问题。

## 未验证与下一步

- 当前加载观察不是原操作的最终审判；此次没有改 cancelled_unknown 为 confirmed、没有解除持久在途保护或自动重发。跨进程残留在途的明确安全释放/人类决定仍待；同样保已提交后取消边界。
- 系统原生指针/文件选择器/键盘/IME/DPI仍未验，当前工具无node_repl；无头像素与注入点击不能代替原生输入。生产迁移/压力/含当前源码的新安装包仍待，不宣称插件整项/生产六门已验收。
- 原R1继续固定cc44eee，不含后续插件。未知Sandbox不停止/接管；只有可证明空闲才给原会话有限续验同一固定安装门，不重建/追main。
- 主区续期独占PID37544，编辑/提交前活检续期，保原demo和所有未跟踪状态；无新派、push、历史改写、树清理、登录采购部署。后续M继续明确原请求核对与剩余释放/生产门。
