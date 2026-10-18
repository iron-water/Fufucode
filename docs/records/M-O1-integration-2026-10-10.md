# M：O1 修复复核与快捷键接线

日期：2026-10-10。状态：已整合待验证；未完成原生M1验收。

## 来源与收编

原OMP工作树 `C:\Users\mio\.omp\wt\wt-20261010-055614-c2a5227`，分支wt/20261010-055614，起点83045c9，交付HEAD2f15a7a；原会话已停止写代码。

代码收编4731024→bedbb0c、beca239→9ff7b1d。O1记录直接采用2f15a7a最终文件；没有收编中间failure-registry变更，以免旧编号或旧main快照覆盖现有登记。原稿局部编号O1-F01/F02/F03与main R-026/027/028明确分开。源工作树及main继承用户改动未覆盖/暂存/提交，未push、重写历史或清理工作树。

## 修复复核

源工作树实际执行：

```text
dotnet build src/Fufucode.App -v quiet --no-restore -o tmp/m-o1-repaired
dotnet tmp/m-o1-repaired/Fufucode.App.dll --selftest --shortcuts-only
```

0警告0错误，143项通过、0失败、exit0；日志源tmp/m-o1-repaired-build.log、tmp/m-o1-repaired-tests.log。

主区旧反例诊断`dotnet run --project tmp/m-o1-review/Review.csproj`：错误bindings数组加载后Writable=False，保存被拒，原文本不变True；释放Ctrl后K记录为K而非Ctrl+K（后续验证拒绝裸键）；跨范围Status不再误报、Conflicts=0、行无冲突。主区tmp/m-o1-repaired-independent.log。第一次错误并行启动两项引用同源项目的构建，触发CS2012 obj文件锁；等待先前构建完成后顺序重跑成功，保留失败事实，不归因产品、不终止用户进程。

## M公共接线

- MainWindow在公共动作注册完成后投影真实目录，动作ID/标题/范围/可用性沿用现有契约。
- 设置新增键盘快捷键分类，承载真实ShortcutsPage；保持Windows设置式导航与辅助栏基线。
- keybindings.json使用应用同一数据根，自测使用隔离SelfTestDataRoot。修改即时用于当前窗口，保存供新窗口加载；损坏配置仍不自动覆盖。
- 移除Alt+Left、Ctrl+K/Shift+P、Ctrl+P、Ctrl+J的旧硬编码可配置路径；仅未配置动作用默认，禁用与改绑不回退旧手势。
- 当前范围优先于全局，终端焦点只检查终端范围，不抢全局键；同范围冲突阻止任意选一个并显示双方提示。
- 录制中窗口路由让路；IME组合读取当前TextPresenter.PreeditText。非配置Escape的原返回/浮层行为保留。原生焦点/IME仍未验。
- 动作目录快捷键显示随生效绑定更新，能力变化只刷新原因，不重新加载而丢失未保存改绑。

## 验证与未验证

主区`dotnet build src/Fufucode.App -v quiet -o tmp/m-o1-integrated-app`：0警告0错误，tmp/m-o1-wiring-build.log。`dotnet build Fufucode.sln --no-restore -v quiet`：0警告0错误，tmp/m-o1-wiring-solution.log。

最终App全量命令`dotnet tmp/m-o1-integrated-app/Fufucode.App.dll --selftest`，日志tmp/m-o1-wiring-full.log；782通过、0项失败、exit0，包括143模块断言与6项真实主窗口路由/设置断言。不能把程序化路由当作原生系统键盘证据。

原生键盘、真实IME、跨焦点、完整App重启、DPI未验证；本轮工具未提供computer-use所需node_repl。不宣称M1完整通过。无模型云调用、生产数据迁移、打包或干净安装验证。R-026/027/028模块修复经独立反例确认，整项仍待V。G1仍待D1原生补验。

## 下一步操作卡

main本次整合基线由最终提交给出。原O1和O3等待原生验收，不需新会话或同步工作树；D1继续原任务补验。M共享接线L3，范围路由/IME与动作契约需统一；测试无模型优先，模型试跑显式最低成本。

O2仅依赖G0、范围Storage，与现有O1/O3可独立，但活跃会话数量未知。用户要开下一任务时可在OMP准备新worktree从本次main基线启动；不要直接复用仍保留O1提交的工作树。未自动创建或发送消息。

原O1可直接发送：

```text
O1修复已由M复核并收编，真实动作目录、快捷键路由与设置入口已接。读取主仓库docs/records/M-O1-integration-2026-10-10.md了解状态。
本任务等待原生验收，保留工作树及继承改动。不同步/重置工作树，不自行整合main、不push、不清理，不自动开始下一任务；未收到明确缺陷修复指令不继续写代码。不要将O1标为整项已验收。
```

O2完整独立开工指令（由用户新建OMP工作树后发送，不在O1原工作树执行）：

```text
本独立OMP会话只负责O2存储管理及其修复，L2常规，免费/低成本模型优先。核对实际cwd、独立worktree、分支/HEAD、起点、继承改动；起点应为M最新main交付基线。读取AGENTS.md、docs/AI_DEVELOPMENT_PLAN.md、docs/tasks/execution-contract.md、session-handoff-protocol.md、launch-prompts.md的O2段和management-surfaces.md的M2。
依赖仅G0；允许App/Features/Storage、独立测试、docs/records/O2-storage.md，不修改共享外壳/配置/权威进度。实现可取消的实际占用计量、目录与保留规则、清理预览和真实结果；默认只清可重建缓存/过期日志，不搬迁数据根。
仅用临时数据验证取消、活跃文件清理失败与实际变化；会话、源码、配置、凭据、资料和worktree保持，不清用户真实数据。模型试跑显式最低成本，不新增收费调用。
保护继承改动，不push、不整合main、不清理工作树。每个可验证增量提交并记录；范围冲突输出协调卡并继续独立项。收尾主动生成完整交付卡写入任务记录并最终展示，公共接线与原生验收交M，不自动开始下个任务。
```
