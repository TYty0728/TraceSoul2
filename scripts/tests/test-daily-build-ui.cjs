const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const html = fs.readFileSync(path.join(__dirname, '../../Tools/Host/wwwroot/index.html'), 'utf8');
const start = html.indexOf('  let dailyPlan = null;');
const end = html.indexOf('  async function loadLadder()', start);
assert.ok(start >= 0 && end > start);
class Element {
  constructor() { this.value = ''; this.children = []; this.disabled = false; this.textContent = ''; }
  replaceChildren() { this.children = []; this.textContent = ''; }
  appendChild(child) { this.children.push(child); }
}
const nodes = Object.fromEntries(['dailyDay', 'dailyStart', 'dailyPreflight'].map(id => [id, new Element()]));
let posts = 0;
let status = { day: '2026-09-20', days: ['2026-09-18', '2026-09-20'],
  providerId: 'review', model: 'test', busy: false, ready: false,
  historicalFailures: [{ day: '2026-09-18', stage: '事件观察', reason: 'HTTP 402' }],
  recentAttempts: [], blockers: ['模型通道已暂停'] };
const sandbox = { $: id => nodes[id], document: { createElement: () => new Element() },
  encodeURIComponent, showSection: async () => {}, loadFailures: async () => {}, logLine: () => {},
  alert: message => { throw new Error(message); },
  api: async (url, method, body) => {
    if (url.startsWith('/memory/daily-status?')) return structuredClone(status);
    assert.equal(url, '/memory/daily-run');
    assert.equal(method, 'POST');
    assert.deepEqual(JSON.parse(JSON.stringify(body)), { day: '2026-09-20' });
    posts++;
    status = { ...status, busy: true, ready: false };
    return { days: status.days, message: '历史优先' };
  }
};
vm.createContext(sandbox);
vm.runInContext(html.slice(start, end), sandbox);
(async () => {
  await sandbox.runDaily();
  assert.equal(posts, 0, '检查不能直接启动付费构建');
  assert.equal(nodes.dailyStart.disabled, true);
  assert.match(nodes.dailyPreflight.children.map(x => x.textContent).join('\n'), /2026-09-18.*402/);
  await sandbox.startDailyPlan();
  assert.equal(posts, 0, '存在阻塞时不能提交');
  status = { ...status, blockers: [], ready: true };
  await sandbox.inspectDaily();
  assert.equal(nodes.dailyStart.disabled, false);
  sandbox.invalidateDailyPlan();
  await sandbox.startDailyPlan();
  assert.equal(posts, 0, '改日期使旧检查失效');
  await sandbox.inspectDaily();
  await sandbox.startDailyPlan();
  assert.equal(posts, 1);
  assert.equal(nodes.dailyStart.disabled, true, '执行期间防止重复提交');
  await sandbox.startDailyPlan();
  assert.equal(posts, 1);
  console.log('Daily build UI checks passed: inspect first, blockers, explicit start and stale-plan invalidation.');
})().catch(error => { console.error(error); process.exitCode = 1; });
