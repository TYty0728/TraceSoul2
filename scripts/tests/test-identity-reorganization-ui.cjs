const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(require('node:path').join(__dirname, '../../Tools/Host/wwwroot/index.html'), 'utf8');
const source = html.slice(html.indexOf('  let identityReorgTimer'), html.indexOf('  let environmentSettings'));
const elements = new Map();
function element() { return { value: '', textContent: '', style: {}, hidden: false, disabled: false, children: [], replaceChildren() { this.children = []; }, append(...items) { this.children.push(...items); } }; }
const $ = id => { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); };
$('identityReorgSlot').value = 'relation';
const calls = [], alerts = [];
let state = { busy: false, defaultDay: '2026-10-22', draft: null };
const context = vm.createContext({ $, document: { createElement: element }, clearTimeout() {}, setTimeout() { return 1; }, alert: message => alerts.push(message), renderCards() {},
  api: async (path, method = 'GET', body) => { calls.push({ path, method, body }); if (path === '/identity/cards') return []; return state; } });
vm.runInContext(source, context);
(async () => {
  await vm.runInContext('loadIdentityReorganization()', context);
  assert.equal($('identityReorgDay').value, '2026-10-22');
  assert.equal($('identityReorgApply').hidden, true);
  state = { busy: false, defaultDay: '2026-10-22', draft: { id: 'candidate', slot: 'relation', day: '2026-10-22', status: 'ready', original: '原卡', batchesDone: 1, batchesTotal: 1,
    proposal: { body: '<img src=x onerror=alert(1)>', change: '认识变化', cognition_ids: ['r1'] }, understandings: [{ meaning: '关系认识', scope: '条件', exceptions: '反证', certainty: '初步', cognition_ids: ['r1'] }], details: [{ id: 'd1', reason: '经历细节' }] } };
  await vm.runInContext('loadIdentityReorganization()', context);
  assert.equal($('identityReorgApply').hidden, false);
  assert($('identityReorgPreview').children.some(x => x.textContent === state.draft.proposal.body));
  await vm.runInContext('applyIdentityReorganization()', context);
  assert(calls.some(x => x.path.endsWith('/apply') && x.body.id === 'candidate'));
  state.busy = true;
  await vm.runInContext('loadIdentityReorganization()', context);
  assert($('identityReorgStart').disabled && $('identityReorgRestart').disabled && $('identityReorgApply').hidden);
  state.busy = false;
  await vm.runInContext('startIdentityReorganization(true)', context);
  assert(calls.some(x => x.method === 'POST' && x.body.restart === true && x.body.day === '2026-10-22'));
  assert.equal(alerts.length, 0);
  console.log('Identity reorganization UI passed: preview, text rendering, explicit apply, busy state and restart date.');
})().catch(error => { console.error(error); process.exitCode = 1; });
