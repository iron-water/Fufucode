# M：随现有CLI交付的后台宿主入口

实际实现mainSHA `cc44eeef02c84c82847e104d31186a08f3d3c6a1`。本轮源码/门在提交前核验，提交后仅补文档；CLI入口集成不等于H1或整项自动化完成。

用户继续轮，M公共实现，主仓库main起点b644a3f，独占租约与继承状态保护。之前R1两包清单没有独立Hosting/Control DLL被错误解释为对应代码缺失：实际源码都在Fufucode.Core同一工程/程序集，不需要同名DLL。**纠正先前判断**；实际缺的是发布物可调用的BackgroundHost启动入口，HarnessHost旧工具只有register/run/status单次执行，不启动这个唯一常驻宿主。

新增现有CLI的 `host serve|status|stop <absolute-host-options.json>`，复用Core BackgroundHost/HostJournal/HostClient。配置含Database/EvidenceDirectory/HostId/EndpointFile及现有端口/节拍/事件上限；要求绝对路径、既有已初始化权威库、64KiB以内JSON和有界值。serve明确启动并按既有槽位拒绝第二宿主；status/stop先核对实际HELLO的HostId，错身份不发STOP。正常停止保原Host clean-stop记账，Ctrl+C明确请求自身停止；不停止未知实例，不开启AUTO、不创建/迁移另一库。

该入口被既有package.ps1的CLI publish自然包含，无需另造独立控制程序集。旧冻结候选不含新增入口，不能反推它已经可用；桌面Pi/OMP接公共宿主H1、自动消费/手机仍另验。

## 实际验证

- 标准solution构建0警告0错误，tmp/m-continue43-final-standard-build.log。
- 新官方CliHostTests纳入Core全量：9项本地真实loopback/权威库/身份/停止检查，独立--cli-host-only已0失败，tmp/m-continue43-host-suite.log。
- 独立进程临时authority/cwd脚本 `pwsh -NoLogo -NoProfile -File tmp/m-continue43-host-check.ps1`：11检查0失败，证明第二宿主被拒、错误HostId不能停止、真实status与正常退出。临时证据root fufucode-cli-host-o9c2m65_。
- self-contained CLI独立发布后同11检查0失败，tmp/m-continue43-cli-publish.log、published-host.log；root fufucode-cli-host-b01tdxjh。真实exe无需dotnet launcher；没有模型请求、没有真实用户库或宿主安装操作。
- 最终Core/Pi/Hosting计数与实际源码SHA在门完成后补录，未跑的门不预先写通过。

这只是可部署宿主入口与生命周期，非完整自动开发/手机或桌面H1验收。OMP托管包、跨版本迁移、原生输入、最新setup干净安装仍待对应任务。

最终实际门：Core全量349条PASS、exit0、Atria选跑1 SKIP（显式关闭真实云开关，未发模型）；Pi全量21 PASS/exit0。日志tmp/m-continue43-Fufucode.Core.Tests.log与Pi.Tests.log。未靠历史计数；新官方Host9检查已在Core全量执行。Hosting回归与主线SHA在完成后补录。

Hosting本轮实际exit0、runner报告**267 checks**，日志tmp/m-continue43-Fufucode.Hosting.Tests.log；268个PASS行中一行为总计，不写成268条断言。最终日志均保留，未调用模型。
