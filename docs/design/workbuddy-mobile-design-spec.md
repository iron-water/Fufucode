# WorkBuddy 移动端官方视觉与代码设计规范（反编译逆向真源）

> 2026-10-17：本文件为旧参考记录，不再作为当前安卓视觉真源。用户指定v2.6.1提取包后，颜色、度量与资产以 `docs/records/mobile-workbuddy-v261-2026-10-17.md` 和其来源清单为准；下文“完整逆向”“官方蓝色”等表述不代表本轮已验证事实。

日期：2026-10-15  
数据来源：对 `C:/Downloads/WorkBuddy.apk.1`（腾讯 WorkBuddy 2.5.0 原生安装包，SHA256: `BFBDEE3F...`）与 `libapp.so` 核心资产的完整逆向解构。

---

## 一、核心设计令牌（Design Tokens）

从 WorkBuddy 原生 `lib/config/design_tokens.dart` 与 `libapp.so` 提取的真实设计常量：

### 1. 调色板（Palette）
- **品牌主色（Brand Primary）**：`#0052D9`（腾讯 TDesign 经典品牌蓝，主按钮、激活状态、重要高亮）
- **品牌次色（Secondary）**：`#185FA5` / `#378ADD` / `#1B5C99`
- **成功绿（Success）**：`#1D9E75` / `#28AE7B` / `#0D923F`
- **危险红（Error/Stop）**：
  - 亮色：`#FF3B30`，浅背景 `rgba(255, 59, 48, 0.08)`，边框 `rgba(255, 59, 48, 0.20)`
  - 暗色：`#FF453A`，深背景 `rgba(255, 69, 58, 0.10)`，边框 `rgba(255, 69, 58, 0.24)`
- **文字颜色（Typography Colors）**：
  - 亮色前景色：`#1C1C1E`（主字），`#8E8E93`（次级说明），`rgba(60, 60, 67, 0.60)`（弱化提示）
  - 暗色前景色：`#E5E5EA`（主字），`#98989D`（次级说明），`rgba(235, 235, 245, 0.60)`（弱化提示）
- **背景与表面（Surface & Backgrounds）**：
  - 亮色：页面底色 `#F2F2F7`，卡片表面 `#FFFFFF`，边框 `rgba(0, 0, 0, 0.06)`
  - 暗色：页面底色 `#18181B` / `#1C1C1E`，卡片表面 `#242426` / `#2C2C2E`，边框 `rgba(255, 255, 255, 0.08)`

### 2. 字体与间距（Typography & Metrics）
- **标题字体**：`AlimamaShuHeiTi`（阿里妈妈数黑体粗体，用于大标题、Logo、关键状态）
- **正文字体**：`-apple-system, BlinkMacSystemFont, "PingFang SC", "Segoe UI", Roboto, sans-serif`
- **等宽代码**：`ui-monospace, "SF Mono", Menlo, Consolas, monospace`
- **圆角（Corner Radius）**：
  - 小组件/输入框：`8px`
  - 气泡与普通卡片：`12px`
  - 大卡片/抽屉/浮层：`16px`
  - 胶囊/圆形按钮：全圆角（`999px` 或半径为高的一半）
- **最小触摸命中区**：严守 `44px × 44px`（真机防误触标准）
- **核心间距**：`AppSpacing.md = 12px`，大间距 `16px` / `20px`

---

## 二、六大核心界面高保真重构规范

### 1. 连接配对页（PairScreen）
- **顶部**：展示官方 `assets/icons/work_buddy_logo.svg` 大 Logo，下方用数黑体展示“Fufucode Mobile”，副标题“局域网安全配对连接”。
- **中间 4 位 PIN 输入框**：
  - 摒弃简陋的单条文本框，采用 **4 个独立的圆形或微圆角方形独立输入格**（每个尺寸 `56px × 64px`）；
  - 当前聚焦格带 `#0052D9` 外发光高亮边框；已输入的格子显示大号加粗字体；
  - 自动调起数字软键盘（`KeyboardType.NumberPassword`）。
- **局域网自动雷达扫描**：
  - 展示雷达波动动画圆环；
  - 发现宿主机后以卡片形式展示“桌面端已就绪 (192.168.x.x:54321)”，点击直接填入并连接。

### 2. 会话抽屉（SessionDrawer）
- **头部**：用户与设备头像卡片，展示当前连接的桌面端状态（绿点：已连接）；
- **新建按钮**：药丸胶囊形渐变按钮（`#0052D9` -> `#378ADD`），内嵌 `wide-new-chat.svg` 图标与“新建会话”文字；
- **项目与会话分组**：按时间与项目归类，每个会话卡片左侧带有当前会话绑定的后端内核彩色徽标（Pi 绿、OMP 橙、Codex 蓝、C# Native 紫），右上角未读红点。

### 3. 聊天时间线（ChatScreen）
- **顶部导航栏**：
  - 左侧：抽屉菜单按钮（`wb_menu.svg`）；
  - 中间：当前会话标题与后端内核微型胶囊徽章（如 `[● C# Native]`）；
  - 右侧：设备状态图标与更多操作（`wb_ellipsis.svg`）。
- **消息气泡**：
  - 用户气泡：右对齐，背景使用品牌蓝 `#0052D9`，文字纯白，圆角 `16px 16px 4px 16px`；
  - 助手气泡：左对齐，表面使用 `#FFFFFF`（暗色下 `#2C2C2E`），圆角 `16px 16px 16px 4px`，带轻微阴影；
  - 思考块（Thinking Block）：折叠药丸栏，内嵌 `working.svg` 旋转动效，点击展开半透明浅底代码/推理流。
- **底部输入栏（ChatInputBar）**：
  - 悬浮圆角卡片（`radius: 24px`），左侧加号扩展按钮（`input-add.svg`）；
  - 输入框自动多行伸缩；
  - 右侧：未输入时显示语音/微图标，输入文字后切换为大号圆形发送按钮（`Button-send.svg`，`#0052D9`）；
  - 会话运行中时无缝切换为红色方块停止按钮（`Button-stop.svg`，`#FF3B30`）。

### 4. 智能体工具卡与审批卡片（Tool Approval Sheet）
- **卡片式浮层**：
  - 顶部带灰色居中拖拽把手（Drag Handle）；
  - 标题显示待审批的工具名（如 `read_file`、`write_file`、`run_command`），右侧显示执行权限警告盾牌（`common-shield-warning.svg`）；
  - 参数区：等宽字体折叠展示 JSON 调用参数，超出部分支持滚动；
  - 底部双按钮：
    - 左侧浅红底色“拒绝”按钮（`#FF3B30` 描边）；
    - 右侧实心品牌蓝“批准执行”大按钮（`#0052D9`）。

---

## 三、实施纪律

- 必须直接引入 `assets/reference/workbuddy/icons/` 下解包出的原生 SVG 资产，杜绝粗糙的系统默认 Icon；
- 必须使用 `AlimamaShuHeiTi-Bold.ttf` 渲染重要标题和数字 PIN；
- 严格遵循上述调色板与圆角间距规范，彻底消除粗制滥造感。
