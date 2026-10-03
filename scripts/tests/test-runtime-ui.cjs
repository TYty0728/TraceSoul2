const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const html = fs.readFileSync('Tools/Host/wwwroot/index.html', 'utf8');
// Compile the full inline script without starting the application or making requests.
for (const match of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g)) new vm.Script(match[1]);
const nodes = {};
const sandbox = {
  $: id => nodes[id] ??= {}, document: { activeElement: null },
  esc: s => String(s ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
};
vm.createContext(sandbox);
const start = html.indexOf('  function formatHeartbeatDue(');
const end = html.indexOf('  async function loadLive(', start);
assert.ok(start >= 0 && end > start);
vm.runInContext(html.slice(start, end), sandbox);
const state = {
  hostTime: '2026-09-30T22:00:00+08:00',
  runtime: { narrative: '准备休息', mood: '好奇', attention: [{ content: '青苔' }] },
  decision: { step: 'finish', updatedFields: ['today', 'mood'], moodChanged: true, mood: '好奇',
    today: '晚上回家', newFact: '青苔喜欢潮湿', goalUpdates: [{ operation: 'create', content: '学习青苔' }] },
  context: { trajectory: '中午 · 看青苔\n晚上 · 回家', todayNewItems: [{ content: '青苔喜欢潮湿' }] },
  latestTurn: { results: [{ capabilityId: 'memory.recall', status: 'success', summary: '找到了资料' }] }
};
sandbox.renderLive(state);
assert.match(nodes.liveDecision.innerHTML, /finish.*经历、情绪/s);
assert.match(nodes.liveDecision.innerHTML, /好奇.*青苔喜欢潮湿.*学习青苔.*找到了资料/s);
assert.doesNotMatch(nodes.liveDecision.innerHTML, /心智说明|查询 \/ 认知/);
assert.match(nodes.liveRuntime.innerHTML, /青苔/);
assert.match(nodes.liveContext.innerHTML, /中午 · 看青苔.*晚上 · 回家.*青苔喜欢潮湿/s);
state.context.trajectoryGroups = [{ time: '上午', events: ['买了新书', '<script>'] }, { time: '上午', events: ['约好周末读书'] }, { time: '下午', events: ['回家'] }];
state.context.activeEvents = [{ time: '昨天深夜', eventSummary: '互道晚安' }, { time: '昨天深夜', eventSummary: '准备入睡' }, { time: '昨天晚上', eventSummary: '散步回家' }];
sandbox.renderLive(state);
assert.equal((nodes.liveContext.innerHTML.match(/class="live-period-title">上午</g) || []).length, 1);
assert.equal((nodes.liveContext.innerHTML.match(/class="live-period-title">昨天深夜</g) || []).length, 1);
assert.match(nodes.liveContext.innerHTML, /<li>买了新书<\/li>.*<li>约好周末读书<\/li>/s);
assert.match(nodes.liveContext.innerHTML, /&lt;script&gt;/);
assert.doesNotMatch(nodes.liveContext.innerHTML, /<script>/);
// Restart uses the persisted latestTurn decision; older snapshots remain renderable.
state.latestTurn.mindDecision = state.decision;
delete state.decision;
sandbox.renderLive(state);
assert.match(nodes.liveDecision.innerHTML, /finish/);
state.latestTurn.mindDecision = { beat: '当下', note: '旧判断', tags: [] };
sandbox.renderLive(state);
assert.match(nodes.liveDecision.innerHTML, /旧判断/);
sandbox.renderLive({});
assert.match(nodes.liveDecision.innerHTML, /本轮决策/);
console.log('Runtime UI checks passed: Agent decisions, receipts, day history, restart, legacy and empty state.');

// The day page displays one final text; intermediate material remains collapsed.
function element(tag) {
  return { tag, children: [], textContent: '',
    append(...items) { this.children.push(...items); },
    appendChild(item) { this.children.push(item); },
    replaceChildren(...items) { this.children = items; } };
}
sandbox.document.createElement = element;
nodes.runtimeSliceHistory = element('div');
nodes.runtimeSliceDay = { value: '2020-08-21' };
const sliceStart = html.indexOf('  async function loadRuntimeSlices()');
const sliceEnd = html.indexOf('  async function saveEnvironment()', sliceStart);
assert.ok(sliceStart >= 0 && sliceEnd > sliceStart);
vm.runInContext(html.slice(sliceStart, sliceEnd), sandbox);
(async () => {
  const data = { day: '2020-08-21', total: 2, pending: 0, slices: [],
    reviews: [{ summary: '整天的一篇回望', memoryVisibility: 'private' }],
    reviewSegments: [{ summary: '<script>分段一</script>' }, { summary: '分段二' }] };
  sandbox.api = async () => data;
  await sandbox.loadRuntimeSlices();
  let rows = nodes.runtimeSliceHistory.children;
  assert.equal(rows.filter(x => x.tag === 'p' && x.textContent.includes('当天回望 ·')).length, 1);
  assert.ok(rows.filter(x => x.tag === 'p').every(x => !x.textContent.includes('分段一')));
  const material = rows.find(x => x.tag === 'details');
  assert.equal(material.open, undefined);
  assert.equal(material.children[1].textContent, '<script>分段一</script>\n\n分段二');
  assert.equal(material.children[1].innerHTML, undefined);
  data.reviews = [];
  await sandbox.loadRuntimeSlices();
  rows = nodes.runtimeSliceHistory.children;
  assert.ok(rows.some(x => x.textContent.includes('当天回望尚未完成')));
  console.log('Day summary UI checks passed: one final, collapsed material, text safety and pending finalization.');
})().catch(error => { console.error(error); process.exitCode = 1; });
