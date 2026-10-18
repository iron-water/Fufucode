> Pi 后端插件诊断工具；默认原生包不包含也不需要它。

# Pi 升级探针

所有模型请求只发送至随机端口的 `127.0.0.1` 合成 SSE 服务，不需要云端凭据。安装目录只读；会话、代理 agent 配置与逐进程原始 RPC/模型请求/stderr 证据写入 `-Output`（默认 `tmp/pi-probe-<timestamp>`）。默认创建独立项目；`-Project` 指定目录时，审批探针会在该目录创建合成文件，因此建议只传临时项目。

## 两个版本

```powershell
./scripts/pi-probe/run-probe.ps1 -PiRoot runtime/pi -ComparePiRoot tmp/pi-0.99.2 -Output tmp/pi-probe-baseline-final -GenerateFixture -SelfTest
./scripts/pi-probe/run-probe.ps1 -PiRoot tmp/pi-0.99.2 -ComparePiRoot runtime/pi -BaselineReport tmp/pi-probe-baseline-final/probe-report.json -Output tmp/pi-probe-0.99.2-final -SelfTest
```

`-PiRoot` 接受含 `node_modules/@earendil-works/pi-coding-agent`（也识别旧 scope `@mariozechner`）的安装目录；Node 默认使用项目 `runtime/node/node.exe`，可用 `-NodeExe` 替换。`-BaselineReport` 输出逐项状态、命令增删、sourceInfo 改动、SDK 内置扩展与 `--no-extensions` 清单对比到 `probe-diff.json`。

报告包括 `probe-report.json`、Markdown 摘要，以及每个场景目录的 `rpc-*.json`。每类失败独立记录，不因一项 FAIL 跳过其他类。退出码：`0` 全 PASS，`1` 至少一项 FAIL 或自测失败，`2` 无 FAIL 但存在 SKIP。SKIP 不是通过。

RPC 启动禁用用户 skills、prompt templates、themes：`--no-skills --no-prompt-templates --no-themes`，保留默认内置 extensions 的行为用于比较；第 09 类另外启动 `--no-extensions` 进程。第 06 类按产品组合使用 `--no-extensions --extension <真实 fufucode-bridge>`，验证显式桥仍注册并且允许/拒绝分别产生/阻止真实文件写入。SDK source 探针额外禁用 context files 并使用实际包内 `builtInExtensions`。

## 十类断言

| 类 | 要求 |
|---|---|
| 01 | 启动、get_state 布尔状态/会话 ID、非空模型列表 |
| 02 | get_entries 全量、since 排除游标、leafId 一致、无效游标失败 |
| 03 | 普通 text_delta、合成正文、agent_start → agent_end → agent_settled |
| 04 | streaming 中 steer/follow_up；clear_queue 返回精确两队列并清空；abort 后 idle |
| 05 | set_model 成功对象与不存在模型失败 |
| 06 | 产品启动参数下真实桥注册、真实 confirm 允许/拒绝、磁盘效果 |
| 07 | 显式坏扩展 code=1 且 stderr 诊断、无 ready；agent_start 异常产生 extension_error |
| 08 | 实际 SDK factories/加载来源/错误来源、普通 inline 与 builtin 区分 |
| 09 | 默认 get_commands schema 与禁扩展清单、版本 diff |
| 10 | 真实 0.87.1 fixture；两个版本分别写入再由对方全量读取并追加、旧 entry 不丢不改 |

第 08 类目前有严格命名 oracle 的版本是 `0.87.1`、`0.99.2`、`1.0.0` 和 `1.1.0`。其他版本该类 SKIP，需人工审阅再补充版本 oracle，不能把未知命名直接当兼容。普通 named factory 保留 `<inline:name>`；只有新版本 `builtin:true` factory 使用 `builtin:<name>`。1.0.0/1.1.0 严格检查四个内置 factory 的 builtin/replaceable flags，依据实际 SDK loader 输出与安装包 `dist/extensions/index.js`。旧 hidden factory 不承诺公开 sourceInfo。

`-Features` 增加第 11 类：显式内置 codemode/tool-search/MCP + 真实审批桥；本机 stdio MCP 与合成模型验证 QuickJS 调用、内层 write 放行/拒绝和 codemode-store 持久化。不是远程 MCP/OAuth/云模型验收。跨版本测试只修改夹具副本头部 cwd 为存在的隔离项目，原始 entries 与 checked-in fixture 保持不变；Pi 1.1.0 恢复 cwd 时会拒绝不存在的目录。

第 10 类必须传 `-ComparePiRoot`，两个版本必须不同，否则不会声称双向通过。`-GenerateFixture` 只允许真实 `0.87.1`；fixture 源自真实 Pi 的持久化 JSONL，保留原始 header、entry ID 与 parentId，来源写入 `fixtures/provenance.json`。当前 fixture 覆盖模型/思考配置与普通文本一轮；压缩、fork、重试、跨 provider 的生产历史不是本 fixture 的验收范围。

## FakePi 与断言自测

先构建 `tests/Fufucode.FakePi`，`-SelfTest` 将运行已构建的 .NET FakePi。独立调用：

```powershell
runtime/node/node.exe scripts/pi-probe/self-test.mjs tmp/pi-probe-self-test
```

自测四项直接使用 FakePi 实际管道：启动、真实 fixture 游标、stream 生命周期、clear_queue；另外十项向同一断言函数注入负向变异，要求抛出 AssertionError。自测 14 PASS 证明这些断言会拦截相应错误，不能证明 FakePi 支持 SDK 内置扩展、真实桥、模型清单或真实跨版本兼容。

可用 `-FakePi <dll>` 将整套真实 RPC 探针指向 FakePi，缺失能力如实 FAIL/SKIP，此模式不是升级准入门。FakePi 的 ID 是字符串，套件仅适配传输 ID 类型，不修改 FakePi 行为。FakePi 缺少 `get_available_models`、`steer`、`follow_up` 的完整真实契约，不应该通过真实兼容探针。
