# fufu-live2d — Live2D 渲染技术验证（FUFU-02)

两条可复现的最小验证路径，对比 Live2D 在 Avalonia 透明桌宠窗口里的落地方案：

| Probe | 渲染路径 | 项目 |
|---|---|---|
| A | WebView2 子窗口 + Cubism SDK for Web (live2dcubismcore.min.js + WebGL) | `src/ProbeWebView2` |
| B | Cubism SDK for Native (Live2DCubismCore.dll P/Invoke) + Avalonia/Skia `DrawVertices` | `src/ProbeNative` |

公共底座 `src/Live2D.Core`：Cubism Core P/Invoke、motion3.json/pose3.json 求值（Linear/Bezier/Stepped/InverseStepped、加权混合、EasingSin 淡入淡出）、EyeBlink/Breath/Pose 效果、命中网格、指标采集。**未移植 physics3.json**（见 REPORT 已知问题）。

## 资产准备（许可约束，不入库）

Cubism SDK 二进制与官方示例模型只允许评估/产品内再分发，**禁止提交本仓库**。手工下载并放到 `%LOCALAPPDATA%\fufu-live2d\`（可用 `FUFU_LIVE2D_ROOT` 覆盖根目录）：

```
%LOCALAPPDATA%\fufu-live2d\
  vendor\web\live2dcubismcore.min.js   ← CubismSdkForWeb-5-r.4.zip 内 Core/bin/live2dcubismcore.min.js
  vendor\native\x86_64\Live2DCubismCore.dll   ← CubismSdkForNative-5-r.4.zip 内 Core/dll/windows/x86_64/
  vendor\native\include\Live2DCubismCore.h
  models\Hiyori\*.model3.json + .moc3 + textures + motions + pose3/physics3
```

下载地址（需同意 Live2D SDK 许可协议）：
- `https://cubism.live2d.com/sdk-web/bin/CubismSdkForWeb-5-r.4.zip`
- `https://cubism.live2d.com/sdk-native/bin/CubismSdkForNative-5-r.4.zip`

zip 内 `Samples/Resources/Hiyori`（或 Haru）整目录拷到 `models\Hiyori`。目录名即 `--model` 参数值。

## 运行

```powershell
cd labs/fufu-live2d/src
dotnet build FufuLive2D.slnx

# Probe B（原生/Skia）
dotnet ProbeNative/bin/Debug/net10.0/ProbeNative.dll --model Hiyori --record 25 --out %LOCALAPPDATA%\fufu-live2d\out\native

# Probe A（WebView2）
# 需要运行时，三选一：
#  1) 已装 Evergreen WebView2 Runtime —— 直接运行
#  2) 固定版本运行时：解包 Microsoft.WebView2.FixedVersionRuntime.*.cab 后设
#     set FUFU_WV2_BROWSER=<解包目录>   （本虚机用 133.0.3065.92 x64，见 REPORT §2）
#  3) 已装 Edge —— 自动回退 %ProgramFiles(x86)%\Microsoft\Edge\Application\<ver>
$env:FUFU_WV2_BROWSER="$env:LOCALAPPDATA\fufu-live2d\wv2-runtime\Microsoft.WebView2.FixedVersionRuntime.133.0.3065.92.x64"
dotnet ProbeWebView2/bin/Debug/net10.0/ProbeWebView2.dll --model Hiyori --record 25 --out %LOCALAPPDATA%\fufu-live2d\out\webview2
```

> 已知限制：子 HWND 内嵌的 WebView2 以不透明 surface 合成（模型外呈黑底）；逐像素透明须走
> `CoreWebView2CompositionController`+DirectComposition，详见 REPORT.md §4/§6。

参数：`--model`（模型目录名或 model3.json 路径）、`--record N`（自动脚本计时录制 N 秒并写指标）、`--out`（指标/截图输出目录）。不带 `--record` 时手动交互：按钮切动作/重载/穿透/网格，点击窗口命中测试。

自动脚本时间线（两 probe 一致）：t3 TapBody 动作 → t6 Idle 动作 → t8 截图 → t9 命中自检 → t10 最小化 → t14 恢复 → t16 截图 → t18 模型重载 → t22 截图 → 结束写 `*.metrics.json`（fps/cpu/工作集/句柄 + 事件日志）。

产物（`*.metrics.*`、截图）写在 `--out` 目录，均不入库（.gitignore 排除）。
