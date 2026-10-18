# WorkBuddy 参考应用资产提取与界面映射清单

日期：2026-10-15  
任务：M1-ANDROID 独立移动端 App 对标研发  
状态：已完成官方 APK 视觉与资源提取，建立端到端界面映射

---

## 一、参考来源包核验（现场事实）

| 字段 | 真实值 | 备注 |
|---|---|---|
| **来源路径** | C:/Downloads/WorkBuddy.apk.1 | 用户本机指定下载位置 |
| **文件大小** | 281,930,310 字节（约 268.8 MB） | 包含完整 Flutter 与资源 |
| **SHA256** | $hash | 唯一可追溯文件哈希 |
| **包名** | com.tencent.workbuddy.app | 官方包名 |
| **版本** | 2.5.0 (versionCode: 650) | 2026 最新官方版本 |
| **最低 / 目标 SDK** | minSDK: 24 (Android 7.0) / targetSDK: 36 (Android 16) | 现代 Android 规范 |
| **主入口 Activity** | com.tencent.workbuddy.app.MainActivity | FlutterActivity 宿主 |
| **原生 ABI** | rm64-v8a | 本机 x86_64 模拟器（API 36）原生支持 NDK Translation |

---

## 二、提取资产统计与分布

已从 ssets/flutter_assets/ 中无损解包并归档至 ssets/reference/workbuddy/：

1. **矢量图标（SVG / PNG）**：共 **579 个**，位于 ssets/reference/workbuddy/icons/；
2. **专属字体**：AlimamaShuHeiTi-Bold.ttf（阿里妈妈数黑体，1,360,708 字节），位于 ssets/reference/workbuddy/fonts/；
3. **数据结构**：deep_thinking.json（深度思考模型映射，231 KB）与 skills.json（技能定义，1.3 KB），位于 ssets/reference/workbuddy/data/。

---

## 三、核心页面与视觉资产映射对照表

| Fufucode 移动端页面 | 对标 WorkBuddy 原生界面 | 核心视觉组件与对应提取资产 | 布局交互特征 |
|---|---|---|---|
| **1. 连接配对页** | 初始登录/连接网关页 | work_buddy_logo.svg<br>connector-connect.svg<br>lert-triangle.svg | - 顶部品牌 Logo + 状态说明；<br>- 4 位大号数字 PIN 码输入方格；<br>- 自动探测局域网 IP 与扫码连接。 |
| **2. 会话列表与抽屉** | 主界面会话抽屉（Drawer） | wb_menu.svg<br>wb_menu_open.svg<br>wide-new-chat.svg<br>rchive.svg | - 左侧滑抽屉展示项目与历史会话；<br>- 支持置顶、未读徽标与新建会话；<br>- 底部显示当前绑定的后端内核徽标。 |
| **3. 聊天时间线** | 核心对话界面（Chat Timeline） | Button-send.svg<br>Button-stop.svg<br>working.svg<br>ssistant-empty-illustration-light.svg | - 顶部会话标题与内核标识；<br>- 时间线流式气泡（用户文本、助手文本、思考折叠块）；<br>- 底部自适应输入框与大触摸面积发送/停止键（≥44px）。 |
| **4. 工具调用与审批卡片** | 智能体工作与审批卡（Tool Card） | sk-option-check.svg<br>workbuddy-task-stop.svg<br>deep_thinking.json | - 单次有效批准/拒绝浮动卡片；<br>- 工具名称、摘要与参数展开；<br>- 执行中带动态指示，执行后显示成功/失败状态。 |
| **5. 附件与扩展面板** | 附件与高级能力抽屉 | ttachment.svg<br>ttachment-image.svg<br>phone-file.svg<br>camera.svg | - 底部展开面板，展示本地照片、文件浏览与快捷引用；<br>- 技能与上下文卡片。 |
| **6. 设备与网络监控** | 设备管理与连接监控 | ell.svg<br>wb_share.svg<br>ddon-expire-history.svg | - 查看当前已配对桌面端 IP、端口与延迟；<br>- 一键断开与重新配对。 |

---

## 四、合规与工程落地原则

1. **独立包名与签名**：独立工程采用 com.fufucode.mobile，不带入腾讯凭据或私有接口；
2. **零 WebView 包装**：移动客户端作为原生/跨平台 App 独立运行，直接通过 HTTP/WebSocket 与 Fufucode 局域网服务通信；
3. **模拟器真实验收**：在本地 JiiiIntegrationOnly / wakeup_36 AVD 模拟器上完整验证安装、冷启动、配对与逐页交互。
