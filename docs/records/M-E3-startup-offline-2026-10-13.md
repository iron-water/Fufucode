# E3 启动离线入口：M 交付记录

任务 E3-M-HOST 的启动/无会话增量｜L3｜M 自做共享外壳。起点 main `b43212d10f44a66b93da7477407d4d7e0f995ca2`；实际源码提交 `08a1e6927f2bf1c89e3b4e98522931df4a9785df`（8 源/自测文件 + 本记录及缺陷登记，共 10 个允许文件）。原 E3/R1 worker 均已停止，无新交付，不重复派工或发送轮询消息。

## 实际行为

- 未选择项目、会话启动失败或信任未决定时，启动页提供“管理本地插件”。原本遮罩挡住整个外壳，无法进入已接好的模块。本增量不创建 mock 会话、不调用模型、不重试引擎。
- 同一 LocalPluginSectionView 在原扩展页和启动页之间转移。目录、视图模型、固定版本及选择保持同一实例；临时返回不释放，窗口最终关闭才释放。选择器以同一 Window 为 owner，不因模块移出扩展页而丢失 StorageProvider。
- “返回启动页”恢复原项目选择/失败说明与重试按钮状态。启动进行中隐藏离线入口；重复、启动中、成功恢复后、关闭后的旧点击不会创建第二模块或覆盖工作区。正式重试前先把模块归还原扩展页。
- 离线可预览、导入与查看副本；没有真实宿主仍不能应用/回退，未加载不能显示成功。恢复真实 Pi 后，同一离线导入版本可以通过既有明确安全应用接线实际加载，不自动应用。

## 验证与证据

| 实际命令 | 结果 | 本地日志 |
| --- | --- | --- |
| `dotnet build Fufucode.sln -c Release -v quiet` | 最终 0 警告、0 错误，exit 0 | `tmp/m-cycle47-solution-verified-build.log` |
| App DLL `--selftest --local-plugins-only` | 原共享 23 + 新启动 29 = 52 PASS、0 失败 | `tmp/m-cycle47-startup-final.log`；最终全量再次运行同断言 |
| App DLL `--selftest --local-plugins-real-only` | 12 PASS、0 失败，exit 0 | `tmp/m-cycle47-shell-real-verified.log` |
| App DLL `--selftest` | 1255 PASS、0 失败，exit 0 | `tmp/m-cycle47-app-full.log` |
| Core 测试 DLL，`FUFUCODE_ATRIA_REAL_MODEL_TEST=0` | 359 PASS、1 可选云测试 SKIP，exit 0 | `tmp/m-cycle47-core.log` |
| Pi 测试 DLL | 21 PASS、exit 0 | `tmp/m-cycle47-pi.log` |

DLL 均使用各工程 `bin/Release/net10.0/` 中的真实产物，没有与运行中的同 DLL 并发重建。全量新增启动检查涵盖两种入口、800×640 返回动作、同控件/数据根/编译列表、离线导入、重复进入、原状态恢复、关闭释放、迟到点击、启动互斥与新窗口持久目录恢复。

真实 12 项先注入一次启动失败，离线导入后明确重试，第二次打开实际 Pi；原会话、草稿、安全应用、更新、坏版本恢复、回退与返回原聊天继续运行。注入失败是测试接缝，不声称真实损坏运行环境已修复。Pi 使用临时数据、自有插件与显式 `proof/devin/swe-2` 离线 provider，没有云 prompt 或高价测试 lead。

M 实际查看最终 800×640 无头渲染 `C:/Users/mio/AppData/Local/Temp/fufucode-startup-plugins-476f420104a14a1c8a4ad077ef1c68d1/failure-offline.png`：返回与导入动作可见，长页面在同一滚动区。图片是无头像素证据，不是原生系统输入验证。

## 红证据与判断

- `tmp/m-cycle47-startup-red.log`：产品入口加入前，无项目/失败入口各一 FAIL。添加实际 UI 和路由后原检查全绿，没有用测试专用入口代替产品按钮。缺陷登记 E3-STARTUP-OFFLINE-1。
- 首个真实恢复工装 `tmp/m-cycle47-shell-real.log` 有 2 FAIL：只等待 Host 身份已知，尚未等待异步目录刷新/页面挂载。等待实际启动遮罩消失及刷新结束后，`shell-real-final.log` 仍保留 1 FAIL；诊断确认 opens=2、同 VM、版本=1，只有 sameTopLevel=False。
- `shell-real-diagnostic.log` 保留这条状态。按 TransitioningContentControl 的真实完成条件执行布局并等原控件挂到原窗口；最终 unchanged 的 sameTopLevel、opens==2、版本与能力断言全部通过。没有删除挂载断言、增加容许的启动次数或把逻辑父当作视觉宿主；也不把这组工装时序错误登记成产品恢复回归。
- 首次新增截图调用触发 1 条 CS0618 警告，日志 `solution-final-build.log` 保留；改用实际推荐的 PngBitmapEncoderOptions 重载，最终零警告，未抑制警告。

## 未验证与下一步

- 本增量 integrated 只覆盖启动离线入口及原恢复接线；E3 整项与生产六门仍 pending。系统原生指针、文件选择器、键盘、IME/DPI、长任务取消/关闭与生产迁移尚未验。
- 本轮没有重新打包；R1 仍冻结 cc44eee，不含后续插件成果。未知 WindowsSandboxServer PID15712（出生 2026-10-13 10:37:58 -07:00）仍存在，原候选安装门继续 defer，不终止未知实例、不重建或改源。
- 下一步 M 串行完成插件原生与取消边界；Sandbox 可证明空闲后才让原 R1 会话有限续验同一安装包。后续包含插件的新候选须另行冻结来源。
- main 可续期独占租约 PID3320，编辑/提交前核对存活与续期；原用户 demo 改动、未跟踪目录和所有 worker 工作树保持。无 push、历史改写、工作树清理或付费/登录操作。
