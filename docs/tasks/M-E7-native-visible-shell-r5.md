# E7-NATIVE R5：多窗口独立坐标、设置重置模态框、滚轮浏览与四皮肤原生验收

L3，原免费 devin/swe-2 / 原工作树 0ed8 / 原会话 01a13993-76d2-7650-a774-ad1df6aa12d7；不换模型、不新开会话、不修改产品代码。M 已完成 Aero 公共接线并生成最新 Release 专用构建。

## 冻结来源与构建产物

| 项目 | 值 |
|---|---|
| 基础 HEAD | `38936eb7d7602b993eef1a627f211ebf7095b4a3`（R4 提交） |
| 固定 sourceSHA | `b9363503c8157ae9c1a2c1409a775d9ba1d09daa`（包含 Aero 公共接线） |
| 构建路径 | `C:\Projects\Fucode\Fufucode\tmp\e7-native-r5-final\Fufucode.App.exe` |
| DLL SHA256 | `9D36EF6A848295E398552A29AB1D9C43CF1689EA72A22373AB5F6282AB002A80` |
| 数据目录 | 自身独立 `tmp/e7-native/data5` |
| 写入范围 | 仅本工作树 `docs/records/E7-native.md`、`tmp/e7-native/**`、`output/e7-native/**` |

## 核心验证要点（按顺序执行）

1. **浮窗多窗口独立坐标交互**：
   - 浮窗是独立的顶级窗口，不能在主窗对象 `WIN` 上用换算偏移点击！
   - 打开浮窗后，执行 `const wins = await S.list_windows();`。
   - 在返回列表中找到标题含“辅助栏”的浮窗条目（`const fw = wins.find(w => w.title && w.title.includes("辅助栏"));`）。
   - 获取浮窗专用句柄：`globalThis.FW = await S.get_window({ id: fw.id, app: fw.app });`。
   - 获取浮窗自身状态：`const sF = await S.get_window_state({ window: FW });`。
   - 在浮窗自身截图坐标系下点击 max 按钮：`await S.click({ window: FW, screenshot_id: sF.screenshots[0].id, x: ..., y: ... });`。
   - 再次获取 `get_window_state({ window: FW })`，点击 restore 按钮还原窗口大小。
   - 点击 X 按钮隐藏，验证 `ui.json` 写入 `hidden:true`。

2. **设置“恢复全部默认设置”模态对话框**：
   - 进入设置 -> 外观（Appearance）页面。
   - 找到占位文本为“选择范围”的下拉组合框（ComboBox）。
   - 选择第 2 项（索引 1，“恢复全部默认设置”）。
   - 旁边的按钮文本会变为“恢复全部”。
   - 点击“恢复全部”按钮，将弹出模态确认对话框“恢复全部默认设置”。
   - 验证对话框标题栏、确认与取消按钮、点击“取消”安全关闭并不改变任何设置。

3. **滚轮横向浏览页签**：
   - 在同一区域放入多个页签（如将文件、变更、预览、浏览器全部放入右栏，形成溢出）。
   - 在页签栏上方使用 `sky.scroll` 触发滚轮事件，验证横向平滑滚动与当前选中项可见。

4. **四皮肤（Fluent / Metro / Material 3 / Aero）切换与原生呈现**：
   - 在设置中依次切换 4 种皮肤：Fluent、Metro、Material 3、Aero。
   - 验证最新加入的 Aero 皮肤：实色渐变标题栏（AeroCaptionBackground）、三键连体水晶窗口按钮（AeroCaptionButtonTheme）在真实桌面下的渲染与 hover 反馈。

5. **800×640 窄窗与常规页面浏览**：
   - 验证窄窗模式下的布局与抽屉收起展开。
   - 检查资料库、通知、用量、插件、终端、存储页面的进出与返回。

6. **E7-NATIVE-4 崩溃判定**：
   - M 独立探针证明：使用 `data4/ui.json.crashed-backup` 在干净启动下不会触发崩溃（PID 34792 正常运行）。
   - 在 `data5` 启动前确保没有其他残留进程同时持有数据锁。若启动正常，记录事实，不归因于 `floating.hidden`。

## 回传与交付卡规范

- 本任务获用户直接授权向主控回传：
  > 授权来源：主agent (提问) 01a12596-9397-7132-b8c9-095b38fa726f
  > 用户原文：“那么你这个会话不要加定时任务。然后按照你这个修改后的做，为了保证能够唤醒，还是把原来的定时唤醒直接改为两小时。”
  > 主控线程 ID：`01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07`
- 任务完成后停写，主动向主控发送一次回传通知，内容为严格 JSON 格式的 `delivery.card`。
- 最终回复以 `{` 开头、`}` 结尾，禁止任何多余文字，包含 `commits` 数组、`writerStopped: true`、`verification` 分组（passed/failed/notRun 数组）与 `build` 信息。
