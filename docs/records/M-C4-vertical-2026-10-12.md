# C4 真实半自动纵向闭环

起点main@864c5933f65af8018a84aa6a3cd3cbfdbf0fc616，M单写L4；真实产品/工装mainSHA `058c753fc2c5b370ebebff49c6b94528e2bbe88b`，integrationStatus=integrated仅本增量。临时集成仓库不是Fufucode main，不计为发布。生产自动消费者未启用。

两个批准任务A/B进入同一U1库，唯一BackgroundHost依次执行。停写与进程归属/settle HEAD独立核对后，真实JSON卡形成三部分积压。固定人类fixture规则接受A.kept、打回A.fix、B暂不审核；过期并行审核拒绝，丢回执原ID重放。明确授权只修A.fix，消费者派生原runtime/model/session；真实devin/swe-2同Codex会话将fix BEFORE改AFTER并提交，kept字节/卡/决定及B保持。revision2独立接受时零集成marks；明确授权后在独立Git仓库fetch/cherry-pick，检查内容/clean再记真实SHA。重开持久库、旧卡迟到重放、原入队回执重放、空闲宿主不新增运行。固定审核规则不是模型自主审核。

最终源码真实证据：`C:/Users/mio/AppData/Local/Temp/fufucode-c4-ea762483c99342a8b496660212130903`。

| 身份 | 值 |
|---|---|
| CLI/模型 | Codex 0.162.1 / devin/swe-2；node+codex.js显式部署，路径只在ignored tmp/m-c4-runtime.json |
| A初次/返工同session | 01a1322c-ffda-7b21-b3e2-5350ea108182 |
| B session | 01a1322e-4b02-7eb1-800c-1dd581cb8c2a |
| A源提交 | 04f9f4f03556866d831911867ac07d248205c6ff |
| B源提交 | e7c729fb44ed99b0f4b4b3f967a86c379533705c |
| A修复提交 | 1a708c766c2af580f0adc56111e38b13b59564aa |
| 临时集成A HEAD | 8cd1169b60db4cd7ff6db4d0754656bcf012e780 |
| 临时集成B HEAD | 4bcbb6b2177c2e2798dc923b9ad7997747db7e70 |

result.json live=true/status=passed；SQLite三Completed/三marks一致，writer52404/15596/16108已退出。首次默认沙箱失败、第二次显式部署59通过、公共阻塞修复后最终59通过，各目录保留。总真实模型调用首次2+第二轮3+最终3，均免费档，不是仅3次总调用。

| 命令（workdir真实主仓库） | 实际结果 | 日志 |
|---|---|---|
| dotnet build Fufucode.sln -v quiet | 0警告0错误 | tmp/m-c4-solution-green.log |
| dotnet tests/Fufucode.Control.Tests/bin/Debug/net10.0/Fufucode.Control.Tests.dll | 511 PASS，含垂直/超时门 | tmp/m-c4-control-green.log |
| 同DLL --c4-live <absolute runtime.json> | 59 PASS，真实免费模型 | tmp/m-c4-live-final.log |
| 同DLL --c4-only | 59 PASS，无云请求 | tmp/m-c4-fixture-final.log |
| tests/Fufucode.Hosting.Tests/bin/Debug/net10.0/Fufucode.Hosting.Tests.exe | 267 PASS，含10边界 | tmp/m-c4-hosting-green.log |
| dotnet run --project tests/Fufucode.Harness.Tests --no-restore | 51 PASS | tmp/m-c4-harness-final.log |
| dotnet run --project tests/Fufucode.Core.Tests --no-build --no-restore | 340 PASS，Atria选跑SKIP | tmp/m-c4-core-full.log |
| dotnet run --project tests/Fufucode.Pi.Tests --no-build --no-restore | 21 PASS | tmp/m-c4-pi-full.log |
| dotnet src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.dll --selftest | 996 ok，0失败 | tmp/m-c4-app-full.log |

早期Control491仅增边界前证据，正式511。Hosting用DLL经dotnet首个owner检查失败，既有工装以Environment.ProcessPath作runtime、需要apphost；按exe复验267，未改断言。真实hold-second超时反例核实自己的host/supervisor/child PID+开始时间后清理，无活writer/Started请求/交付，未扩大生产终止权限。

必要修复：默认workspace-write拒绝.git，A如实blocked、B虚报module_ready但无新HEAD，均未接收。HarnessRuntime可选CodexSandbox保留默认/旧序列化，限定支持值/客户端无覆盖权，修复继承原值。隔离probe由M显式danger-full-access，全局配置和生产默认不变。

Windows creator PID1548关联1780/1908，但是数小时前系统进程。ChildrenOfWriter查实际birth，受保护进程只对该父PID补CIM创建元数据；早于writer排除，未知/后生仍阻断，无提升权限。tmp/m-c4-birth-proof.log真实RawChildren1780,1908→FilteredChildren空，五子进程边界通过；最早无完整诊断的C3-OWNERSHIP-DIAGNOSTIC-1仍open，本次明确原因另记verified。

端点读写分享冲突导致RestartBacklog失败；读分享ReadWrite/Delete，完整临时文件后File.Replace。File.Move(overwrite)旧read handle红例保留tmp/m-c4-boundaries-atomic-red.log，同例Replace通过。自有失败后台通过STOP正常退出。夹具LF/CRLF固定配置、null live配置拒绝回退。

C4通过限定真实Codex、显式部署和临时Git/U1；不证明四harness完整开发权限、生产库升级、服务安装/自动重启、自主审核或全项目终验。Unknown/失败返工后续路线、自动stop/权限预算、原生IME/DPI和新包另验。下一C5原生最小控制tab：同ControlClientSnapshot/事件、必需决定收件箱、既有浮动容器，不复制后台/JSON状态，不先重做图。监督Transport closed保持open、不重派；皮肤待选择，设置/辅助栏基线和固定候选保持。
