# M：R1-REVALIDATE 新候选审核（第45轮）

任务/run：R1-REVALIDATE / R1-REVALIDATE-cc44eee。原线程 `01a133b9-7e8d-7b61-a49a-cd02aed15303`，原 qf 模型/L3/cb9f 工作树。实际交付 HEAD `40973286d9569bc382404d9bd02a79b4020d1598`；冻结产品 `cc44eeef02c84c82847e104d31186a08f3d3c6a1`，构建内嵌导入提交 `c715b3c4d05b545c602c73a675e745a4b45228f0`。

决定：**defer / not_integrated**，仅新版 setup 的干净安装门有外部阻塞，不打回要求 worker 修改公共产品。接受原日志证明的候选部分能力与整项审核/收编分别处理；本记录不称六门齐全或当前 main 最新功能都进包。

## M 实际核验

- 先看到 thread idle/completed、终态 turn 和 writerStopped，再读取唯一交付卡；实际树 HEAD 一致，无该树 dotnet/node/pwsh 写入进程；仅继承 LICENSE 幻影 M，numstat 空。没有读运行半成品或中途审核。
- 最终消息含单个 JSON 代码围栏，M人工抽取唯一卡并保原始回复于 R1-after-focus-delivery JSON，不能冒称严格裸 JSON 运输自动校验成功。
- 检查实际三个提交及允许 delta：8条授权文件逐字导入、记录和报告；`git diff --quiet <head> <cc44eee> -- src tests extensions runtime Fufucode.sln` 实际 exit0。未导入后续插件代码，没有同步未来 main。
- 实际重算 ZIP/setup/清单三文件 size/SHA256，与交付卡全部一致；ZIP 15542 文件逐项流式 SHA、嵌入清单字节相同、无重名、无缺失/多余；真正的 self-contained CLI 入口在包中。
- 逐份读取原始包内全量 run0/run1/run2：每份1203 ok，无FAIL，终态零失败；源/包关联哨兵记录同一 ZIP SHA和各自独立PID/根。M本轮未再执行这三次，未借M上一轮源自测当包内证据。
- 核对完整 clean-start 原始JSONL：overall PASS、托管Pi get_state、graceful exit0、residual0；读真实CLI host A–J及另一次身份行取证，错误HostID不停止、第二宿主拒绝、同ID停止/endpoint清除均有原日志。
- worker 首次 host 身份行取证的 stdout 流被自己的 ReadToEnd 占用，原红日志已保留；独立聚焦探针成功取得身份行。此为其工装缺陷，不给产品登记虚构回归。
- M实际程序检查14项零失败，命令 `python tmp/m-cycle45-r1-review.py`，证据 `tmp/m-cycle45-r1-artifact-review.log`。它校验产物及原证据，**不是 M 再次跑原生安装/模型**。

## 实际候选身份

| 产物（原 cb9f/out/r1-after-focus） | 字节 | SHA256 |
| --- | ---: | --- |
| Fufucode.zip | 191740584 | DE11A1B6FC431CAC61597DA1364867FAC54DA57F126D1A46C9A88E9A00790CF6 |
| Fufucode-1.4.4-candidate-setup.exe | 141149252 | 94B65949A883FA081BE8B753F070AF3E1C82C9E1470444120BB7ABFC8D131F49 |
| build/integrity.sha256.json | 2963859 | CD6D34FC168B3950FAC4976B530CA6275B89430B3F773DE996ECDDBB3131A1D8 |

源构建0警告0错误、Core349/Pi21/Hosting267/Control511、包内三轮1203零失败/启动器17项均属本次 worker 实跑证据；M核验上述必要产物及原日志，不把它们冒称M本轮全部独立重跑。

## 阻塞与后续

M只读CIM核对 WindowsSandboxServer PID15712、父22708、出生10:37:58仍匹配卡内未知实例。该进程不是当前worker所起，父进程已消失；`wsb list` 空不能证明槽位空。没有停未知实例、没有宿主装卸、没有抢占原生验证槽。新 setup 安装/启动/卸载/数据保留没有运行，旧版本12/12不覆盖新版。

下一步在正常监督周期检查一次槽位，释放后原会话/原模型仅续做这一个固定安装门，不重建候选、不同步未来main，不让模型反复等待/轮询。未知实例未释放时继续独立产品开发。OMP托管、原生IME/DPI、升级迁移/WebView2真实页及H1/手机/全自动仍未验。

本轮候选与后续 main 的 E3 离线/真实初始化证明增量是不同冻结源。保原树和所有旧/新工件，未拷覆盖任何主区候选、未push/改历史/清理。结构化决定绑定真实HEAD于 `R1-after-focus-review-deferred-2026-10-13.json`；此次 mainSHA=null，不能凭日志门通过假填集成SHA。
