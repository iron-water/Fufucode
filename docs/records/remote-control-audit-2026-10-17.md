# 手机远程控制代码审核与修复

日期：2026-10-17。范围：远控插件 HTTP/WebSocket、认证、会话投影、审批、宿主、内嵌 Web 客户端、Android 传输/状态机/草稿，以及桌面启动接线。

## 已修复

| 问题 | 修改与证据 |
| --- | --- |
| WebSocket 只在握手验证令牌，过期连接仍能读会话 | 每次推送校验，空闲连接每秒检查并关闭；真实回环连接与可控时钟覆盖 |
| 未认证 TCP 连接也积压会话事件，且队列无限增长 | 仅握手完成后收事件，首帧固定快照；256 项有界队列，慢客户端断开后重连补快照；连接数上限128、读请求超时15秒 |
| 跨站配对与 DNS 重绑定 | 校验 Origin 和实际监听 Host；保留已安装浏览器扩展的既有配对/控制流程，拦截第三方网页；回环 HTTP 反例覆盖 |
| 启停竞争可能捕获已经置空的 CTS，旧监听接收进入新代次 | 捕获本次令牌，串行启停并核对监听实例；连续20次启停验证 |
| HTTP 半包正文仍可能交给命令处理器 | 截断、非法长度、重复头、Transfer-Encoding 拒绝；握手校验 GET/version/key/Connection；HEAD 不发正文 |
| 宿主启动失败遗留订阅，插件实例可能关掉别人的全局服务 | 失败时释放 server，重复初始化拒绝，关闭只处置本实例拥有的服务 |
| 释放后的适配器仍可发送，模型显示锁在首次值 | 解绑后拒绝发送/停止；快照读取当前模型 |
| 手机快照漏掉启用远控前的历史 | 宿主传入真实 transcript，按消息ID幂等补齐，不覆盖已有流式条目 |
| 手机审批未接真实会话 | 实现 SessionApprovalGate，confirm 请求按原始对象与 generation 回传真实会话；过期、重复及不支持的请求不伪造批准 |
| 内存审批先决定再等待会丢失原决定 | 保存有界已决结果，Wait 可以获取先到的决定，Resolve 仍只接受一次 |
| 设置入口未 Attach 当前会话、使用过时 UI 布尔值判断开关 | 设置页从当前 VM 取得会话/历史；按宿主真实 IsRunning 操作；主窗口关闭停止服务 |
| Android 配对晚返回可在断开后复活连接 | 配对也绑定连接代次；断开/新配对使旧响应失效 |
| Android 重连后发送/停止按钮可能永久在途 | 新代次清除旧在途标记；状态请求401后不再建立失效事件流 |
| Android 与 Web 发送前清空草稿 | 只在服务器明确 accepted=true 后清除原草稿；等待期间的新输入保留 |
| Android 事件通道满时静默丢增量 | 取消 WebSocket，流结束触发重连和快照补齐 |
| Web 重连异常会卡 connecting、无效令牌无限重连、停止失败报成功 | 修正状态归属、401重新配对、解析停止结果、防重复停止 |
| Android 地址解析接受凭据/片段/空主机；模型未知沿用旧值 | URI结构校验，快照中的 null 模型保持未知 |
| 绑定IPv6/指定网卡却公布另一地址 | 优先公布实际绑定地址 |
| 附件说明暗示电脑可读手机本地路径 | 界面与消息明确只有文件信息，未上传内容，电脑不能直接读取手机路径 |

## 验证

- `dotnet run --project tests/Fufucode.App.RemoteControl.Tests -p:TreatWarningsAsErrors=true`：130 项断言通过。含原112项和本轮18项。证据 `tmp/remote-audit-test.log`。
- `node tests/Fufucode.App.RemoteControl.Tests/web-client-audit.cjs`：执行生产内嵌 JS，验证拒绝发送保留草稿、迟到受理保留新输入、停止拒绝显示失败。DOM与网络为确定性替身，不是浏览器真机。
- Android：用本机 Android Studio JBR 执行 `gradlew.bat :app:testDebugUnitTest :app:assembleDebug --console=plain`。结果以本轮最终日志 `tmp/remote-android-test.log` 与 `app/build/test-results/testDebugUnitTest/TEST-*.xml` 为准；31项JUnit用例通过，debug APK构建成功。
- 桌面 App 最终强构建结果见 `tmp/remote-app-build.log`：`dotnet build src/Fufucode.App/Fufucode.App.csproj --no-restore /warnaserror -v quiet` 被其他在途文件 TimelineEntryViewModels.cs:245 的 CS1717 阻挡（自赋值）。此前普通增量构建返回0警告0错误，但不替代最终强构建；该文件非本轮修改，未以关闭告警掩盖。
- 远控测试已移除不使用的 App 工程引用，实际依赖插件/Core/Abstractions；桌面公共接线仍另跑 App 构建。

## 边界与未验证

- 未修改用户现用安装或默认监听配置；网络测试只使用127.0.0.1及临时端口；无模型调用，无付费。
- 未进行安卓真机/模拟器安装、跨Wi-Fi切网、桌面原生点击、真实模型工具审批端到端验证；不能据单元测试宣称这些通过。
- 实际审批只支持宿主真实暴露的 confirm 请求。原生内核未产生该类请求时不会凭空制造审批；input/select 等交互仍由桌面处理。
- 附件二进制上传、外网穿透/TLS、二维码实际扫码解码不是本轮新增能力；当前仍为局域网文本控制协议。
- Android 传输结果不明时保留草稿并允许用户核对后重试；协议尚无跨请求幂等键，不能承诺网络丢响应时“恰好执行一次”。
- 主区存在大量既有源码与已暂存改动，共享接线文件也有其他任务改动。本轮不将这些混入提交，不推送、不重置、不清理工作树；修复保留在当前工作区待主控按差异收编。
