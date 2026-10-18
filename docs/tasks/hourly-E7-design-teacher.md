# E7-DESIGN：有限设计示范与实现参考书

推荐主模型：`group/auto-claude-opus-5-5`。难度L4，角色为设计教师/关键样板实现，不是测试lead。用户已在本线程明确希望Opus5.5担当皮肤教师，现要求前提皮肤并派任务；这是有限设计授权，不是无限额度或其他任务换模型授权。只执行本包，不派昂贵子代理、不试跑模型、不自行采购或切路由。配额不足保已有交付并报告，M继续其他线。

## 起点与使命

创建时主区任务书提交为起点；工作在创建后的真实独立树。先读AGENTS、AI_DEVELOPMENT_PLAN、execution-contract、session-handoff-protocol、此任务，读 `docs/人类意见/前端风格.md`（用户未跟踪原文件若不在新树，读M只读摘录的 `docs/tasks/E7-style-brief.md`）、frontend-design-guideline、floating-workbench、S1-skins记录和已接受的skins目录。读当前真实MainWindow/Styles/WorkbenchAppearance，旧HTML布局是历史，不直接照搬旧状态栏/浮窗。

目标是“可看、可照抄且范围有限”的教师交付，不能只写抽象审美长文，不能把额度用于全仓重写。先用现成S1成果，再亲自完成最难的Vista/Aero视觉样板和一份按组件到Avalonia的迁移参考。默认Fluent不变，提供可选皮肤；不替用户宣布某套风格获最终视觉验收。Liquid Glass仍不做；Luna只定所需资源/形状策略，不生一大批素材。

## 唯一允许写入

- `docs/frontend-prototype/skins/teacher/**`：新教师样板/资源/截图/说明；旧S1原目录及shots只读。
- `docs/design/skin-teacher-reference.md`：教师参考书。
- `docs/records/E7-design-teacher.md`：身份、工作、测试和完整交付卡。

禁止写生产src/Styles/MainWindow/Settings/WorkbenchAppearance、共享计划/进度/决策/失败登记、旧S1工作树、其他模块或主仓库。reference第三方只读。所有内容可离线打开，无CDN/账号/模型调用、无真实用户数据。既有功能和业务交互语义不改。

## 必须直接完成的产物

1. **一个同结构的可交互教师HTML**，沿当前五区：caption/中央聊天composer/左、右、底可停靠页签/Windows设置式独立目的地；页签/整辅助栏可浮动的视觉样板，通知与用量作为功能页，主页面极扁状态栏默认关闭并有独立开关。只是视觉示例，显式标样板，不伪造业务操作/后端能力。
2. **有限Vista/Aero核心示范**：caption/window边框、页签、按钮/输入框、菜单/对话框、设置行和一处聊天/终端容器。做亮/暗和active/inactive、hover/pressed/focus/selected/disabled/error状态，有真实渐变/层次/高光规则；保内容面板可读，不给正文叠玻璃噪声。不承诺Avalonia能获得原生DWM Vista模糊；没有blur时给可工作的实色/渐变降级。标题栏视觉不能改变窗口按钮、hit-test、无障碍或拖动语义。
3. **一份Fluent基线对照**：同一示范页面与Aero切换，复用已完成Windows设置/左右辅助栏布局并校对现代码；已有Metro/M3保留链接，不重画所有皮肤。提供两套核心亮暗4帧，另外状态画廊及至少一个800×640窄窗，截图明确工具与尺寸。
4. **精炼参考书**：理念→视觉层级→色彩/形状/字体/间距/层次token→组件及状态→应用示范→Avalonia可行性/降级→实施验收。关键部分有具体值/代码/截图标注；不要写几百页同义文字。给色彩/字号/圆角/描边/阴影/渐变/对比度、资产来源与许可、系统字体fallback。正文/编辑区/终端文字保证可读。
5. **亲手完成一个可迁移的难点**：Aero caption/按钮状态的一份自包含AXAML资源/控件样板放teacher/avalonia，不写生产文件；须按本仓库实际Avalonia版本/语法。可以是经过最小工装编译的模板，也可以如实标“尚未编译示意”，不得伪称已接产品。讲清ResourceDictionary/ControlTheme/scoped Style的落点，不能要求后续模型猜CSS→AXAML规则。
6. **给便宜模型的限定任务prompt**：分成“token补齐”“控件状态迁移”“资产补齐”“像素比对”几个小包，逐包写允许文件/输入样板/完成条件，仍标L等级与完整group模型建议。不可替M发起后续线程。

## 停止与成本

只有上述有限样板+参考书，工作内部自主连续完成；不先写一份空计划然后等待。无需全仓扫描/重跑全量App。普通HTML渲染/AXAML编译由机械工具执行，无额外云请求；若需要委派验证，用显式devin/swe-2，最多一个、不能让贵模型担当验证lead。没有工具不伪造截图；交已完成文件和具体缺口。不得因美化无限拓展Luna/Aero全应用或修改功能、补原生验收。

逐可验证增量提交，交付卡包含实际head/起点/文件/截图/编译结果/未验证项/源控件映射。终答只JSON delivery.card，停写。模块接受、生产接线、用户视觉确认、原生验证四件事分开。M独立审核后由下一执行者学习与迁移，不继续烧Opus额度做普通实现。
