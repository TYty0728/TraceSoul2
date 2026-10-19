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

WebUI 使用独立的单管理员认证。首次启动生成 24 位随机密码，只在日志显示一次：

```bash
docker compose logs tracesoul2 | grep -A 4 '控制台已创建管理员账号'
```

用 `admin` 和该密码首次登录后，必须立即修改用户名和密码。服务器只保存 PBKDF2-SHA256 密码摘要；登录会话保存在 HttpOnly、SameSite=Strict 的加密 Cookie 中。

5080 和 9021 默认都只绑定服务器回环地址。在本地电脑建立隧道：

```bash
ssh -L 5080:127.0.0.1:5080 user@server
```

打开 `http://127.0.0.1:5080`。仅在配置了下述域名白名单和 HTTPS 后，才可通过公网反向代理访问。

### 公网域名访问

如需公网使用，必须准备一个解析到服务器的域名，并由 Caddy 等反向代理自动申请 HTTPS 证书。不要把 Compose 的 `127.0.0.1:5080` 改成 `0.0.0.0`。

先在仓库根目录创建不会被 Git 提交的 `.env`：

```dotenv
TRACESOUL2_PUBLIC_HOSTS=soul.example.com
```

多个域名用英文逗号分隔。然后重新创建容器：

```bash
docker compose up -d --build
```

Caddy 最小配置如下，域名替换成自己的：

```caddyfile
soul.example.com {
    reverse_proxy 127.0.0.1:5080
}
```

公网域名不在 `TRACESOUL2_PUBLIC_HOSTS` 白名单、请求不是 HTTPS，或者浏览器 Origin 与访问域名不一致时，Host 都会拒绝访问。OneBot 和游戏桥 WebSocket 不使用管理员 Cookie，仍分别校验自己的 `access_token`。

如果忘记初始密码且还没有重要登录会话，先停止容器，仅删除认证文件与加密会话密钥，再启动生成新密码：

```bash
docker compose down
rm -- runtime/Data/control-auth.json
rm -r -- runtime/Data/auth-keys
docker compose up -d
docker compose logs tracesoul2 | grep -A 4 '控制台已创建管理员账号'
```

这会使所有旧登录会话失效，但不会删除角色、聊天记录、模型设置或插件数据。

## 5. WebUI 一键更新

更新页仓库填写 `TYty0728/TraceSoul2`。正式发布后，Host 会按 CPU 架构选择 `linux-x64` 或 `linux-arm64` ZIP，校验 SHA-256，从 `Data/updates/` 启动外置更新器，替换 `App`、升级 Release 内声明的官方插件包，再由容器内 supervisor 拉起新 Host。应用与每个被替换的官方插件都会留下独立备份，任一步失败都会尽力回滚。

安装页面会实时显示下载大小、百分比和当前阶段。详细记录位于宿主机 `runtime/Data/updates/update.log`；即使浏览器刷新或反向代理请求断开，服务器也会继续本次安装。

v0.1.7 起优先走 GitHub API 资产下载、HTTP/1.1、自动重试与断点续传。如果旧版因为 `github.com:443` 超时而无法升级，下载仓库内 `scripts/update-server.py` 后，在宿主机执行一次：

```bash
python3 scripts/update-server.py --root /home/ubuntu/TraceSoul2 --version 0.1.7
```

`--root` 换成实际项目路径。脚本使用 Python 3 标准库，无需 pip 或宿主机 dotnet；需要宿主机 Docker 权限和正在运行的默认 `tracesoul2` 容器。只要安装成功，后续正式 Release 可直接从 WebUI 更新，无需每次执行 SSH 脚本。若下载中断，重跑同一脚本可利用保留的部分文件。执行期间不要同时点击 WebUI 安装。

更新不会替换：

- `Data/`
- `plugins_data/`
- `Plugins/` 中不属于该 Release 的第三方插件

`Plugins/` 中的 `qq-tts`、`qq-imagegen`、`qq-qzone`、`qq-status`、`qq-sticker`、`game-session`、`media-understanding`、`realtime-call`、`tavily` 官方包会随正式版本升级，但对应 `plugins_data/<包名>/` 配置和生成文件保持原样。旧 `App` 与旧官方插件包会作为隐藏备份目录保留在 `runtime/`，确认新版稳定后再人工清理。

## 6. 整目录带回本地

停止容器后复制整个 `runtime/`。在另一台 Linux 或安装了 Docker Desktop 的电脑上，把它放回项目根目录并再次运行 `scripts/docker-up.sh` 即可。SQLite 的 `-wal`、`-shm` 文件必须和主数据库一起复制，不能在线漏拷。

## 7. 0.1.48感官与语音服务

主Docker容器负责Soul、Moment、供应商请求和表情选择，不安装USB驱动、ASR模型、PyTorch或千问GPU模型。没有开发板、声纹或TTS服务时，Host正常启动，WXXZ显示未连接且不会自动监听。新镜像初始化九个官方插件；重启不覆盖已经存在的插件目录或任何plugins_data。已有runtime/App不会因重建镜像而强行升级，仍使用WebUI或上文更新脚本升级。

QQ和WXXZ的共用图库位于`runtime/plugins_data/qq-sticker/`，模型与语义词表随App发布，用户图片和索引跟随持久数据迁移。发布包不含个人图片、录音、声纹或角色参考音频。TTS特殊供应商与Decisions供应商配置仍位于角色目录的私密供应商文件；原账号/角色迁移后继续使用。

### 千问TTS

在「大脑 · LLM」把千问供应商基址改为服务器容器实际可达的`https://voice.example.com/v1`，填写该语音服务的独立API Key并选择语音槽。此域名应是受保护的专用入口，不能直接把Windows回环服务暴露公网；供应商依然校验证书、不跟随重定向、不借用其他供应商密钥。回环HTTP仅适用于**同一网络命名空间**内的服务或SSH端口转发，不接受远端明文HTTP。`host.docker.internal`本身不能替代TLS与鉴权。

模型仍可放在有GPU的个人电脑或独立Linux/CUDA服务上，`Tools/LocalVoice/server.py`以独立Python环境运行，凭据文件支持Linux的0600权限。Windows安装/启动PS1仅是本机辅助工具，不在Linux主容器内执行。服务的HTTPS反向代理须校验API Key、保持`Authorization`头、覆盖上游`Host`为其回环地址，工作台应使用其同源HTTPS入口并转发安全的Origin；不能删掉服务现有Host/Origin或Bearer检查。具体模型依赖和本机安装见[本地语音](../Tools/LocalVoice/README.md)。

### 开发板桥

开发板仍连接有USB的本地电脑，由`Tools/SensoryBoard/bridge.py`采音、ASR及播放。服务器不能直接使用本地电脑的COM端口。可选择受保护的HTTPS网关，或把SSH转发建立在Host所在网络命名空间的回环端口；默认地址仍为`http://127.0.0.1:5089/`。

专用HTTPS入口在Compose的私密`.env`中填写：

```dotenv
TRACESOUL2_WXXZ_BRIDGE_URL=https://body.example.com/wxxz/
TRACESOUL2_WXXZ_BRIDGE_KEY=YOUR_32_TO_256_CHARACTER_PRIVATE_TOKEN
```

远端桥必须配置32～256位英文字母/数字/下划线/短横线的独立Bearer；不能填供应商Key或控制台Cookie。网关负责验证该令牌，再将固定路径转发给本地回环桥，覆盖上游Host为`127.0.0.1:5089`并清除跨站Origin，不接受任意目标URL。桥自身仍仅监听回环并保留来源校验，本版本不自动部署网关或创建隧道。没有网关时不要把URL改成公网IP，不要开放裸5089端口。

配置后重新创建Host容器，再明确连接/开始监听；Host重启不会自动采音。本机密钥、声纹档案和音色参考目录需要备份，均不进入镜像构建上下文或Git。状态预览可在无硬件时演示，但不代表实体屏幕动画已接入。
