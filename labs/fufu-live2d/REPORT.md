# FUFU-02 Live2D 渲染技术验证 — 交接卡

- **任务ID**：FUFU-02　**状态**：已完成（两路径均实跑，Path B 全项验证通过，Path A 除逐像素透明外全项验证通过）
- **工作树路径**：`labs/fufu-live2d/`（本目录以下全部内容为本任务改动；仓库其他路径未触碰）
- **分支**：`feature/fufu-02-live2d-probe`（基于 main `eef79d1`）
- **真实 HEAD / 提交SHA**：见 git log（本文件随分支提交后 HEAD 为最新提交；此前 base 为 `eef79d1485288590850e627cbc67abab81d268a2`）
- **stoppedWriting**：无遗留写入中的文件。

## 1. 验证对象与构建

| 项 | 值 |
|---|---|
| 机型 | Windows Server 2022 虚机（无 GPU 透传，D3D 走 WARP/软渲） |
| 运行时 | .NET SDK 10.0.402（`%LOCALAPPDATA%\dotnet`）+ Avalonia 11.3.x + SkiaSharp 3.x + Microsoft.Web.WebView2 1.0.x |
| Live2D | Cubism SDK for Web / for Native **5-r.4**，官方示例模型 **Hiyori**（134 drawables，canvas 2976×4175，ppu=2976） |
| 构建 | `dotnet build src/FufuLive2D.slnx` → 0 错误 |

## 2. 模型来源与授权记录

| 资产 | 来源 | 许可 | 存放 |
|---|---|---|---|
| `live2dcubismcore.min.js` / `Live2DCubismCore.dll` | 官方 zip：`cubism.live2d.com/sdk-web|sdk-native/bin/CubismSdkForWeb-5-r.4.zip` `CubismSdkForNative-5-r.4.zip` | Live2D Cubism SDK License（评估与产品内再分发许可） | `%LOCALAPPDATA%\fufu-live2d\vendor\`，未进仓库 |
| Hiyori 模型 | 同 zip 内 `Samples/Resources/Hiyori/` | 同上（示例资产） | `%LOCALAPPDATA%\fufu-live2d\models\`，未进仓库 |
| WebView2 运行时 | `Microsoft.WebView2.FixedVersionRuntime.133.0.3065.92.x64.cab`（社区归档 westinyang/WebView2RuntimeArchive；Authenticode 签名验证 **Valid**） | Microsoft 软件许可 | `%LOCALAPPDATA%\fufu-live2d\wv2-runtime\` |

**授权风险**：SDK 二进制与官方模型不可入本公开仓库，`.gitignore` 已排除 `vendor/`、`models/`、`out/`、截图与 metrics；产品化时需以同样方式分发或让用户自备。本虚机缺 Evergreen WebView2 Runtime（引导安装器 0x80070057 失败），故 probe 用固定版本运行时 + `FUFU_WV2_BROWSER` 指向 `browserExecutableFolder`。

## 3. 实测数据表（60s 自动脚本：tap→idle→截图→hitTest→最小化→还原→重载）

| 指标 | Path A：WebView2+WebGL | Path B：Native(Skia) |
|---|---|---|
| 实测帧率 | **js_fps_avg ≈ 71–99**（rAF 内统计） | **55.8–57.0 fps**（UI 线程渲染） |
| 平均 CPU | **~43%**（主进程+7个wv2子进程合计） | **~3.3–4.7%** |
| 内存 | 主进程 WS ~104MB + WV2 子进程 WS ~430–530MB（peak 合计 ~635MB） | peak WS **286MB**，句柄 ~437 |
| 最小化 | CPU 不回落（WV2 内部持续合成/rAF 驱动） | CPU≈0%/秒级采样回落 |
| 重载 | reload→`loaded` 正常回传 | reload 正常（并发渲染锁保护，0.4s） |
| 命中测试 | (210,300)→`Body`，(30,30)→miss ✅ | 同左 ✅ |
| 动作切换 | TapBody→Idle 均生效 ✅ | 同左 ✅ |
| GPU | WebGL 经 WV2 GPU 进程（无独显机器上为 D3D11/WARP） | Skia 软渲到 CPU 位图，再经 Avalonia 合成 |

## 4. 透明 + 命中结论

- **Path B**：Avalonia `TransparencyLevelHint.Transparent` 无边框窗口内自定义 `DrawOperation` 直出 Skia 网格，**逐像素透明验证通过**（桌面截图可见模型外区域透出 Chrome/Google 页）。命中测试用模型坐标→控件坐标换算（unit→letterbox 映射），点击身体返回 `Body`，边缘空区返回 null。**推荐接入形态：实现 `ICharacterRenderer` 的 `NativeSkiaRenderer`，Load/SetState/HitTest/Size 可直接包裹 `CubismModel`+`Live2DSkiaView` 的逻辑。**
- **Path A**：`NativeControlHost` 子 HWND + `CoreWebView2Controller`，页面/JS 管线完整跑通（loaded/动作/hitTest/reload），**但 WV2 区域按不透明 surface 合成（模型外为黑底），逐像素透明未验证**。官方可行路径是 `CreateCoreWebView2CompositionControllerAsync` + DirectComposition `RootVisualTarget`；本 probe 的最小 C# 互操作在 `DCompositionCreateDevice→CreateTargetForHwnd` 处 0xc0000005 崩溃（RCW 与裸 vtable 两种写法均崩），未继续深挖。子 HWND 另导致：RenderTargetBitmap 截不到 WV2 内容（probe 内截图只见 Avalonia 覆盖层，视觉确认依赖桌面截图）；每像素鼠标穿透也无法在子 HWND 内做到（只有整窗 WS_EX_TRANSPARENT）。

## 5. 实际验证清单

- [x] 两路径：可复现程序（`ProbeNative`、`ProbeWebView2`，`--model/--record/--out` 参数化）
- [x] 模型加载（.model3.json→.moc3→贴图→drawables=134）+ 动作组切换（TapBody/Idle）+ 眨眼/呼吸参数包
- [x] Path B 全项：透明、命中、fps/cpu/mem、最小化资源行为、重载释放
- [x] Path A：加载/动作/命中/指标/最小化/重载；**透明背景未达成**（黑底），如实记录
- [x] 桌面截图证据：Path B 模型透明叠于桌面；Path A 模型于黑色不透明区块内渲染

## 6. 未执行/未验证项与原因

- **physics3.json 未移植**（两路径均缺物理摆动物理引擎；motion 本体播放正常）——工作量超出本次范围，列为已知缺陷。
- Path A 逐像素透明：如上，DComp 互操作崩溃未解；建议产品期引入成熟绑定（Terrafx/CsWinRT 生成代码）或改用 `WebView2CompositionControl` 承载。
- GPU 计数器未采（虚机无独显，性能计数器无意义）；句柄/GDI 只采主进程列。
- 多模型（Haru 等）未跑——Hiyori 已覆盖 moc3/贴图/motion/掩码全特性。

## 7. 已知问题与阻塞

- Evergreen WV2 引导安装器在本机失败（0x80070057）；Edge Stable 155 不能作 `browserExecutableFolder`（0x80070032）。已用固定版本运行时解决。
- WV2 子窗口原生类若用 `"static"` 会灰底闪烁 + 标题残留；已换自注册不擦背景类。
- WV2 最小化后 CPU 不回落——产品化需主动 `controller.IsVisible=false`/暂停 rAF。
- Skia 路径若对贴图 `Dispose` 与渲染并发会 0xc0000005——已加 `_renderLock`。

## 8. 最终技术建议

**推荐 Path B（Cubism Native Core + Skia）接入 FUFU**：资源占用低一个量级（3–5% CPU、~290MB vs 40%+ CPU、~600MB+），逐像素透明、命中、最小化回落全部实测通过，`ICharacterRenderer` 适配层约一个文件。Path A 仅在必须复用 Web 侧资产时考虑，且需先解决 DComp 透明合成与进程开销。

## 9. 公共接线需求 / 进程状态

- 复现需按 README 下载 vendor 资产到 `%LOCALAPPDATA%\fufu-live2d\`（含 wv2-runtime 固定版本）。
- 会话结束时无遗留运行中 probe 进程；metrics/截图在 `%LOCALAPPDATA%\fufu-live2d\out\`（不入库）。
