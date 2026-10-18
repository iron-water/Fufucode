# M1-REMOTE 手机局域网远程控制 交付记录

状态：模块交付待接线（`module_ready`）｜难度：L3
任务书：`docs/tasks/hourly-M1-remote-control.md`
工作树：`C:/Projects/Fucode/worktrees/m1-remote`｜分支：`agent/m1-remote`
起点 HEAD：`c86254fecbe79c7bcd393c49141d51a37859385e`

## 1. 交付范围

按任务书允许路径新增，未触碰 `MainWindow*`、`SettingsPage*`、`WorkbenchSectionsView*` 等共享外壳；
公共 UI 入口接线留给主控 M。

| 路径 | 内容 |
| --- | --- |
| `src/Fufucode.Core/RemoteControl/**` | 轻量服务、认证、审批闸门、线协议、二维码（9 个文件） |
| `src/Fufucode.App/Features/RemoteControl/**` | 内嵌移动端页面、会话桥接、宿主（4 个文件） |
| `tests/Fufucode.App.RemoteControl.Tests/**` | 独立测试工程（3 个文件） |
| `docs/records/M1-remote-control.md` | 本记录 |
| `docs/records/evidence/M1-remote-control/*.png` | 真实 Chrome 渲染证据（10 张） |

## 2. 实现要点

### 2.1 轻量内置服务（Core）

- `RemoteControlServer`：只依赖 .NET 内置网络库。HTTP/1.1 与 WebSocket 握手为自实现最小实现
  （`RemoteHttp`，未使用 `HttpListener`，避免 Windows URL ACL 需要管理员预留 URL 的部署要求）。
- 生命周期：`Start(int port = 0)`（0 = 系统自动分配端口）、`Stop()`、`Status`
  （IP、端口、连接数、配对 URL、PIN、令牌有效期、启动时间），另有 `StartAsync` 供异步宿主。
- 事件推送：`/api/events` 升级为 WebSocket，连接建立即下发完整快照，随后按增量推送。
- 连接生命周期：每个连接带读侧泵（`ReceivePumpAsync`），手机端关闭页面/切网后连接被真实回收，
  不会长期占用连接数或 TCP 资源。

### 2.2 4 位 PIN 安全认证

- 服务每次 `Start` 生成随机 4 位数字 PIN（`RandomNumberGenerator`），并撤销全部既有令牌；
  停止后状态清空端口与配对信息，重启轮换 PIN。
- 配对成功后颁发 32 字节随机短期令牌（默认 12 小时可配置），过期即失效。
- PIN 比较使用 `CryptographicOperations.FixedTimeEquals`，长度不等直接判否。
- **锁定按客户端地址计数**（HTTP 为请求-响应即关闭的短连接，按连接计数会让计数永远无法累积）；
  同一客户端连续错误达到上限后锁定 5 分钟，锁定期间即使 PIN 正确也拒绝；其他客户端与既有令牌不受影响。
- 未授权连接对 `/api/snapshot`、`/api/message`、`/api/stop`、`/api/approval`、`/api/events`
  一律 401；**Web UI 外壳（页面/CSS/JS）免认证可达**——手机必须能先打开页面输入 PIN，
  且外壳不含任何会话数据。

### 2.3 移动端 Web UI（App）

- `RemoteControlWebAssets` 内嵌自包含单文件 HTML/CSS/JS，**无外部 CDN、无构建步骤**，局域网离线可用。
- 版式对标 WorkBuddy 移动端：底部三 Tab（会话/审批/设备）+ 顶部状态胶囊 + 紧凑卡片时间线
  + 底部审批抽屉（sheet）。
- 会话页：流式文本（带游标）、思考块（虚线弱化）、工具卡片（状态图标/颜色）、
  发送按钮与紧急停止按钮互斥切换。
- 深/浅色自适应（`prefers-color-scheme`）、`100dvh` + `safe-area-inset-*` 软键盘与刘海屏适配、
  320px 超窄屏无横向滚动、触摸命中区 ≥44px。
- 断线：横幅提示并可重连；重连后重新拉取快照补齐，断线期间为只读（发送/停止禁用）。

### 2.4 会话桥接与审批

- `RemoteSessionAdapter` 把真实 `IChatSession` 投影为远控模型，复用与桌面一致的消息重建语义
  （消息整体替换、块按 contentIndex 增量、工具状态变化）。
- `InMemoryToolApprovalGate`：单次有效；重复批准/拒绝同一审批返回「已失效」，不会二次放行；
  `WaitAsync` 供宿主阻塞等待手机端决定。
- `RemoteControlHost`：`Attach(会话, 标题)` / `StartAsync` / `Stop` / `Status` / `PairingQrSvg`，
  会话切换时替换适配器并撤销旧令牌。

### 2.5 配对二维码

`RemoteControlQr` 为自包含编码器（字节模式、纠错等级 L、版本 1-6），只生成模块矩阵与 SVG；
超出容量或空文本返回 `null`，由 UI 降级为纯文本地址，不产生不可扫的错误图案。

## 3. 实际验证

### 3.1 构建

| 命令 | 结果 |
| --- | --- |
| `dotnet build tests/Fufucode.App.RemoteControl.Tests/Fufucode.App.RemoteControl.Tests.csproj` | **0 警告、0 错误** |
| `dotnet build Fufucode.sln`（全解决方案回归） | **0 警告、0 错误** |
| `dotnet build src/Fufucode.Core` / `src/Fufucode.App` | 0 警告、0 错误 |

测试工程未注册进 `Fufucode.sln`，与既有 `Fufucode.App.Browser.Tests` 一致（同样不在 solution 内，按独立入口运行）。

### 3.2 定向测试（真实回环 HTTP/WebSocket）

```
dotnet run --project tests/Fufucode.App.RemoteControl.Tests
→ 全部通过：103 项断言
```

覆盖：回环启动/停止/状态（含端口 0 自动分配、占用端口真实失败、重启轮换 PIN 与旧令牌 401）、
PIN 认证（未授权拦截、错误 PIN、锁定与隔离、令牌过期）、会话数据与流式推送
（用户/助手消息、思考块、工具卡片、错误条目、重连补齐）、消息投递（含空白与不可发送拒绝）、
审批流转（批准/拒绝/重复批准拒绝/非法参数）、紧急停止（空闲拒绝、运行真实停止）、
页面契约（自包含/触摸区/自适应）与二维码契约。

### 3.3 真实浏览器验证（Chrome，非无头假设）

以 `RemoteControlHost` 起真实服务并注入脚本化会话，用系统 Chrome 实测：

| 视口 | 结果 |
| --- | --- |
| 390×844 手机视口 | 21/21 项通过（含错误 PIN、正确 PIN 进入会话、时间线渲染、审批拒绝、发送、深色模式、320 宽无横向滚动、无 JS 异常、除配对接口外无未授权失败） |
| Pixel 7 / iPhone 14 Pro / Galaxy S9+ 设备档案 | 触摸命中区 44×44、Tab 48 高、横向溢出 0、JS 异常 0 |
| 深色模式 | `body` 背景 `rgb(31,31,31)`，气泡/输入框/导航栏均为深色配色（数值实测，非目测） |

截图证据：`docs/records/evidence/M1-remote-control/`
（`m1-chat-390`、`m1-chat-dark-390`、`m1-chat-light-390`、`m1-chat-320`、`m1-approvals-390`、
`m1-approval-sheet-390`、`m1-device-390`、`m1-pixel-7-real`、`m1-iphone-14-pro-real`、`m1-galaxy-s9--real`）。

### 3.4 二维码可扫性（独立参考解码）

用 Python `qrcode` 生成对照并用 OpenCV `QRCodeDetector` 解码自研编码器输出：
版本 1–6、含双纠错块的版本 6、以及真实运行会话的配对 URL `http://127.0.0.1:58941/`
**全部解码回原文**；超出容量返回 `null`（安全降级）。

### 3.5 过程中修正的真实缺陷（红/绿对照）

| 缺陷 | 现象 | 修正 |
| --- | --- | --- |
| 派生类型序列化丢判别字段 | 直接序列化 `RemoteSnapshotUpdate` 时不含 `type`，对端无法还原 | `RemoteWire.SerializeUpdate` 以 `RemoteUpdate` 为声明类型序列化 |
| 枚举按数字上线 | JS 按字符串比较角色/状态，全部判错 | `RemoteWire.Options` 启用 `JsonStringEnumConverter` |
| 终态替换丢工具身份 | `message_end` 整体替换后工具卡片丢失工具名与调用 ID | `BlockBuilder.ApplyFinal` 从终态块回填 ID/名称 |
| 空闲连接不回收 | 客户端关闭后连接数长期不回落 | WebSocket 增加读侧泵 |
| 页面外壳被 401 拦截 | CSS/JS 加载失败，页面不可用 | 静态资源在认证前提供，`/api/**` 仍受保护 |
| 锁定计数按连接 | 短连接每次得到新身份，连续错误 PIN 永不锁定 | 按客户端地址计数 |

## 4. 未运行 / 未验证

- **未执行** 1480 断言的 App 全量自测与原生桌面测试（按 v3 战略与本任务书延后至里程碑收尾）。
- **未在真机手机**上验证：真实 IME、Android 返回键/手势、锁屏/切网、真实 WiFi 局域网跨设备访问
  （本次为同机回环 + 桌面 Chrome 移动视口/设备档案；真机验收属 D6/D9 门）。
- **未实现** 工具执行前的宿主自动拦截：`RemoteControlHost.RequestApproval` 已提供宿主登记入口，
  但"把审批结果真正接到 Pi 的扩展 UI 应答通道"属于公共接线，留给主控 M。
- 未验证多手机同时连接、长时间运行下的内存/令牌清理与生产安装包内的表现。

## 5. 公共接线需求（交主控 M）

1. 在共享外壳挂载远控面板：`RemoteControlHost` 的 `Status.Pin` / `PairingUrl` /
   `PairingQrSvg` 用于展示 IP、端口、4 位 PIN 与二维码；`StartAsync` / `Stop` 控制开关。
2. 会话绑定：`host.Attach(activeChatSession, 标题)`，切换会话时重新 `Attach`（旧令牌自动失效）。
3. 审批接线：宿主在执行工具前调用 `host.RequestApproval(...)`，并以
   `host.WaitForApprovalAsync(approvalId)` 等待手机端决定；若要与现有
   `IExtensionUiSession` 通道合并，须由 M 决定单一真源，避免两套审批并存。
4. 本模块未注册进 `Fufucode.sln`，与 `Fufucode.App.Browser.Tests` 一致。

## 6. 提交与停写

- 起点 HEAD：`c86254fecbe79c7bcd393c49141d51a37859385e`
- 本任务提交：`02cba6d801f217302b7d94ee3bb670bea78a99fa`
  （`feat(remote): deliver M1 mobile LAN remote control with PIN pairing, WorkBuddy-style web UI, and 103-assertion verification`）
- 本记录文档随后单独补录上一条 SHA，因此本记录的最终提交是任务提交之后的第二个提交
  （不在文档内自引用自身 SHA，避免伪造或循环引用）。
- `writerStopped: true`，提交后工作树保持干净，不再写入。
