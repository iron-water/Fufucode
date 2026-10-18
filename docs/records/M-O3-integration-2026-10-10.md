# M：O3 修复复核与公共接线

日期：2026-10-10。状态：已整合待验证；不是整项验收。

## 来源与整合

源工作树 `C:\Users\mio\.omp\wt\wt-20261010-055801-c2a5227`，分支 `wt/20261010-055801`，起点83045c9，交付HEAD34fdd15；原会话已停止写代码。

六项提交按顺序收编：4f0d672→ed1b572、e691373→8d0a622、c7a5895→3faa02f、1924e4b→49d6721、f558336→83a5b70、34fdd15→10a971e。没有覆盖继承改动、push、改写历史或清理工作树。

M接线复用现有五区工作台：资料库可由工作台目录与命令面板打开；设置内置模块列表提供启停，并保存项目模块偏好。服务使用应用数据根下library独立库，测试使用SelfTestDataRoot。临时移栏保持VM；模块停用释放VM，重新启用创建新VM；窗口最终释放页面后再释放服务。沿用旧模块设置版本，缺失LibraryEnabled时默认启用。

聊天插入引用/收藏仍等待G1，不绕过门禁；独立页面导入、搜索、标签、预览、引用复制保持可用路径。

## 实际证据

- 源工作树：`dotnet build tests/Fufucode.App.Library.Tests -t:Rebuild --no-incremental -v quiet`，0警告0错误；随后`dotnet run --project tests/Fufucode.App.Library.Tests --no-build`，130通过、exit0。日志tmp/m-o3-repair-build.log、tmp/m-o3-repair-tests.log。
- M旧反例重跑：`dotnet run --project tmp/m-o3-review/Review.csproj`，31字符再导入31字符、matches=True；重挂后serviceCount=2、viewCount=2、attached=True。主区tmp/m-o3-repair-independent.log。该临时诊断项目传递引用仍有MSB3277，与已修复的正式Library.Tests工程区别，不宣称诊断工程零警告。
- 主区独立输出：`dotnet build src/Fufucode.App -v quiet -o tmp/m-o3-integrated-app`，0警告0错误；日志tmp/m-o3-wiring-final-build.log。
- 主区解决方案：`dotnet build Fufucode.sln --no-restore -v quiet`，0警告0错误；日志tmp/m-o3-integrated-solution.log。
- 主区`dotnet tmp/m-o3-integrated-app/Fufucode.App.dll --selftest`实际完成：0项失败、exit0；最终日志tmp/m-o3-wiring-final.log。包含实际主窗口停用释放、新启用新VM、偏好往返三项断言。

期间M误在旧自测未退出时重建同一默认输出目录，发生MSB3026/MSB3021文件锁失败。未关闭用户进程，改用独立输出目录重新实际验证。失败日志tmp/m-o3-wiring-build.log保留，不算产品缺陷或通过证据。未增加警告压制、未放宽断言。

## 未验证与下一步操作卡

原生指针、键盘、真实IME、DPI、主题切换尚未验证。本会话工具目录无node_repl；computer-use技能要求通过该工具导入@oai/sky，不伪造原生输入证据。独立Library.Tests仍单独运行，不宣称其130项已自动纳入App默认全量。无生产数据迁移、干净安装或新吞吐基准证据。

R-023/024/025模块缺陷已独立复核修复；O3整体保持已整合待验证，O7不提前解锁。原O3会话等待，不需新会话；O1原会话仍修复，D1原会话仍补G1。活跃写入状态未知，不自动创建/发送任务。M接线L3（生命周期、持久化与共享外壳）；原生补验L3，需要computer-use，模型试跑用最低成本，不能因验证改用昂贵模型。

原O3可直接发送：

```text
O3修复已由M复核并收编，公共工作台/设置启停已接。读取主仓库docs/records/M-O3-integration-2026-10-10.md了解状态。
本任务进入等待原生验收阶段，保留现有工作树与继承改动。不要自行整合main、同步或重置工作树，不开始O7、不push、不清理、不将任务标为整项完成。只有收到M明确的新缺陷修复指令时继续O3代码修改；必要协调主动输出协调卡。
```
