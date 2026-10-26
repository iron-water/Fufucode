# 交互增强（chat.interactive-ui）

把消息里的 ` ```ui ` 围栏渲染成原生交互组件。围栏内**每行一个 JSON 对象**；
解析失败只影响那一行，其余组件照常显示。

## 基础组件（默认轨道可用）

```ui
{"type":"slider","label":"温度","min":0,"max":2,"step":0.1,"value":0.7}
{"type":"select","label":"模型","options":["A","B","C"],"value":"B"}
{"type":"toggle","label":"启用缓存","value":true}
{"type":"chips","label":"平台","options":["iOS","Android","Web"],"multi":true,"value":"Android"}
{"type":"input","label":"备注","placeholder":"可选"}
{"type":"number","label":"并发数","min":1,"max":16,"value":4}
{"type":"rating","label":"满意度","max":5,"value":4}
{"type":"progress","label":"进度","value":60,"max":100}
{"type":"actions","options":["按这个继续"]}
```

`actions` 会把当前所有组件的值汇总成一段文本作为新消息提交；
`"mode":"insert"` 则改为填入输入框而不发送。

## 扩展组件（仅完整特性轨道）

chart / table 需要 `EnableEnhancedPreview=true`。纯净轨道下这两类会被当作不支持的类型。

```ui
{"type":"chart","label":"耗时","chart":"bar","series":[{"name":"模型","points":[{"x":"A","y":12.5},{"x":"B","y":8}]}]}
{"type":"table","label":"结果","columns":["名称","耗时"],"rows":[["A","12ms"],["B","8ms"]]}
```

- `chart`：`bar` / `line` / `pie`；多序列时每个 `series` 一项。
- `table`：最多 8 列；列头下方的选择器可切换排序列与升/降序。
