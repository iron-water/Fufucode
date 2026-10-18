# M：O2 修复轮复核

日期：2026-10-10。源HEAD282bd8c，工作树C:\Users\mio\.omp\wt\wt-20261010-072036-c2a5227，分支wt/20261010-072036，起点8cf3921。状态：继续修复，未收编/未接线；原会话停止写代码。

源实际`dotnet run --project tests/Fufucode.App.Storage.Tests`：201断言通过，exit0，tmp/m-o2-repair-tests.log。M直接编译真实清理源文件的临时诊断`dotnet run --project tmp/m-o2-review/Review.csproj`：原缓存内已有junction反例外部文件保留，旧过期日志更新后保留。CacheStorage已移到站点确认分类，静态核对通过；没有将文件符号链接SKIP标为通过。

但R-030仍open：目标缓存本身不是链接、预览子目录原来不是链接时，执行对快照文件只检查文件本身IsReparsePoint，不检查其父链；新ScanDirectory虽跳过链接，却继续遍历旧Snapshot.Files并通过链接访问外部同名文件。

可稳定复现，无需竞态：

1. 临时数据根webview/Default/Cache/nested/victim.txt为普通文件，构建默认缓存预览。
2. 移除夹具nested原文件和空目录，创建nested junction指向数据根外的临时protected-fixture。
3. protected-fixture/victim.txt为需保护的外部夹具，执行先前预览。
4. 输出`NESTED_SWAP linkCreated=True protectedExists=False deletedFiles=1`；新链接扫描已知跳过仍未保护旧文件执行路径。

完整主区日志tmp/m-o2-repaired-independent.log；唯一临时根C:\Users\mio\AppData\Local\Temp\m-o2-review-3b3e954228b84c60a36e7173cd87ea9e。仅夹具删除，无用户真实数据操作。R-031独立旧反例已通过，但未收编前状态保留，R-030不能关闭。未重复产品全量/原生/真实WebView2，避免无关测试。

## 下一步操作卡

原O2会话同worktree继续L3修复，不需新会话；免费/低成本模型优先，测试本地无模型即可。O1/O3仍待原生，D1继续G1，未解锁O2下游，不停止独立项。主区用户改动保持，不push、不改写历史、不清理worktree。

直接转发原O2：

```text
继续O2第三轮修复，同工作树，不开始下一任务。先读主仓库docs/records/M-O2-repair-review-2026-10-10.md和最新执行协议。
M已确认201测试通过、原已有junction与更新日志反例通过，但R-030仍存在：预览时Cache/nested/victim.txt普通文件，预览后nested替换为指向外部临时目录的junction，旧Snapshot.Files执行仍删除外部victim.txt。
修复CleanDirectoryTarget：对每个快照文件及目录实际操作前检查从可信边界到该对象的完整祖先链，不仅IsReparsePoint(full)。当前重新扫描已排除或不可安全到达的旧快照路径不得继续删除；目标自身、祖先和子层替换统一处理。无法读取属性不能当作安全可删除。保留跳过原因与外部文件，目录删除也不得对链接进行清空/越界。
补精确反例：先建立普通nested并预览，再替换该子目录为junction，外部同名哨兵内容/哈希不变、DeletedFiles不含它；嵌套两级父目录替换也覆盖。用真实临时junction，不能只有目标Cache本身替换测试。旧201断言保留，不放宽。
仅改Storage模块、独立测试、O2记录。保护继承文件，不改共享外壳/全局failure-registry、不整合main、不push、不清理工作树。不碰真实用户数据。模型试跑显式最低成本，无需收费调用。
小步提交，更新记录与忠实回归证据后主动重新给完整交付卡。原生/真实WebView2/文件符号链接未验证继续如实标注。
```
