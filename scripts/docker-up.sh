#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
cd "$REPO_ROOT"

if ! command -v docker >/dev/null 2>&1; then
  echo "[停止] 找不到 docker。请先安装 Docker Engine 与 Compose 插件。" >&2
  exit 1
fi
if ! docker compose version >/dev/null 2>&1; then
  echo "[停止] 找不到 docker compose 插件。" >&2
  exit 1
fi

mkdir -p runtime/App runtime/Data runtime/Plugins runtime/plugins_data
export TRACESOUL2_UID=${TRACESOUL2_UID:-$(id -u)}
export TRACESOUL2_GID=${TRACESOUL2_GID:-$(id -g)}
docker compose up -d --build
echo "TraceSoul2 已启动：http://127.0.0.1:${TRACESOUL2_PORT:-5080}"
echo "首次启动请运行 docker compose logs tracesoul2 查看一次性管理员密码。"
echo "远程访问请使用 SSH 隧道，或按 docs/DOCKER.md 配置 HTTPS 反向代理。"
