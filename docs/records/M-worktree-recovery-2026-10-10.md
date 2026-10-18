# OMP 新工作树创建失败与恢复

日期：2026-10-10。用户错误：git commit报Windows1455（页面文件不足），随后git checkout无法由树87245a7创建索引；MCP microsoft-learn另报socket关闭，不将其与Git失败混为一因。

实测CommittedBytes=58,185,465,856，CommitLimit=59,446,099,968，余量约1.17GiB；物理可用3147MB。页面文件为C盘8GiB、D盘16GiB，AutomaticManagedPagefile=false。确认当前提交空间紧张；没有确定哪个进程造成增长，没有关闭用户应用或修改系统配置。

三个新工作树071954、072024、072036都登记于main基线8cf3921，但各2020条暂存删除，核心源码不存在。最新`C:\Users\mio\.omp\wt\wt-20261010-072036-c2a5227`现场只有.git，无未跟踪文件，确认是空检出而非用户删除工作。

恢复命令：

```text
git -c checkout.workers=1 -C C:\Users\mio\.omp\wt\wt-20261010-072036-c2a5227 restore --source=HEAD --staged --worktree -- .
```

实际exit0；恢复后git status --porcelain空输出、HEAD完整8cf3921da552782299f740ded78fd30b6956cd8b，MainWindow.Shortcuts.cs存在。仅修复已核实的空工作树，不修改主区用户文件、不重置历史，不删除另外两个失败目录。用户可在新终端进入此已恢复目录启动omp，不再重复/worktree。

没有重跑OMP自动快照、MCP重连或模型调用；没有声称1455系统根因已消除。恢复解除本次O2开工阻塞，内存提交压力仍在，后续系统页面文件调整另需明确授权。两个空失败工作树保留待核查清理，禁止直接递归删除。
