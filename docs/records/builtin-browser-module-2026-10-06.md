# 内建浏览器模块增量交付（2026-10-06）

## 本次完成

- 接收 R01-R04 研究结果，首先采用 R01 建议的内建模块注册表与浏览器启停，不引入第三方 DLL SDK。
- 核心页面不可停用，浏览器稳定 ID 在停用期间仍保留。设置页提供内建浏览器开关，命令面板、区域菜单和直接打开入口保持一致。
- 停用前保存地址草稿、导航目标及文件预览路径；移除视图并释放 WebView2 控制器。重新启用创建新实例，恢复原停靠位置与上述状态，中央输入草稿不受影响。
- 按项目保存模块偏好，重开后继续停用。未知版本/损坏数据给出诊断，不自动覆盖未知数据；用户显式切换后才允许重置。
- 停用选中页后仅在渲染时回退到可用页，不覆盖休眠页位置；终端启动判断使用实际可见页。

## 验证

- `dotnet build src/Fufucode.App/Fufucode.App.csproj --no-restore -v quiet -o tmp/module-build`：0 错误、0 警告。
- `node scripts/workbench-demo-verify.mjs tmp/module-build/Fufucode.App.dll`：Workbench failures 0；真实已安装 Pi 配合本地脚本模型完成回答、引用与终态检查，模块停用重开及恢复通过。此结果不代表远端生产模型验收。
- `dotnet tmp/module-build/Fufucode.App.dll --selftest --settings-destination-only`：17 项检查、0 失败。
- `dotnet tmp/module-build/Fufucode.App.dll --native-module-selftest`：真实 WebView2 8 项通过，覆盖 HTML 加载、三区移位、停用释放、恢复及立即停用防止异步复活。最终隔离数据根为 `tmp/native-modules-975e6381030044d5bdb9c0639338227b`。
- 原用户 Mock 窗口保留，未继续向其发送输入。验证窗口使用隔离数据并自行关闭。

## 未完成与边界

- 未注入 WebView2 runtime 缺失、初始化失败等故障，未验证高 DPI 和真实 IME。
- 模块真正停用后不保留 DOM 表单、完整后退/前进栈；普通跨区移动保留原控制器。
- 未实现第三方模块加载、Pi 插件热重载、R02 多窗口、R03 历史图、R04 皮肤选择。研究报告中的建议不作为实现或运行证据。
- 不修改第三方 `reference/`，不推送。

## 可运行入口与下一步

- 本轮最新产物：`C:\Projects\Fufucode\tmp\module-build\Fufucode.App.exe --mock`。默认输出仍被原用户窗口使用，因此本次采用独立输出目录，未声称默认 Demo 入口已更新。
- 下一增量：继续内容阅读与文件预览阶段，先明确格式覆盖、失败状态及定向验收，再实现；多窗口、历史图和皮肤按独立任务推进。
