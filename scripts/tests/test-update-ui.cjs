const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");
const html = fs.readFileSync(path.join(__dirname, "../../Tools/Host/wwwroot/index.html"), "utf8");
const scripts = [...html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi)].map(x => x[1]).join("\n");
new Function(scripts);
const start = scripts.indexOf("/* ================= 系统更新 ================= */");
const end = scripts.indexOf("/* ================= 顶栏与整体刷新 ================= */", start);
assert(start > 0 && end > start);
function setup() {
  const nodes = new Map(), timers = new Set(), storage = new Map();
  let counter = 0, reloads = 0;
  const sandbox = {
    $: id => {
      if (!nodes.has(id)) nodes.set(id, { textContent: "", hidden: false, style: {}, classList: { toggle() {} } });
      return nodes.get(id);
    },
    setInterval: () => { const id = ++counter; timers.add(id); return id; },
    clearInterval: id => timers.delete(id),
    setTimeout: () => ++counter, clearTimeout() {},
    Date, AbortController,
    location: { reload: () => reloads++ },
    sessionStorage: { getItem: k => storage.get(k), setItem: (k,v) => storage.set(k,v), removeItem: k => storage.delete(k) },
    confirm: () => true, logLine() {}, lockDashboard() {},
    api: async () => ({ started: true, message: "后台安装已提交" }),
    fetch: async () => ({ ok: true, json: async () => sandbox.state })
  };
  sandbox.state = { currentVersion: "0.1.6", configured: true, installable: true,
    latest: { version: "0.1.7", updateAvailable: true }, install: { phase: "idle", inProgress: false } };
  const context = vm.createContext(sandbox);
  vm.runInContext(scripts.slice(start, end), context);
  return { sandbox, nodes, timers, storage, eval: code => vm.runInContext(code, context), reloads: () => reloads };
}
(async () => {
  let s = setup();
  s.state = s.sandbox.state;
  s.eval("renderUpdateStatus(state)");
  s.sandbox.state.install = { phase: "connecting", inProgress: true, version: "0.1.7", percent: 5, message: "GitHub API 连接中" };
  s.eval("renderUpdateStatus(state)");
  assert.equal(s.nodes.get("updateProgressBar").style.width, "5%");
  assert.equal(s.nodes.get("installUpdateBtn").disabled, true);
  assert.equal(s.storage.get("updateExpectedVersion"), "0.1.7");
  assert.equal(s.timers.size, 1);
  s.sandbox.state.install = { phase: "failed", inProgress: false, percent: 30, error: "重试耗尽" };
  s.eval("renderUpdateStatus(state)");
  assert.equal(s.timers.size, 0);
  assert.equal(s.storage.size, 0);
  assert.equal(s.nodes.get("installUpdateBtn").disabled, false);

  s = setup();
  s.eval("renderUpdateStatus(state)");
  s.sandbox.api = async () => {
    s.sandbox.state.install = { phase: "downloading", inProgress: true, version: "0.1.7", percent: 20 };
    return { started: true };
  };
  await s.eval("installUpdate()");
  assert.equal(s.timers.size, 1, "Installing should poll, not wait for the entire download");
  assert.equal(s.reloads(), 0);
  s.sandbox.state = { currentVersion: "0.1.7", configured: true, installable: true, install: { phase: "idle" } };
  await s.eval("pollUpdateInstall()");
  assert.equal(s.reloads(), 1);
  assert.equal(s.storage.size, 0);
  assert.equal(s.timers.size, 0);

  s = setup();
  s.eval('rememberUpdateTarget("0.1.7"); startUpdateInstallPolling()');
  await s.eval("pollUpdateInstall()");
  assert.equal(s.timers.size, 0, "Restart on old version should not poll forever");
  assert.match(s.nodes.get("updateStatus").textContent, /未确认目标版本/);

  s = setup();
  s.eval('rememberUpdateTarget("0.1.7"); startUpdateInstallPolling()');
  s.sandbox.fetch = async () => { throw new Error("disconnected"); };
  await s.eval("pollUpdateInstall()");
  assert.equal(s.timers.size, 1, "Temporary disconnect is not installation failure");
  s.eval("updateDisconnectedAt = Date.now() - 181000");
  await s.eval("pollUpdateInstall()");
  assert.equal(s.timers.size, 0, "Unreachable host must eventually stop polling");
  assert.match(s.nodes.get("updateStatus").textContent, /不代表安装一定失败/);
  console.log("WebUI update tests passed: background submit, progress, failure/retry, new-version reload, rollback, bounded reconnect.");
})().catch(error => { console.error(error); process.exitCode = 1; });
