# AND-3 模拟器安装与冷启动验证

日期：2026-10-15
任务：AND-3（L2 / verification）
主控 Thread：01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07
执行 Thread：01a13f29-5cce-7222-98ad-914b1597980d

## 结论

自制 Android App（`app-x86_64-debug.apk`）已在 AVD `emulator-5554` 上成功安装、冷启动并持续前台运行；配对首页正常渲染，无崩溃。截图证据已落盘。

本任务为**模拟器级安装/冷启动验证**，不等于完整 M1 产品验收（未覆盖真实手机 WiFi/锁屏/跨设备、与真实宿主的端到端配对与远控链路）。

## 环境事实（实测）

| 项 | 实测值 |
|---|---|
| adb 设备 | `emulator-5554  device product:sdk_gphone64_x86_64` |
| Android | 16（`ro.build.version.sdk=36`） |
| ABI | `x86_64` |
| 分辨率/密度 | 1080x2000 / 420dpi |
| boot 状态 | `sys.boot_completed=1` |
| 安装包 | `android/Fufucode.Mobile/app/build/outputs/apk/debug/app-x86_64-debug.apk` |
| APK 哈希(SHA256) | `9CD41E2B20D78B057285B1ABA61C973011C4AEBB5402C0D7AFA15D94EA9FF530` |
| APK 大小 | 17,467,243 字节 |
| 包名 | `com.fufucode.mobile.debug`（debug 后缀） |
| versionName / versionCode | 1.0.0 / 1 |

注：开工时 `adb devices` 已有运行中的 `emulator-5554`，未新启 AVD；`emulator-5586` 在验证过程中由**其他 agent** 上线，本任务所有命令均以 `-s emulator-5554` 限定，未触碰该设备。

## 执行步骤与实测结果

1. **设备检查**：`adb devices -l` → 已有 `emulator-5554 device`，`sys.boot_completed=1`。未启动新模拟器。
2. **安装**：`adb -s emulator-5554 install -r app-x86_64-debug.apk` → `Performing Streamed Install` / **`Success`**。
3. **启动**：`adb shell am start -n com.fufucode.mobile.debug/com.fufucode.mobile.MainActivity` → 启动成功。
4. **前台确认**：
   - `dumpsys window` → `mCurrentFocus=Window{... com.fufucode.mobile.debug/com.fufucode.mobile.MainActivity}`，`mFocusedApp` 一致。
   - `dumpsys activity activities` → `topResumedActivity` / `ResumedActivity` 均为该 MainActivity。
   - `pidof com.fufucode.mobile.debug` → 进程存活（例：4150）。
5. **首次截图**：`adb exec-out screencap` 在 PowerShell 下被字符串化（不可用）；改用 `adb shell screencap -p /sdcard/...png` + `adb pull` 得到有效 PNG。
6. **冷启动复核**：`am force-stop` 后 `am start -W` →
   ```
   Status: ok
   LaunchState: COLD
   TotalTime: 3134
   WaitTime: 3138
   Complete
   ```
7. **数值输入/软键盘**：点按配对码框 → 数字键盘弹出（`dumpsys input_method`：`mInputShown=true`，`mIsInputViewShown=true`）；`input text "1234"` 后四个码位正确填入，返回键收起键盘，布局无错位。
8. **局域网自动发现**：点按「扫描」→ 按钮切换为「停止」（扫描态），数秒后恢复「扫描」，未发现主机（**预期**：本机未启动 Fufucode 宿主服务）。
9. **崩溃扫描**：`logcat -b crash` 中无 `fufucode` 记录。
10. **清理**：删除本任务在 `/sdcard` 下的临时截图；设备上其余 `a1–z3.png` 属其他 agent 会话，未触碰。

## 证据（已落盘）

目录：`docs/records/evidence/and3-emulator/`

| 文件 | 内容 |
|---|---|
| `pair-screen.png` | 应用启动首页（配对码 + 电脑地址 + 自动发现），顶栏「未连接」 |
| `cold-start.png` | force-stop 后冷启动复现的同一首页 |
| `pair-input-focus.png` | 点按配对码框后数字软键盘弹出 |
| `pair-digits-entered.png` | 输入 1234 后四位码正确显示，键盘收起 |
| `discovery-scan.png` | 「扫描」→「停止」扫描态 |
| `discovery-result.png` | 扫描结束回到空闲态，未发现主机（预期） |

首页渲染要点（截图为证）：WorkBuddy 风格 logo、标题「连接电脑上的 Fufucode」、副标题「仅局域网直连，不经过任何云端服务。」、4 格配对码输入、「连接」主按钮、「电脑地址」输入、「自动发现 / 扫描」区块。

## 未执行 / 未验证

- 未安装/运行参考包 WorkBuddy（arm64-v8a），未做同尺寸逐页对照——本任务范围仅自制 APK。
- 未做真实宿主端到端配对与远控操作（需宿主「手机远控」面板在线并出码）。
- 未做：旋转、前后台切换、进程重启恢复、断线重连、真实手机 WiFi/锁屏/跨设备。
- 设备为 x86_64 AVD；参考包为 arm64，本机未验证 arm64 变体。

## 遗留状态

- `emulator-5554` 保持运行，应用 `com.fufucode.mobile.debug` 处于前台（PID 常驻）。
- 本任务未修改仓库源码；本记录为新增文档。
