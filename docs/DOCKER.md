# Ubuntu Docker 部署

Docker 只固定 .NET 8 运行环境，真正需要迁移和备份的内容全部位于仓库旁的 `runtime/`。容器不挂载 Docker Socket；WebUI 更新下载对应 Linux Release，并在 `runtime/` 内原子替换 `App`。

## 1. 准备服务器

安装 Docker Engine 与 Compose 插件，确认：

```bash
docker version
docker compose version
```

克隆仓库时必须拉取 Git LFS 模型：

```bash
git clone https://github.com/TYty0728/TraceSoul2.git
cd TraceSoul2
git lfs pull
```

也可以把本机整个项目目录和已导出的 `runtime/` 一起传到服务器。

## 2. 迁移现有 Windows 数据

先停止旧 Host，再在 Windows 项目目录执行：

```powershell
.\scripts\Export-DockerRuntime.ps1 `
  -SourceHome 'D:\TraceSoul2' `
  -SourcePlugins 'D:\AISoftWare\TraceSoul2\plugins' `
  -SourcePluginsData 'D:\AISoftWare\TraceSoul2\plugins_data'
```

脚本会复制角色、数据库、插件和插件数据，把导出副本的插件路径改为相对目录，并清空只适用于 Windows 的 `napcat_path`。`runtime/` 已被 Git 忽略，绝不能提交。

把 `runtime/` 传到 Ubuntu 后，检查这些机器相关配置：

- NapCat：需要在 Ubuntu 上独立部署，并让它连接映射后的 OneBot 端口；
- 游戏插件中的游戏/MCP 路径：需要在 Linux 重新安装或重新选择；
- 指向宿主机服务的 `127.0.0.1`：容器里应改成 `host.docker.internal`；
- OneBot 反向 WS 默认映射 `127.0.0.1:9021`，自定义端口时同步修改 Compose 环境变量和角色配置。

## 3. 一键启动

```bash
chmod +x scripts/*.sh
./scripts/docker-up.sh
```

查看状态：

```bash
docker compose ps
docker compose logs -f --tail=200 tracesoul2
```

停止：

```bash
docker compose down
```

Compose 默认使用当前 Linux 用户的 UID/GID 写入 `runtime/`，避免数据文件变成 root 所有。如果目录来自其他账号，先把它交给运行 Docker 的用户。

## 4. 打开 WebUI

5080 和 9021 都只绑定服务器回环地址。在本地电脑建立隧道：

```bash
ssh -L 5080:127.0.0.1:5080 user@server
```

打开 `http://127.0.0.1:5080`。不要公开映射 5080，也不要给它配置公网反向代理。

## 5. WebUI 一键更新

更新页仓库填写 `TYty0728/TraceSoul2`。正式发布后，Host 会按 CPU 架构选择 `linux-x64` 或 `linux-arm64` ZIP，校验 SHA-256，从 `Data/updates/` 启动外置更新器，替换 `App` 后由容器内 supervisor 拉起新 Host。

更新不会替换：

- `Data/`
- `Plugins/`
- `plugins_data/`

旧 `App` 会作为隐藏备份目录保留在 `runtime/`，确认新版稳定后再人工清理。

## 6. 整目录带回本地

停止容器后复制整个 `runtime/`。在另一台 Linux 或安装了 Docker Desktop 的电脑上，把它放回项目根目录并再次运行 `scripts/docker-up.sh` 即可。SQLite 的 `-wal`、`-shm` 文件必须和主数据库一起复制，不能在线漏拷。
