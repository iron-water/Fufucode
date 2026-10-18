# S1 皮肤样稿：交付记录

任务：S1 皮肤样稿（派工难度 L2 常规）｜执行软件：OMP 执行会话（本次 prompt 为队列目录 `s1-02`，因原 `s1-01` 的 Gemini 路由在实现前工具调用枚举不兼容而改用备选模型）。
日期：2026-10-10。

## 1. 开工身份卡（实际核对结果）

| 项 | 值 |
|---|---|
| 工作树（唯一允许写入） | `C:\Projects\Fucode\Fufucode-agent-s1` |
| 分支 | `agent/s1-unattended` |
| 起点 SHA | `7349405`（docs: require explicit group model IDs for new assignments） |
| 开工时已有改动 | 无（`git status --porcelain` 为空），未继承未提交用户改动 |
| 允许范围 | `docs/frontend-prototype/skins/**`、`docs/records/S1-skins.md` |
| 只读参考 | `C:/Projects/Fucode/Fufucode/docs/人类意见/前端风格.md`（未修改主仓库任何文件） |
| 未触碰 | 生产 `Styles/**`、`MainWindow*`、设置页、辅助栏、其他模块、全局进度、全局 failure-registry |

## 2. 交付物

| 路径 | 内容 |
|---|---|
| `docs/frontend-prototype/skins/index.html` | 五区外壳 + 独立设置目的地 + 状态画廊；三皮肤共用同一份标记 |
| `docs/frontend-prototype/skins/compare.html` | 三皮肤 × 亮暗同屏对照 + 令牌差异表 |
| `docs/frontend-prototype/skins/css/tokens.css` | 三皮肤 × 明暗令牌；变量名与生产 `App*` 语义键、`ControlCornerRadius` 对应 |
| `docs/frontend-prototype/skins/css/base.css` | 结构层（布局/控件骨架/画廊/状态工具类） |
| `docs/frontend-prototype/skins/css/skin-fluent.css`、`skin-metro.css`、`skin-material3.css` | 皮肤形态差异（圆角、描边、阴影、页签、输入区、焦点环、字体） |
| `docs/frontend-prototype/skins/js/app.js` | 皮肤/主题/缩放/视图/会话状态切换与画廊渲染 |
| `docs/frontend-prototype/skins/README.md` | 打开方式、目录、覆盖范围、截图索引、限制 |
| `docs/frontend-prototype/skins/TOKENS-MAPPING.md` | 生产现状事实、两套接入方案、令牌映射表、未验证项 |
| `docs/frontend-prototype/skins/shots/*.png` | 18 张 headless Chrome 实测渲染截图 |

覆盖：五区外壳（顶栏/左栏含图标轨+会话侧栏/中央含会话头+时间线+composer/右栏页签+diff/底栏终端/状态栏）、Windows 设置式独立设置页、亮暗、100%/125%/150% 缩放、默认/悬停/焦点/按下/选中/禁用/错误/空/加载中状态、空会话/加载失败/加载中会话变体。Fluent 基线沿用当前交付工作台色调意图（蓝 `#0078D4` / 暗色 `#60CDFF`、中性 Fluent 表面、4/8 圆角），未改架构。不做 Aero / Liquid Glass，未替用户选定生产风格。

## 3. 验证命令与实际结果

样稿渲染（工作树根目录，Chrome 154 headless；每次 URL 参数不同，产物见 `shots/`）：

```text
chrome --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 \
  --allow-file-access-from-files --window-size=1440,900 --virtual-time-budget=5000 \
  --screenshot=docs/frontend-prototype/skins/shots/<name>.png \
  "file:///C:/Projects/Fucode/Fufucode-agent-s1/docs/frontend-prototype/skins/index.html?skin=<skin>&theme=<theme>&view=<view>"
```

结果：18 张截图写出成功（命令输出 “NNNNN bytes written to file …”），逐张核对文件大小互不相同、内容与参数一致（例：`fluent-light-shell.png` 119136 B、`metro-light-shell.png` 99546 B、`material3-light-shell.png` 122938 B；`*-light-states.png` 为 1440×1560 长图）。已逐张目视检查：三皮肤在页签选中态、圆角、按钮形态、表面分层、输入区与焦点环上可区分；设置页与状态画廊内容完整。

布局诊断（Chrome DevTools 协议，`evaluate_script`）用于定位一次真实缺陷：`view=states` 时画廊落入隐式网格行、`.statusbar` 占满第 2 行。测得 `rows:"48px 1062px 26px"`、`gallery.top=1110`、`gallery.height=60`。修正为显式 `grid-row/grid-column` 后重新渲染，画廊恢复满高（截图为 136086 B 的内容长图）。

未运行：任何 Avalonia 构建、测试或原生窗口验证——本任务是独立视觉样稿，未改生产源码，按要求不越门。

## 4. 本任务内新问题登记（不写全局 failure-registry）

| # | 现象与证据 | 影响 | 建议处理方 |
|---|---|---|---|
| S1-1 | `WorkbenchAppearance.Surface`（白名单 `fluent/glass/flat/classic`）**没有任何渲染消费者**：全仓 grep 仅命中字段定义、校验与 `SelfTest/WorkbenchCustomizationSelfTest.cs:353-357` 的持久化自测 | “扁平/玻璃”等外观值目前不改变任何外观，皮肤接线缺少落位点 | S2/Codex：把 `Surface` 接到皮肤令牌选择，或明确废弃该字段 |
| S1-2 | `App*` 语义键在 `FufucodeTheme.axaml:15-120` 是 `StaticResource` 别名 | 运行时替换底层 Fluent 字典不会刷新已解析别名 → 方案 B（合并字典）可能只在启动时生效 | S2/Codex：皮肤字典直接给具体值，或改 `DynamicResource` |
| S1-3 | 形状令牌只有 `ControlCornerRadius=4`、`OverlayCornerRadius=8`（`FufucodeTheme.axaml:124-125`） | Metro 直角可覆盖，但 Material 3 的卡 16 / 浮层 28 / 按钮胶囊无对应键 | S2/Codex：按 `TOKENS-MAPPING.md` §2 增键 |
| S1-4 | 生产 `Styles/*.axaml` 中未找到 diff 加/删/边框令牌（只有原型 `workbench.css` 与内联样式有值） | 右侧“变更”面板在换肤后无法跟随皮肤状态色 | S2/Codex：补 diff 语义令牌 |
| S1-5 | Material 3 需要 Roboto，Windows 不自带；`docs/design/frontend-design-guideline.md` U02 明确“字体不打包第三方文件” | M3 皮肤字体只能回退系统字体，与规范观感有差异 | 用户视觉决策 + S2 |
| S1-6 | 本样稿缩放用 CSS `zoom` 近似 DPI 缩放 | 命中区/字号/IME 的 125%/150% 结论不可直接迁移到 Avalonia | V 任务：真实窗口原生复核 |

## 5. 未验证项与原因

- 生产接线全部未验证：本轮允许范围仅样稿目录，未改 `Styles`/`MainWindow`/设置页/辅助栏。
- Avalonia 12.1.3 自定义 `ThemeVariant`（方案 A 的前提）未实测；FluentTheme 模板是否硬编码圆角/阴影未核实（标 [INFERENCE]）。
- 悬浮标签、状态层、elevation 阴影在 Avalonia 的实现成本未评估。
- 截图仅覆盖 Chrome 154 渲染；未做 Firefox/Edge 差异比较（样稿不要求）。

## 6. 交付卡

```text
交付卡
任务ID与软件：S1 皮肤样稿｜OMP 执行会话（prompt 队列条目 s1-02）
工作树绝对路径、分支/HEAD、起点SHA：
  C:\Projects\Fucode\Fufucode-agent-s1，分支 agent/s1-unattended，起点 7349405
本任务提交SHA（多个提交注明范围）：
  7ce8767 样稿实现（index.html/compare.html/css/js/README/TOKENS-MAPPING）
  d11a6fb 渲染截图（shots/ 18 张）
  随后一次提交仅含 docs/records/S1-skins.md（本记录）
改动文件、继承用户改动的保护情况：
  新增 docs/frontend-prototype/skins/** 与 docs/records/S1-skins.md；未改生产 Styles/MainWindow/设置页/辅助栏/其他模块/全局进度/全局缺陷表；主仓库未写入；开工时无未提交用户改动，未夹带提交
实际验证命令、结果、证据位置：
  headless Chrome 渲染 18 图（命令与参数见 §3，产物 shots/*.png）；逐张目视核对皮肤差异与内容完整；DevTools 布局诊断定位并修复 states 视图网格落位缺陷后重渲染
未运行验证与原因：
  Avalonia 构建/测试/原生窗口验证——本任务为独立视觉样稿，未改生产源码，不越门；CSS zoom 缩放不等于原生 DPI
公共接线需求、接口/入口/依赖：
  S2 需新增 Styles/Skins/{metro,material3}.axaml 皮肤令牌包，扩展 App*/形状/字体/阴影键，并把 WorkbenchAppearance.Surface 接到皮肤选择；细节见 skins/TOKENS-MAPPING.md §2
状态：模块交付待接线（等用户选定风格后由 S2 接入）
原会话是否仍在写代码：否（本次任务完成，未开始下一任务）
```

附加提醒：`Surface` 白名单校验会拒绝 `metro`/`material3` 字符串（`WorkbenchAppearance.cs:85`），接线时需要同时扩展该白名单。
