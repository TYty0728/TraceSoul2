const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const html = fs.readFileSync(path.join(__dirname, '../../Tools/Host/wwwroot/index.html'), 'utf8');
assert.ok(!html.includes('slotThinking') && !html.includes('开口走思考槽'));
assert.ok(html.includes('启用模型推理'));
const nodes = Object.fromEntries(['Chat', 'Jev', 'Review', 'Multimodal', 'Speech', 'Image']
  .map(name => ['slot' + name, { value: '', innerHTML: '' }]));
const writes = [];
const sandbox = {
  $: id => nodes[id],
  esc: value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('"', '&quot;'),
  api: async (url, method, body) => { writes.push({ url, method, body: JSON.parse(JSON.stringify(body)) }); },
  logLine() {}, invalidateDailyPlan() {}, refreshProviders: async () => {},
  alert(message) { throw new Error(message); }
};
vm.createContext(sandbox);
const start = html.indexOf('  const ROLE_LABELS =');
const end = html.indexOf('  async function loadBrain()', start);
assert.ok(start >= 0 && end > start);
vm.runInContext(html.slice(start, end), sandbox);
const saveStart = html.indexOf('  async function saveSlots()');
const saveEnd = html.indexOf('  async function saveReviewSettings()', saveStart);
assert.ok(saveStart >= 0 && saveEnd > saveStart);
vm.runInContext(html.slice(saveStart, saveEnd), sandbox);
vm.runInContext(`providerCache = [{ id: 'p', displayName: '测试', models: [
  { id: 'chat', enabled: true, roles: ['chat'] },
  { id: 'decision', enabled: true, roles: ['jev'] },
  { id: 'disabled', enabled: false, roles: ['chat'] },
  { id: 'picture', enabled: true, roles: ['image'] }
] }]; slotCache = { chat: { providerId: 'p', model: 'chat' } };`, sandbox);
sandbox.renderSlots();
assert.equal(nodes.slotJev.value, '', '旧配置显示未指定');
assert.match(nodes.slotJev.innerHTML, /p\|\|chat/);
assert.match(nodes.slotJev.innerHTML, /p\|\|decision/);
assert.doesNotMatch(nodes.slotJev.innerHTML, /disabled|picture/);

(async () => {
  nodes.slotJev.value = 'p||decision';
  await sandbox.saveSlots();
  assert.equal(writes.length, 1);
  assert.equal(writes[0].url, '/providers/slots');
  assert.equal(writes[0].method, 'PUT');
  assert.deepEqual(writes[0].body.jev, { providerId: 'p', model: 'decision' });
  assert.deepEqual(writes[0].body.chat, { providerId: 'p', model: 'chat' });
  assert.ok(!Object.hasOwn(writes[0].body, 'thinking'), '页面仅保存统一主模型，不再写思考槽');
  vm.runInContext(`slotCache.jev = { providerId: 'p', model: 'decision' };`, sandbox);
  sandbox.renderSlots();
  assert.equal(nodes.slotJev.value, 'p||decision', '重载显示保存的决策槽');
  nodes.slotJev.value = '';
  await sandbox.saveSlots();
  assert.deepEqual(writes[1].body.jev, { providerId: '', model: '' });
  console.log('Provider slots UI checks passed: JEV choices, legacy default, save, reload and clear.');
})().catch(error => { console.error(error); process.exitCode = 1; });
