const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const html = fs.readFileSync(path.join(__dirname, '../../Tools/Host/wwwroot/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
new vm.Script(script);
const functions = script.slice(script.indexOf('  function classify('), script.indexOf('  function esc('));
const rows = [];
const log = {children: rows, prepend(row) { rows.unshift(row); },
  removeChild(row) { rows.splice(rows.indexOf(row), 1); },
  get lastChild() { return rows.at(-1); }, get childElementCount() { return rows.length; }};
const elements = {logLevel: {value: 'normal'}, logSearch: {value: ''}, logCount: {textContent: ''}};
const context = vm.createContext({logEl: log, $: id => elements[id],
  document: {createElement: () => ({dataset: {}, textContent: '', hidden: false})}});
vm.runInContext(functions, context);
context.addLog('2026-11-03 10:00:00.100 +08:00 [调试] [轮次 abc] 上下文已读取');
context.addLog('2026-11-03 10:00:00.200 +08:00 [错误] [轮次 abc] 记忆神经失败');
context.addLog('2026-11-03 10:00:00.300 +08:00 [信息] [轮次 def] 模型请求完成');
assert.equal(rows.filter(r => !r.hidden).length, 2);
assert.equal(rows[1].className, 'logline error'); // 错误不能被“记忆”配色盖住
elements.logLevel.value = 'issues'; context.filterLogs();
assert.equal(rows.filter(r => !r.hidden).length, 1);
elements.logLevel.value = 'all'; elements.logSearch.value = 'ABC'; context.filterLogs();
assert.equal(rows.filter(r => !r.hidden).length, 2);
elements.logSearch.value = '不存在'; context.filterLogs();
assert.equal(rows.filter(r => !r.hidden).length, 0);
assert.equal(rows.length, 3); // 筛选不删历史
elements.logSearch.value = ''; context.filterLogs();
context.addLog('<img src=x onerror=alert(1)>');
assert.equal(rows[0].textContent, '<img src=x onerror=alert(1)>');
assert.equal(rows[0].innerHTML, undefined);
for (let i = 0; i < 350; i++) context.addLog('普通日志 ' + i);
assert.equal(rows.length, 300);
assert.equal(rows[0].textContent, '普通日志 349');
assert.equal(context.logLevel('10:00:00 旧插件执行失败'), '警告');
assert.equal(context.logLevel('10:00:00 [信息] 上次失败现已恢复'), '信息');
console.log('Log UI checks passed: syntax, levels, filters, trace search, retained history and text rendering.');
