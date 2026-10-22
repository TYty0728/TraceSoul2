const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const html = fs.readFileSync(path.join(__dirname, '../../Tools/Host/wwwroot/index.html'), 'utf8');
const nodes = Object.fromEntries(['fallbackSecondary', 'fallbackTertiary', 'mainFallbackStatus']
  .map(id => [id, { value: '', innerHTML: '', textContent: '' }]));
const calls = [], alerts = [];
let fail = false;
const context = {
  $: id => nodes[id], esc: value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('"', '&quot;'),
  logLine() {}, alert: message => alerts.push(message),
  api: async (url, method, body) => {
    calls.push({ url, method, body: JSON.parse(JSON.stringify(body)) });
    if (fail) throw new Error('不能重复');
    return JSON.parse(JSON.stringify(body));
  }
};
vm.createContext(context);
vm.runInContext(html.slice(html.indexOf('  const ROLE_LABELS ='), html.indexOf('  async function loadBrain()')), context);
vm.runInContext(`providerCache = [
  {id:'p',models:[{id:'main',enabled:true,roles:['chat']},{id:'disabled',enabled:false,roles:['chat']}]},
  {id:'s',models:[{id:'second',enabled:true,roles:['chat']}]},
  {id:'t',models:[{id:'third',enabled:true,roles:['chat']}]},
  {id:'native',type:'openrouter_decisions',models:[{id:'jev',enabled:true,roles:['chat']}]},
  {id:'voice',type:'qwen_tts',models:[{id:'tts',enabled:true,roles:['chat']}]}
]; slotCache = {chat:{providerId:'p',model:'main'}};`, context);
context.renderMainFallbacks();
assert.equal(nodes.fallbackSecondary.value, '');
assert.equal(nodes.fallbackTertiary.value, '');
assert.match(nodes.mainFallbackStatus.textContent, /尚未启用/);
for (const id of ['fallbackSecondary', 'fallbackTertiary']) {
  assert.match(nodes[id].innerHTML, /s\|\|second/);
  assert.doesNotMatch(nodes[id].innerHTML, /disabled|native\|\||voice\|\|/);
}
(async () => {
  nodes.fallbackSecondary.value = 's||second';
  nodes.fallbackTertiary.value = 't||third';
  await context.saveMainFallbacks();
  assert.equal(calls.length, 1, '只保存顺序，不测试模型或发消息');
  assert.equal(calls[0].url, '/providers/main-fallbacks');
  assert.equal(calls[0].method, 'PUT');
  assert.deepEqual(calls[0].body, {secondary:{providerId:'s',model:'second'},tertiary:{providerId:'t',model:'third'}});
  context.renderMainFallbacks();
  assert.equal(nodes.fallbackTertiary.value, 't||third');
  fail = true;
  nodes.fallbackSecondary.value = 'p||main';
  await context.saveMainFallbacks();
  assert.match(alerts[0], /不能重复/);
  context.renderMainFallbacks();
  assert.equal(nodes.fallbackSecondary.value, 's||second', '保存失败不把未保存选择当成成功');
  fail = false;
  nodes.fallbackSecondary.value = nodes.fallbackTertiary.value = '';
  await context.saveMainFallbacks();
  assert.deepEqual(calls[2].body, {secondary:{providerId:'',model:''},tertiary:{providerId:'',model:''}});
  assert.match(nodes.mainFallbackStatus.textContent, /尚未启用/);
  console.log('Main fallback UI checks passed: optional choices, exclusions, save, reload, failure and clear.');
})().catch(error => { console.error(error); process.exitCode = 1; });
