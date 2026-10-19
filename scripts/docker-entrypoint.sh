#!/bin/sh
set -eu

ROOT=${TRACESOUL2_BUNDLE_ROOT:-/opt/tracesoul2}
APP="$ROOT/App"
SEED=/opt/tracesoul2-seed/App

mkdir -p "$APP" "$ROOT/Data" "$ROOT/Plugins" "$ROOT/plugins_data"
if [ ! -f "$APP/tracesoul2.install.json" ]; then
  if [ -n "$(find "$APP" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]; then
    echo "[停止] $APP 不是空目录，但缺少 tracesoul2.install.json。" >&2
    exit 1
  fi
  cp -a "$SEED/." "$APP/"
  echo "已从镜像初始化 TraceSoul2 App。"
fi

# Fresh installs receive the same official plugins as Release packages. Existing
# plugin code and all plugins_data remain untouched; upgrades use the updater.
for plugin in "$APP"/BundledPlugins/*; do
  [ -d "$plugin" ] || continue
  name=${plugin##*/}
  if [ ! -e "$ROOT/Plugins/$name" ] && [ ! -L "$ROOT/Plugins/$name" ]; then
    cp -a "$plugin" "$ROOT/Plugins/$name"
  fi
done

exec sh "$APP/Start-TraceSoul2.sh"
