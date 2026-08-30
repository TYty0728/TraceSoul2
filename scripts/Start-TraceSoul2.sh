#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
export TRACESOUL2_HOME=${TRACESOUL2_HOME:-"$SCRIPT_DIR/../Data"}
export TRACESOUL2_PLUGINS=${TRACESOUL2_PLUGINS:-"$SCRIPT_DIR/../Plugins"}
export TRACESOUL2_PLUGINS_DATA=${TRACESOUL2_PLUGINS_DATA:-"$SCRIPT_DIR/../plugins_data"}
export TRACESOUL2_RESTART_MODE=${TRACESOUL2_RESTART_MODE:-supervisor}

stopping=0
child_pid=""

stop_host() {
  stopping=1
  if [ -n "$child_pid" ]; then
    kill -TERM "$child_pid" 2>/dev/null || true
  fi
}

trap stop_host INT TERM

while [ "$stopping" -eq 0 ]; do
  dotnet "$SCRIPT_DIR/TraceSoul2.Host.dll" &
  child_pid=$!
  set +e
  wait "$child_pid"
  exit_code=$?
  set -e
  child_pid=""
  if [ "$stopping" -ne 0 ]; then
    exit 0
  fi
  echo "TraceSoul2 Host 已退出（$exit_code），5 秒后由 supervisor 重启。"
  sleep 5
done
