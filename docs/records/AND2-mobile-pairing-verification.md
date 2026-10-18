# AND-2 手机端与宿主机真实端到端联调验证

日期：2026-10-15
任务：AND-2（L3 / verification + 缺陷修复）
主控 Thread：01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07
工作树：`C:/Projects/Fucode/worktrees/and1-mobile`｜分支：`agent/and1-mobile`
起点 HEAD：`9faa90ec4a6ae6b0ee671c9e894fded52afb9b67`

## 结论

**端到端联调通过**。Android 应用（`com.fufucode.mobile.debug`）在 `emulator-5554` 上
与宿主真实 `RemoteControlHost` 完成 4 位 PIN 配对、消息投递、工具审批全链路；
宿主侧 `WaitForApprovalAsync` 真实收到 `Approve`（`accepted=true`）。

过程中发现并修复了 **5 个真实缺陷**（其中 1 个使审批功能完全不可用），
并把界面按主控下发的官方 TDesign 规范（`docs/design/workbuddy-mobile-design-spec.md`）重构。

本任务为**模拟器级端到端验证**，不等于完整 M1 产品验收：未覆盖真机手机、真实 WiFi 跨设备、
锁屏/切网、旋转；宿主侧使用脚本化会话（`Fufucode.Mobile.LanHarness`），不是产品桌面 UI 面板。

## 一、验收环境（实测）

| 项 | 实测值 |
|---|---|
| 设备 | `emulator-5554`（AVD `wakeup_36`） |
| Android / ABI | SDK 36 / `x86_64` |
| 分辨率 / 密度 | 1080x2000 / 420dpi |
| 被测 APK | `android/Fufucode.Mobile/app/build/outputs/apk/debug/app-x86_64-debug.apk` |
| APK SHA256 | `761632BC8C5D4D362D6DAB906C0F66A5545ECF7DFE87FA6BADE8614BB39777BD` |
| APK 大小 | 17,474,448 字节 |
| 包名 | `com.fufucode.mobile.debug` |
| 宿主 | `tests/Fufucode.Mobile.LanHarness`（真实 `RemoteControlHost` + 真实 `IChatSession`） |
| 监听 | `0.0.0.0:54321`（宿主报告的 advertise 地址为 `198.18.0.1`，手机侧走 `10.0.2.2` 回环） |

> 端口占用前置检查是脚本的一部分：若 54321 已被占用会直接失败并报出占用者 PID，
> 避免把别的进程误当成验证对象（实测中确实靠它拦住了一次假通过）。

## 二、自动化脚本

`scripts/mobile/verify-mobile-pairing-live.ps1`

```powershell
pwsh -NoProfile -File scripts/mobile/verify-mobile-pairing-live.ps1
```

脚本要点（都是踩坑后收敛出来的真实做法）：

- **宿主侧是真源**：配对成功与否看宿主 `status` 的 `connections`，
  消息是否送达看 `sent` / `activityLog`，审批是否回传看 `WaitForApprovalAsync` 的返回值。
  不把「点过了」「进程退出了」当成功。
- **坐标从 uiautomator 层级动态读取**：`Compose` 的可编辑框暴露为 `android.widget.EditText`
  且带准确 `bounds`；按钮按 `text`/`content-desc` 找可点击祖先。
  固定像素坐标在实测中反复点错（把地址打进配对码、点按钮变成给配对码打字）。
- **焦点用输入法上报的 `inputTypeString` 反证**：地址框 = `URI`，配对码格 = `Number`。
  点按后轮询到预期类型才算切过去。
- **收键盘只能用 BACK（keyevent 4）**：本机实测 **ESC（keyevent 111）不收起键盘**，
  反而把字符插进当前输入框（日志里出现过 `g ft g ft` 这类垃圾进入地址框）。
- **清焦点再点按钮**：配对码格保持焦点时，下一次点击会先把焦点交回该格并重新弹起键盘。
- **等待应用首帧用像素判据**：冷启动会先显示 splash，此时 `dumpsys window` 已经是
  「应用有焦点」，点击却全部落空。脚本改为轮询截图里是否出现品牌蓝主按钮像素。

## 三、实测链路

| 步骤 | 做法 | 宿主侧实测结果 |
|---|---|---|
| 配对 | 手机输入 `10.0.2.2:54321` + 宿主动态 4 位 PIN，点「连接」 | `{"connections":1}` |
| 进入会话页 | 配对成功后自动进入 ChatScreen | 顶栏显示真实会话标题 `AND-1 移动端联调会话` / `gpt-6.1-sol` |
| 消息 | 手机输入 `Hello from WorkBuddy Android` 并发送 | `{"sent":["Hello from WorkBuddy Android"],"activityLog":["send:Hello from WorkBuddy Android"]}` |
| 流式渲染 | 宿主投递脚本化助手回复（思考 + 文本流 + 工具调用） | 手机实时渲染思考块/文本/工具卡 |
| 审批 | 宿主 `RequestApproval("tool_1","read_file","{}")` | 手机弹出审批卡；点「批准执行」后宿主 `awaitApproval` → `{"decision":"Approve","accepted":true}` |
| 收尾 | 手机点「批准执行」后 | `approvalsPending=0`，界面提示「已批准一次执行：edit」 |

一次完整通过的结果文件：`docs/records/evidence/and2-live-pairing/and2-live-results.json`

> 可复现性：从提交后的干净工作树再跑一次 `verify-mobile-pairing-live.ps1 -SkipBuild`，
> 得到新的 4 位 PIN（`9449`）与新的审批号（`apv-61be53ad4c5d`），
> 仍为 `decision=Approve / accepted=true / outcome=pass`。
> 证据目录保留首次完整通过的截图（PIN `7527`、审批 `apv-581e58e83d3d`）与本文档一致。

```json
{
  "build": "ok",
  "host": { "address": "198.18.0.1", "port": 54321, "pin": "7527", "pairingUrl": "http://198.18.0.1:54321/" },
  "connections": 1,
  "pairing": "ok",
  "messageReceived": "Hello from WorkBuddy Android",
  "approval": { "approvalId": "apv-581e58e83d3d", "toolName": "edit" },
  "approvalAccepted": "Approve",
  "finalStatus": { "service": "Running", "connections": 1, "approvalsPending": 0, "stopCount": 0,
                   "sentMessages": ["Hello from WorkBuddy Android"] },
  "outcome": "pass"
}
```

## 四、发现并修复的真实缺陷（红/绿对照）

| # | 缺陷 | 现象（实测） | 修复 |
|---|---|---|---|
| D1 | **审批 UI 从未被渲染** | `ApprovalCard` 在 `ChatScreen.kt` 里定义了，但没有任何调用点。工具卡显示「待审批」，整页却没有批准/拒绝按钮，手机端**永远无法回传审批** | 在时间线 `LazyColumn` 顶部插入 `items(state.approvals)` 渲染审批卡；审批为空时不再误显示空态 |
| D2 | **配对表单超出视口** | 原布局「配对码卡在上、地址卡在下」，420dpi 下地址框落在首屏之外；键盘弹起后必须先滚动才能填地址 | 重建为「品牌区（收缩）+ 地址卡 + 配对码卡（含连接按钮）」，并去掉 `imePadding()`，整表单在键盘弹起时也不被遮挡 |
| D3 | **配对码格不可聚焦** | 透明 `BasicTextField` 用 0dp/1dp 尺寸时被测量成不可聚焦，`requestFocus()` 无效，输入法类型不切换；改成铺满后又与方格争夺命中区 | 最终方案：外层 `Box` 统一 `clickable → focusRequester.requestFocus()`，透明输入框铺满方格行（字号 1sp + 透明光标） |
| D4 | **连接按钮被键盘吞掉** | 配对码格保持焦点时点「连接」，点击先被该格抢回焦点并重新弹起键盘，按钮永远点不到 | `PrimaryActionButton` 的 `onClick` 里先 `focusManager.clearFocus(force = true)` 再 `keyboard?.hide()` |
| D5 | **Gradle 配置导致构建在本机必然失败** | `-Xmx3072m` + `parallel=true` 在本机可用内存下稳定触发 JVM 原生内存分配失败（`insufficient memory for the Java Runtime Environment`） | 降为 `-Xmx1536m -XX:MaxMetaspaceSize=512m` + `parallel=false` |

> D1 是**功能性硬伤**：本次任务书要求的「工具审批验证」在修复前不可能通过。
> 修复前的红证据是三张连续失败截图（`06-approval-card` 只有工具卡、无按钮；
> `WaitForApprovalAsync` 超时）；修复后绿证据为 `06a-approval-card-visible` +
> `07-approval-resolved` + 宿主 `accepted=true`。

## 五、官方设计规范落地（主控 2026-10-15 指示）

按 `docs/design/workbuddy-mobile-design-spec.md`（对 `WorkBuddy.apk.1` 与 `libapp.so` 的逆向结果）重构：

| 规范项 | 落地情况 |
|---|---|
| 品牌主色 `#0052D9` | `MobileColors.Accent`；主按钮、用户气泡、发送键、焦点环统一使用 |
| 危险色 `#FF3B30` | `MobileColors.Danger`；停止键、拒绝按钮描边使用 |
| 浅底/描边 `rgba(...)` | `DangerSoft=0x14FF3B30`、`DangerBorder=0x33FF3B30`、`Border=0x0F000000` |
| 圆角 8/12/16/24px + 999px 胶囊 | `ControlRadius/CardRadius/CardRadiusLarge/ComposerRadius`，发送键为全圆 |
| 触摸命中区 ≥44px | `TouchTarget=48dp`、`TouchTargetMin=44dp` |
| 数黑体标题 | `AlimamaFamily` = 参考包 `AlimamaShuHeiTi-Bold.ttf`，用于标题/按钮/PIN 数字 |
| PIN 独立方格 56×64 + 高亮焦点环 | `PinBoxWidth/PinBoxHeight`；聚焦格品牌蓝描边 + 品牌蓝数字 |
| 用户气泡品牌蓝、圆角 16 | `UserBubble=#0052D9`，`16/16/4/16` 与助手 `16/16/16/4` |
| 输入栏 24px 悬浮圆角 | `ComposerRadius=24.dp` |
| 发送→停止无缝切换 | 空闲为品牌蓝圆形发送键，运行中切为危险红停止键 |
| 审批卡拖拽把手 + 盾牌警示 | 卡片顶部 36×4dp 把手；`common-shield-warning.svg` |
| 官方 SVG 资产 | 沿用包内 579 个解包资产，未引入自绘替代图形 |

## 六、实际验证命令与结果

| 命令 | 结果 |
|---|---|
| `gradlew :app:assembleDebug` | **BUILD SUCCESSFUL** |
| `gradlew :app:testDebugUnitTest` | **BUILD SUCCESSFUL**；`MobileSessionControllerTest` 21/21、`RemoteWireTest` 8/8，0 failures / 0 errors |
| `pwsh -File scripts/mobile/verify-mobile-pairing-live.ps1` | **outcome: pass**（配对 + 消息 + 审批 + 截图全绿） |

## 七、证据（已落盘）

目录：`docs/records/evidence/and2-live-pairing/`

| 文件 | 内容 |
|---|---|
| `01-pair-screen.png` | 配对页首屏：官方 logo、地址卡、4 位方格、连接按钮 |
| `02a-pin-entered.png` | 配对码已填入 4 格（品牌蓝高亮） |
| `02-pair-filled.png` | 电脑地址 `10.0.2.2:54321` 与配对码均已填入 |
| `03-chat-entered.png` | 配对成功后进入 ChatScreen（真实会话标题 + 引擎标签） |
| `04-message-typed.png` | 聊天框内已键入待发送文本 |
| `05-timeline-streaming.png` | 时间线渲染：用户气泡 + 思考块 + 流式文本 + 工具卡 |
| `06-approval-card.png` | 审批到达时的界面（工具卡「待审批」） |
| `06a-approval-card-visible.png` | **审批卡本体**：把手 + 盾牌 + 参数 + 拒绝 / 批准执行 |
| `07-approval-resolved.png` | 批准后「已批准一次执行：edit」 |
| `and2-live-results.json` | 本次运行的机器可读结果（`outcome: pass`） |

## 八、未执行 / 未验证

- 未在**真机手机**上验证：真实 IME、锁屏/切网、真实 WiFi 跨设备、旋转；
  本次为 `x86_64` 模拟器 + `10.0.2.2` 回环。
- 未验证 arm64 变体在真机上的表现。
- 宿主侧为脚本化会话（`LanHarness`），**未接产品桌面 UI 面板**；
  审批与 Pi 扩展 UI 应答通道的单一真源仍属公共接线（见 `M1-remote-control.md` §5）。
- 未验证多手机同时连接、长时间运行的令牌/内存回收。
- 未执行 1480 断言的全量桌面自测与原生桌面测试（按 v3 战略延后）。

## 九、公共接线需求

1. 产品桌面端需提供「手机远控」面板并调用 `RemoteControlHost.StartAsync`，
   展示 `Status.Pin` / `PairingUrl` / `PairingQrSvg`。
2. 宿主在执行工具前调用 `host.RequestApproval(...)`，并用
   `host.WaitForApprovalAsync(approvalId)` 等待手机决定；与既有
   `IExtensionUiSession` 审批通道的单一真源由主控 M 决定。
3. `LanHarness` 仅为联调宿主，不应进入产品分发路径。

## 十、临时状态与恢复

- `emulator-5554` 保持运行，应用 `com.fufucode.mobile.debug` 处于前台（冷启动后可复现）。
- 本任务在 `/sdcard` 下的临时文件（`and2-*.png`、`and2-ui.xml`、`and2-ready.png`）均已清理。
- 宿主进程由脚本在收尾时 `quit` 并等待退出；脚本不残留监听端口。
- `gradle.properties` 的内存下调是**本机可用内存下的构建前置**，非临时 hack，已随修复提交。

## 十一、状态

状态：**提交待验收**（模块 + 端到端联调通过，真实缺陷已修，官方规范已落地）
`writerStopped: true`
