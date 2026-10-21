# 外部调研结果

日期：2026-10-21。

本目录保存外部 agent 的调研报告、证据和原型副本。报告结论不自动成为架构决定；当前权威仍见 [docs/README.md](../README.md)。

| 目录 | 内容 | 说明 |
|---|---|---|
| `R01/` | Pi 插件/上游接口调研 | `evidence/` 是第三方上游源码与文档快照；大文件、非产品代码 |
| `R02/` | Windows 多窗口调研 | 报告 |
| `R03/` | 节点控制台调研 | 报告与证据 |
| `R04/` | 视觉风格调研 | 报告与 mockup |
| `R05/` | 前端原型研究 | `index.html`/`interactions.js`/`lucide.min.js` 与 `../frontend-prototype/` 重复；产品原型以 `../frontend-prototype/` 为准 |

规则：

1. 第三方证据保留来源/版本；不要把它当成本产品验收。
2. 原型副本与 `frontend-prototype/` 重复时，只保留一份真源；历史报告可以引用副本路径。
3. 新调研放 `Rxx/`，报告写结论、证据、未验证项；不要修改 `docs/design` 的当前规格。
