# U0：自动开发工作台研究与 HTML 交付

日期：2026-10-10 当地日期，验证约 2026-10-11 04:13 UTC。状态：研究与交互原型已交付；用户布局讨论待进行；原生产品实现未开始。

## 交付卡

- taskId：U0；难度 L3；负责人 M 当前交互会话；交付类别 module_ready（研究/原型），非生产原生验收。
- 内容：四个官方选项对比、版本 SHA/许可证、接入判断；保持五区的任务/队列/详情/事件 HTML；U0–U3 任务与依赖/验收契约。
- 文件：[研究](../research/unattended-orchestration-options-2026-10-10.md)、[原型](../frontend-prototype/automation/index.html)、[任务书](../tasks/automation-workbench.md)。提交身份以本记录所在 Git 提交为准，不伪造 worker run、机器交付事件或执行模型。
- 验证：`node --check docs/frontend-prototype/automation/automation.js` 成功；Playwright 在独立命名浏览器 fufucode-automation-preview 实际运行 14 项交互检查。没有模型请求，也没有第三方引擎安装试跑。
- 检查结果：空闲/空重放零模拟审核调用；双交付积压；暂停不审核；明确打回；原会话第二轮修复；module_ready 接受但等待接线；重放不加调用；集成解锁依赖；三份交付卡；投递不明重启仍冻结；有效决定本地对账不重发；主题切换；780px 无横向溢出。
- 证据：`output/playwright/automation-browser-checks.txt`，截图 `automation-light.png` / `automation-dark.png`；console 零错误/警告。截图通过本地 view_image 检查。截图与 CLI 缓存作为本地验证产物，不包含在源码提交中。
- 未完成：用户讨论原型布局；U1 持久状态实现、U2 独立产品宿主/真实引擎适配、U3 Avalonia 界面。示例计数与重启按钮不是实际模型/OS 进程验收。
- 保护：未改继承的 demo-verify JSON、.playwright-cli/.workbuddy 或用户意见文件；未接管未知会话，未恢复 Desktop heartbeat，未 push、部署或删除 worktree。

## 下一步操作卡

用户直接查看已打开的 HTML 或本地 index.html，讨论中间看板、右侧详情及恢复操作。采用独立宿主 + 项目持久状态路线；U1 可按任务书先做独立模块。原生外壳与后端接线不绕过 G1/D5/G2，也不把这份演示状态当生产状态来源。

## 用户纠正后的新版交付（2026-10-11 04:40UTC）

用户明确不要求复刻 Fufucode 前端，改为功能区侧边栏中的一个 tab；所有功能紧围开发流程图，流程动画化。上述初版布局与14项检查保留历史，新版是当前入口。

- 已替换完整外壳/任务列表，当前 tab 以12节点闭环图及依赖子图为核心。节点上下文承载执行会话、交付、决定、集成与恢复；橙色回路明确回原会话。
- 动画沿14个示例事件推进；支持播放、暂停、单步、速度、缩放、适应视图、深浅主题与减少动态效果。动画暂停只是播放控制，不假称停止后台任务。
- 真实浏览器19项通过：连线样式实际随时间移动、双执行者状态、积压、去重、投递不明冻结/重启/对账、打回回路、第二轮原会话身份、接受与集成分开、验证解锁、G1门禁不越过、节点详情、600px无页面横向溢出、自动播放结束、暂停无新事件、减少动态效果生效。console零错误/警告。
- 证据 `output/playwright/automation-flow-checks.txt`；截图 `automation-flow-initial.png`、`automation-flow-rework.png`、`automation-project-dependencies.png`、`automation-flow-dark.png`。新版流程截图已目视检查；本轮没有模型调用。
- U3改为流程图功能tab，执行契约同步。后台设计由M负责，不要求用户选择内部实现；仍遵守原有权限、持久记录、原会话恢复、单写入者与G1/D5/G2验收。
- 下一步操作：用户查看新版动画并讨论节点/信息；原生产品使用同一任务/事件身份驱动状态与动画，不从屏幕表现反推任务完成。

## 线程节点与部分打回修订（2026-10-11 05:20UTC）

用户再次纠正节点粒度：节点必须是对话线程，内含多个任务进度，任务可分部分打回，并恢复原Mermaid的大层级。当前入口已替换上一版12步骤节点图。

- 9个对话线程，50个任务行；主控/首轮/后续阶段外层，首轮内Codex/OMP子组，共5个分组容器。51条连线绑定具体任务端口，涵盖后端、宿主、桌面、Git、管理、审查/插件/调度、原型、主题、手机、U任务及后续能力。
- 局部状态演示：D1-P/message保持已集成；仅D1-P/stop打回、原线程修复、第2轮交付/复审/收编。D1-A继续执行，其他线程/任务不变。任务及部分轮次独立，不把修复轮次附到整个线程。O1/conflicts单独交付收编，O2/O3不变。
- 节点内任务选择、部分勾选/打回、动画、分组折叠、全图/阅读比例及滚动；投递不明冻结不会被点选节点或重启解除。图上门禁始终保持未通过。
- 24项真实浏览器交互/动画检查通过，证据output/playwright/automation-thread-checks.txt；补齐依赖数据后单独核对全部51个端口身份/渲染连线、部分打回标签，证据automation-thread-graph-checks.txt。console零错误/警告，JavaScript语法检查与文档差异检查通过。
- 全图及局部打回截图automation-thread-overview.png、automation-thread-partial-rework.png已目视检查。旧截图/检查保留历史，不作为新版布局证据；本轮无模型请求。
- 新增[线程层级Mermaid](../面向人类/自动开发线程层级图.md)；产品契约增加Thread→Task→DeliveryPart/Revision。既有v1脚本未自动升级，也未迁移真实在途任务；U1/U2须版本化实现和验证新基数与部分审核。
