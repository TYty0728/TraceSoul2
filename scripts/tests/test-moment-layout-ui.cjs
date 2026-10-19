const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const html = fs.readFileSync(path.join(__dirname, '../../Tools/Host/wwwroot/index.html'), 'utf8');
const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(x=>x[1]);
assert.equal(ids.length,new Set(ids).size,'控件ID不能因搬移而重复');
const section = name => html.slice(html.indexOf('<section id="sec-'+name+'"'),html.indexOf('</section>',html.indexOf('<section id="sec-'+name+'"')));
for (const id of ['wxxzSampleRecord1','wxxzVoiceCheck','wxxzSpeechEngine','wxxzConnect']) {
  assert.ok(section('platform').includes('id="'+id+'"'),'设备设置归入身体与感官');
  assert.ok(!section('chat').includes('id="'+id+'"'),'对话页不再堆放设备设置');
}
for (const id of ['wxxzCallStart','wxxzCallStop','content','transcript','momentTypeFilter','momentPlatformFilter'])
  assert.ok(section('chat').includes('id="'+id+'"'),'保留对话和经历操作');
assert.ok(html.indexOf('<h2>上下文与运行节奏</h2>')>html.indexOf('id="modelBox"'),'运行设置独立于供应商编辑区');
const nodes = new Map();
const node=id=>{
  if(!nodes.has(id))nodes.set(id,{value:'',innerHTML:'',children:[],scrollHeight:10,append(child){this.children.push(child);}});
  return nodes.get(id);
};
const reads=[];
const sandbox={ $:node, currentUsername:'本人',esc:x=>String(x).replaceAll('<','&lt;').replaceAll('>','&gt;'),
  document:{createElement:()=>({})},renderTurn(){},ensureWxxzMonitorPolling(){},pollWxxzMonitor:async()=>{},
  api:async url=>{
    reads.push(url);
    if(url==='/moments/catalog')return {types:[{id:'hearing',label:'我听见'}],platforms:[{id:'wxxz',label:'小智开发板'}]};
    if(url==='/turns/last')return {};
    return [{role:'本人',type:'hearing',typeLabel:'我听见',sourcePlatform:'wxxz',platformLabel:'小智开发板',content:'<原始语音>',createdUnixMs:1}];
  }
};
vm.createContext(sandbox);
vm.runInContext(html.slice(html.indexOf('  async function loadChat()'),html.indexOf('  let wxxzSession =')),sandbox);
(async()=>{
  await sandbox.loadChat();
  assert.match(node('momentTypeFilter').innerHTML,/我听见/);
  assert.match(node('momentPlatformFilter').innerHTML,/小智开发板/);
  node('momentTypeFilter').value='hearing'; node('momentPlatformFilter').value='wxxz';
  await sandbox.refreshTranscript();
  assert.match(reads.at(-1),/source=all&type=hearing&platform=wxxz/);
  const row=node('transcript').children.at(-1).innerHTML;
  assert.match(row,/我听见/);assert.match(row,/小智开发板/);assert.match(row,/&lt;原始语音&gt;/);
  assert.doesNotMatch(row,/<原始语音>/,'原文不能作为HTML执行');
  console.log('Moment layout UI checks passed: controls relocated, IDs unique, independent filters and escaped timeline.');
})().catch(e=>{console.error(e);process.exitCode=1;});
