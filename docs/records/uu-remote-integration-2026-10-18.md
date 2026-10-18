# UU 远程接入

## 依据与边界

已通过 gh 读取官方 README 与 Android MainActivity 示例。
来源：https://github.com/netease-uuyc/uuremote-integration-guide
参考提交：`181dee4540a8e4161a65e1053fbcf6ad223d24f8`。

官方接口是跨 App 设备绑定及桌面远控 URL Scheme，要求 UU 4.42.0+，目标为 Windows/macOS。
没有开放 TCP/HTTP/WebSocket 隧道接口。Fufucode 的 HTTP + WebSocket 聊天链路仍独立运行，
不能把 UU 小窗成功当作聊天在线，不能从匿名设备ID推导本地地址。
本轮增加远程桌面备选入口，不开放本地服务到公网、不虚构通用转发能力。

## 实现

- 配对页与设备页新增“远程桌面 · UU”，无本地配对也能使用。
- 绑定、更换电脑、解除本机绑定、全屏、小窗及应用市场入口。
- Deeplink限定 UU 正式包，解析实际 Intent 检查可用性；不把可解析等同于版本合格。
- 独立保存设备ID和待完成请求，保存成功后才拉起 UU；保存失败不发送。
- Activity冷启动和onNewIntent均处理回调；debug/release采用独立scheme。
- 绑定nonce、小窗callback内request_id与待处理请求匹配；拒绝重复字段、
  畸形编码、空ID、控制字符、错host/path及未请求、重复、取消后或旧请求回调。
- 小窗只接受明确的成功或失败；未知错误按通用失败处理。
- device_not_found只清除匹配请求的旧绑定，其他失败保留绑定。
- 全屏不传callback、不等待结果，只显示已打开UU，不宣称远控成功。
- 等待期间禁止重复发起；“结束等待”只结束本地等待，不承诺终止UU。
  不用短超时误判尚待用户确认的UU弹窗。
- 不展示原始回调和设备ID。nonce提供请求关联与防重放，不是UU密码学身份认证；
  官方指南未提供签名回调协议。本地聊天鉴权与UU绑定互不替代。

## 验证

- `:app:testDebugUnitTest :app:assembleDebug`：BUILD SUCCESSFUL，31秒。
- 43项JUnit通过：既有23项会话状态、8项wire、新增12项UU协议。
- 新测试覆盖编码、互斥字段、重复回调、全屏无回调、小窗成功与失败、
  错误来源、失效绑定、取消后迟到回调和请求身份唯一性。
- 独立只读模拟器emulator-5590安装成功；原生点击配对页入口进入UU页；
  绑定操作正确显示“请安装或更新 UU 远程至 4.42.0 及以上”。
- 系统ACTION_VIEW冷启动递送无pending的绑定回调，实际保持未绑定配对页。
  首次UI dump返回null root，未采用旧XML；随后用新文件成功核对。
- 已关闭本轮emulator-5590，未操作其他设备或登录账号。
- 证据：[未安装提示截图](evidence/uu-remote-20261018/uu-not-installed.png)、
  [协议测试XML](evidence/uu-remote-20261018/TEST-com.fufucode.mobile.UuRemoteProtocolTest.xml)。
- 日志：`C:/tmp/work/2026-10-18-uu-integration/`。
- APK：`android/Fufucode.Mobile/app/build/outputs/apk/debug/app-universal-debug.apk`。
  SHA256：`E2C677B623C97091D0E698073806D3A89A85815C558AF284C70CCCB88AD0F27A`。

## 未验证与后续

真实UU登录授权、电脑绑定、跨网首帧、PiP权限与小窗、真实回调在进程回收后的完整往返均未验证。
单元测试不替代这些产品验收。需要用户在UU中登录并授权被控电脑后验证。
如果目标是聊天跨网传输，需另接实际的加密隧道或可信中继，本指南不提供该能力。
