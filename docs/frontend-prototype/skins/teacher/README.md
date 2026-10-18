# E7 教师样板（Fluent 基线 / Vista Aero）

**视觉样板，不是产品实现。** 不调用模型、不读真实项目数据、不访问网络、不写本地存储。参考书：[`docs/design/skin-teacher-reference.md`](../../../design/skin-teacher-reference.md)。

## 打开

双击 `index.html`（离线可用）。顶部深色条是样板控制，不属于产品：皮肤 / 明暗 / active·inactive / 实色降级·有模糊时 / 工作台·设置·状态画廊。也可以用 URL 参数复现截图：

```text
index.html?skin=fluent|aero&theme=light|dark&active=true|false&backdrop=solid|glass
          &view=shell|settings|states&statusbar=on|off&menu=open&dialog=open&float=right&chrome=off
```

Fluent 原语值直接引用 S1 的 `../css/tokens.css`（只读，没有复制）；Metro/Material 3 仍看 S1 的 [`../index.html`](../index.html) 与 [`../compare.html`](../compare.html)。

## 目录

| 路径 | 内容 |
|---|---|
| `index.html` | 五区工作台 + Windows 设置式目的地 + 状态画廊 + 区域菜单 + 对话框 + 浮动整栏窗口 |
| `css/teacher-tokens.css` | `--t-*` 组件令牌：Fluent 映射 S1；Aero 亮/暗新值 |
| `css/teacher.css` | 组件层（只读令牌）+ 800 宽窄窗折叠规则 |
| `js/teacher.js` | 切换与画廊渲染；自绘 16px 图标 |
| `avalonia/AeroCaption.axaml` | **可编译**：Aero 标题栏/窗口按钮画刷 + `AeroCaptionButtonTheme` |
| `avalonia/AeroCaption.Styles.axaml` | **可编译**：caption 容器、inactive、glass 作用域样式 |
| `avalonia/harness/` | 最小无头工装（不进 sln）；`harness-run.txt` 是最近一次实际输出 |
| `shots/` | 截图；`html-shots-log.txt` 记录每张的尺寸和参数 |

## 复现

```text
chrome --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --allow-file-access-from-files
       --window-size=<W>,<H> --virtual-time-budget=3000 --screenshot=shots/<name>.png "file:///<abs>/index.html?chrome=off&<参数>"
dotnet build avalonia/harness/AeroCaptionHarness.csproj -c Release --artifacts-path %TEMP%\e7-aero-harness
<artifacts>\bin\AeroCaptionHarness\release\AeroCaptionHarness.exe shots
```

## 限制

- HTML 截图是 Chrome 渲染，不是 Avalonia；只有 `avalonia-aero-caption-900x398.png` 是 Avalonia 真实帧。
- “有模糊时”只是示意透明度，HTML 不伪造 DWM 模糊；Avalonia 也不提供 Vista DWM 玻璃。
- 窄窗规则（右栏收起、底栏 150）是设计建议，生产当前行为以 `MainWindow` 为准。
- Aero Night 暗色是本样板自定，Vista 没有官方暗色，须用户确认。
