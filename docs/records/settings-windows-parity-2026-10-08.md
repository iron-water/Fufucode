# 设置页对齐 Windows 内置设置（像素级，2026-10-08）

用户要求：设置页与 Windows 内置设置**像素级相似**，交互也学进来。本文记录度量依据、逐项改动、以及反复截图比对的结论。

## 度量与结构的依据

对照用户提供的「系统 > 屏幕」截图（2000×1250，125% 缩放 ≈ 1600×1000 逻辑像素），提取到这些可测特征并落到实现里：

| 特征 | Windows 观察值 | 本实现 |
|---|---|---|
| 左栏宽度 | ≈320 逻辑像素 | 320 |
| 导航项 | 高 40，圆角 4，图标 16，项间留缝 | 一致 |
| 选中态 | 浅底 + 左侧 3×16 竖条 | 实测 3×16（像素扫描确认） |
| 页面标题 | 「系统 > 屏幕」，28px，父级次级色 | 28px，父级可点返回 |
| 搜索框 | 相对窗口水平居中 | 居中，宽 440 |
| 分组标题 | 14px 半粗，上方留 ≈24px 间距 | 一致 |
| 设置项 | **每项一张独立圆角卡**，卡间留 4px | 一致（原来是一组一张卡，已纠正） |
| 行高 | 带说明 64，仅标题 52 | 一致 |
| 行内布局 | 左小图标 + 标题 + 说明，右侧控件 | 一致 |
| 开关行 | 开关左侧带「开/关」状态字，点整行可切换 | 一致 |
| 下钻行 | 右侧 `>` 箭头，点整行进入 | 一致 |
| 二级页 | 面包屑「外观 › 前端功能排布」+ 返回箭头 | 一致 |
| 长列表分区 | 分组标题可折叠（如「多显示器」） | 入口位置分组可折叠，收起时显示项数 |

## 逐项改动

### 样式层（`Styles/WorkbenchStyles.axaml`）
重写设置区样式为 WinUI 度量：`settingsRow`（独立卡，圆角 4，边距 0,0,0,4，行高 64/52）、`settingsNav`（导航列表与选中态）、`navAccent`（强调竖条）、`settingsRowTitle` 14px / `settingsRowDesc` 12px / `settingsGroupHeader` 14px 半粗 / `settingsPageTitle` 28px / `breadcrumb` / `crumbLink`。

### 行构件（`Views/SettingsLayout.cs`）
- `Group` 改为纵向排列表，不再合成一张大卡（Windows 每项独立成卡）。
- 新增 `ToggleRow`：开关左侧带状态字，且**整行可点切换**（Windows 的行级热区）。
- 新增 `CollapsibleGroupHeader`：可折叠分组标题 + 项数 + 展开箭头。
- `Row`/`ChevronRow` 支持行首图标；`compact` 类用于仅标题行（52 高）。

### 页面壳（`Views/SettingsPage.axaml`）
- 页头改为 Windows 布局：左侧返回箭头 + 「设置」，搜索框**相对整个窗口居中**，右侧留摘要。
- 左侧导航每项加彩色图标（外观=青、功能=蓝、配置文件=紫、模型=橙、网络=绿、通知=黄、模块=粉、关于=灰），选中项浅底 + 强调竖条。
- 内容列左对齐（原来居中），与 Windows 一致。

### 二级页导航
`SettingsPageViewModel` 增加 `CurrentPageId` / `PageTitle` / `IsSubPage` / `ActivePageId` / `BackOrReturn` / `BackToolTip`。页头返回键**有二级页时先回分类首页，否则回工作区**——对齐 Windows 的返回语义。面包屑父级可点返回。

### 分类图标配色
`SettingsCategoryViewModel` 增加 `IconColor`；新增 `SettingsIconConverter`（键名→几何）与它的 `Brush` 转换器（#RRGGBB→画刷）。

## 开发中修掉的四个真实缺陷

1. **下拉框被图标挤位**：`ChoiceRow` 调用点已传入图标键，但签名还是 `params string[] values`，图标字串被当成第一个取值，导致「预设」「密度」显示空白、其余下拉错位。修签名后补上「一行一图标」断言（`every settings dropdown shows its current value`）。
2. **强调竖条不显示**：基础样式写在选中规则之后，同优先级下后者被覆盖；且模板里的 `IsVisible="False"` 压过样式。已调整顺序并由样式独占控制。
3. **响应式代码打错容器**：`UpdateResponsiveLayout` 仍按旧结构移动滚动区，改结构后抛 `ArgumentOutOfRangeException`；改为移动新的 `SettingsContentArea`。
4. **动作名残留英文**：文件/变更两个动作标题是 `Files` / `Changes`，在「入口位置」列表里露出来；已改中文。

另有一处**测试污染**：新增的整行切换用例改了状态栏却没还原，导致「默认关闭」断言失败；已在使用例末尾回写原值。

## 验证

| 项 | 结果 |
|---|---|
| 构建 | 0 错误 0 警告 |
| `--selftest --r05-only` | 67 项通过 0 失败（连跑三次稳定） |
| `--selftest --tabs-only` | 56 项通过 0 失败 |
| `--selftest --settings-destination-only` | 20 项通过 0 失败 |

新增断言：所有下拉都有取值；点开关行任意处可切换；分组标题可折叠与展开；进入二级页显示面包屑；返回回到分类首页。

截图（`tmp/r05-tests-c8ff10884ce84f6c909f0c1b35665eee/`）已人工核对：
- `settings-page.png` / `settings-page-dark.png` 明暗两套分类首页
- `settings-subpage.png` 二级页（区域显隐 / 页面位置 / 可折叠的入口位置）
- `settings-{models,network,notifications,modules,about,config}.png` 其余分类

另用电脑操作功能在实机 mock 窗口里打开设置页核对了一次：左栏彩色图标、选中竖条、卡片分行、下拉取值、强调色色板、二级入口箭头均与截图一致。

## 仍未做到

- 未做 Windows 设置页底的「相关支持」链接区。
- 搜索仍是分类级过滤，未细化到行级。
- 间距与字号按截图目测落值，未做逐像素叠加比对；如需精确到 1px，需要同分辨率同缩放的两图叠合，这超出当前证据条件。
