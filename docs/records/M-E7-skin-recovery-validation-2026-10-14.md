# M E7换肤恢复与失败路径补验（第60轮）

起点main `6d59ee416b49c5751c09ef231f68215fcc2b9eb1`，产品沿用 `7deef34974fb1af0b1c6723d8587f0aa13a3b1b3`。M持main-writer独占租约PID18240，仅扩展现 `SelfTest/SkinIntegrationSelfTest.cs` 与本轮文档。没有修改产品/后端/资源，未夹带用户demo JSON及未跟踪内容。

## 新增14项实际共享路径检查

- 真实设置选择器选择Metro时，临时ui.json由独占FileStream锁住。保存失败后Current/窗口仍M3，选择器复原并暴露错误；解除锁后核原文件字节不变。不是手工抛异常的假保存失败。
- 紧接保存失败执行Undo/Redo，按上一次实际成功写入恢复Metro/M3；控件资源与持久值一起核对，不把失败选择写入撤销栈。
- 打开生产dock-tab-files的真实ContextMenu，M3圆角16；同页重新挂到浮动Window再选Metro，真实菜单圆角0。不只检查字典含某个值。
- 主/浮窗口回Fluent后同时恢复自选#087E66强调色，浮窗皮肤字典计数回0。
- reading预设保M3，重新new配置Store读回实际UI持久值；关闭原MainWindow后另开同临时根的新MainWindow，持久M3实际显示，按当前继承主题解析正确窗口底色，底部状态条仍隐藏。

以上与原18项合并为32项，按实际日志计数全部PASS、exit0（`tmp/m-cycle60-final-paths.log`）。没有发现需改产品的回归，也没有为了制造红绿而人为破坏产品或放宽原断言。初次29项之后补实际关窗/新窗口3项，最终32项只运行一次。先前进度中的13/31为人工计数错误，最终以日志14/32为准。

## 验证与证据

| 项 | 结果 |
| --- | --- |
| App Release构建 | 0警告0错误、exit0；`tmp/m-cycle60-final-build.log` |
| `--selftest --skins-integration-only` | 32 PASS/0 FAIL、exit0；`tmp/m-cycle60-final-paths.log` |
| 默认App全量自测（注册的新增检查） | 1420 PASS/0 FAIL、exit0；`tmp/m-cycle60-app-full.log` |

数据与真实无头主窗口帧：`C:/Users/mio/AppData/Local/Temp/fufucode-skins-integration-5493704bd7ec42b988977a22526ac7b9/`。这些是真实Avalonia控件、文件锁和资源/生命周期证据；新MainWindow是同进程重开，不是系统进程重启/崩溃恢复证据。原生系统输入/IME/DPI不在此工装覆盖内。

## 并行监督与后续

E7-AERO原turn active/inProgress、源HEADc4b630b，未交付不读半成品。当前未知SandboxPID15712/出生不变，不接管、不把旧R1冻结候选当新皮肤安装证据。

第二槽E7-NATIVE已按hourly-E7-native任务书创建：真实thread `01a13993-76d2-7650-a774-ad1df6aa12d7`、树 `C:/Users/mio/.codex/worktrees/0ed8/Fufucode`、起点cd1b470，固定产品7deef34。保存会话turn_context核实有效 `devin/swe-2`/medium/cwd，没调用贵验证lead；目录声称免费模型最高档不替代实际metadata。首turn completed但没有交付卡/记录提交，末条仅准备启动；M核对源HEAD/干净状态、无独立源码写入者，保留其kernel进程，通过原会话原模型成功续接完成实际验证。运行摘要的工具可用声称仍不是产品原生通过证据。只写本任务记录/临时证据，与Aero新资源路径不重叠，原生桌面操作独占串行。

本轮补验接受仅32项无头/文件恢复行为，不表示完整皮肤/G3或原生验收。后续M只在两包停写、卡/HEAD/范围核实后审证据或返工，原模型原会话；不新开第三槽。Core/Pi未改不重复全量/云试跑，发布冻结时六门仍需完成。
