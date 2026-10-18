# 轨道 B 任务：M5 管理页验收扩展集

> 小任务，为轨道 D 的 M5 插件管理页提供验收弹药。D 只有两个正经扩展（bridge + demo），管理页的"加载错误展示"与"多扩展启停"路径需要专门的测试扩展才能验收。开工前 `git merge main`。

## 范围（只允许写这些）

- `extensions/`（新增 2 个 fixture 扩展目录）
- `scripts/demo-verify/`（如需验证脚本）
- `docs/archive/progress/progress-b.md`

## 任务

1. **`extensions/atria-widget-lab/`**：一个功能丰富但无害的扩展——注册工具（只读类）、扩展命令、全量 ctx.ui 面（select/input/editor/notify/setStatus/setWidget/setTitle），供 D 的管理页做"正常多能力扩展"的状态展示与启停测试。
2. **`extensions/broken-fixture/`**：一个**故意**在加载期抛错的扩展（明确的错误信息），供管理页验证"最近加载错误"展示与 A03 语义（实测已知：坏扩展会导致 Pi 启动即退出——这个行为本身也要在文档里标注给 D，作为管理页"该扩展导致启动失败"的诊断素材）。
3. 两者均带 package.json 声明与 README 说明用途；**不得**混入 bridge/demo 的行为。

## 完成标准

- 两个扩展可被 Pi 加载（broken-fixture 可被复现失败）；progress-b.md 说明各自触发方式与管理页验收用法。
