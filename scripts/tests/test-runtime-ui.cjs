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
