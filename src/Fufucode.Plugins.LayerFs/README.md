# LayerFS 工作区插件

可选插件 `workspace.layerfs`。安装启用后贡献一个 LayerFS 工作区页面，默认不安装，不替换已有会话的工具执行器。通过 `wsl.exe` 在所选 WSL2 发行版中直接调用 LayerFS v1.1.0 的 Linux SDK 与 Runtime。主程序不需要 Node 或 Docker。

支持导入项目独立副本、列出项目、查看项目状态和提交、创建分支、创建沙箱、挂载分支、执行命令、保存检查点、卸载工作区和关闭空沙箱。输出保留官方 SDK 的完整 JSON。命令失败保留退出码，结果未知时不会自动重放。

## 使用

1. 在插件市场安装并启用“LayerFS 工作区”。
2. 打开 LayerFS 页面，在“WSL2 连接设置”中填写发行版、用户、Node 和配置文件的绝对 Linux 路径，保存后检查环境。
3. 填写源目录和新的 LayerFS 项目名，点击“导入项目”。项目名为 1–63 位小写字母、数字、`_`、`.`、`-`，首尾为字母或数字。
4. 刷新并选择项目，从 `main` 创建一个工作分支。创建沙箱后挂载此分支，页面自动填入沙箱编号和挂载路径。
5. 执行命令，然后保存检查点。完成后卸载工作区并关闭空沙箱。

源目录可以是 Windows 驱动器路径、所选发行版的 WSL UNC 路径或 Linux 绝对路径。导入包含 `.git` 和忽略文件，会占用相应空间。大目录导入受到 LayerFS 的 300 秒导入期限约束。

**LayerFS 当前只有 Disposable 存储模式，不保证进程崩溃或断电后的持久性。请保留源项目与重要结果的独立备份。** 挂载工作区属于 Docker 容器中的路径，不能当作宿主文件夹使用。默认 Node 镜像提供 Node、Git、Python、编译工具，不承诺包含项目需要的全部依赖。

## WSL2 部署

需要支持 API 1.54 的 Linux Docker Engine（不是 Windows Docker named pipe；Ubuntu 的 Docker 29.1/API 1.52 不够）、`/dev/fuse`、Node >=22.19、Rust/Cargo、编译依赖。使用固定来源 <https://github.com/Ephemeral-AI-Lab/layerfs/tree/v1.1.0> 构建 `layerfs-daemon` 和 `layerfs-node`，并在其 `api` 与 `api/agent/cli` 目录运行 `npm install`（仅链接本地包）。

本插件以 Linux 进程运行，不启动 Windows SDK worker，因此不需要 sealed Windows-worker bundle。三个 JS 包、原生绑定、daemon 必须来自同一版本。原生代码构建：

```bash
cargo build --manifest-path core/Cargo.toml --locked --release -j 4 -p layerfs-node --lib -p layerfs-daemon --bins
```

提供 `install-wsl.sh` 接收源码目录、release 产物目录、Node 目录与镜像 ID，创建默认部署（已有配置时拒绝覆盖）。`Runtime.Dockerfile` 是本次 Ubuntu 26.04 构建对应的沙箱镜像配方。Docker 29.9.2/API 1.56 已实测满足调用需求。

构建应带仓库 `.cargo/config.toml` 定义的 `LIBSQLITE3_FLAGS=-USQLITE_ENABLE_MEMORY_MANAGEMENT`。将 `liblayerfs_node.so` 作为 `layerfs.node` 安装，记录所有源版本与实际 SHA256。运行时镜像需要与 daemon 的 Linux 动态库兼容，以镜像 ID `sha256:…` 配置。

默认连接为 `Ubuntu-26.04`、`root`、`/opt/fufucode-layerfs/node/bin/node`、`/var/lib/fufucode-layerfs/config.json`。可修改为其他发行版/安装目录。Linux runtime 需要对 Docker 卷目录的读写权限，所以默认 root。Store 与 discovery home 必须在发行版的 Linux 文件系统上，不能放在 `/mnt/c`。

配置格式：

```json
{
  "source": "/opt/fufucode-layerfs/source",
  "native": "/opt/fufucode-layerfs/layerfs.node",
  "daemon": "/opt/fufucode-layerfs/layerfs-daemon",
  "home": "/var/lib/fufucode-layerfs/home",
  "image": "sha256:<实际镜像ID>",
  "docker": "/var/run/docker.sock",
  "volumes": "/var/lib/docker/volumes"
}
```

插件只在用户触发操作时启动短时桥接进程，不开放额外网络服务。停用/卸载取消进行中的调用并释放桥接子进程；中断后调用的结果可能未知，需刷新检查。WSL 超时守卫限制残留 Linux 调用的生命周期。插件不停止无关容器，不删除 Store、项目或挂载。宿主设置保存在 `<dataRoot>/layerfs/settings.json`，重装可复用；Linux 数据与自行安装的 Docker/Node/LayerFS 均保留，插件卸载不代表移除它们。
