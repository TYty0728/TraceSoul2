const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const html=fs.readFileSync(path.join(__dirname,'../../Tools/Host/wwwroot/index.html'),'utf8');
const nodes=new Map(),writes=[];
const $=id=>{if(!nodes.has(id))nodes.set(id,{value:'',textContent:'',disabled:false,checked:false,dataset:{}});return nodes.get(id);};
let release,rendered=0;
const sandbox={$,sending:false,setInterval:()=>1,clearInterval(){},window:{addEventListener(){}},crypto:{randomUUID:()=> 'a'.repeat(32)},
  renderTurn:()=>rendered++,refreshTranscript:async()=>{},
  api:async(url,method,body)=>{writes.push({url,body});
    if(url.endsWith('/start'))return {sessionId:'s',manualRecording:true,monitor:{active:false,state:'stopped',turns:0}};
    await new Promise(r=>release=r);return {text:'完整测试',recordedMs:8000,turn:{}};}};
vm.createContext(sandbox);
vm.runInContext(html.slice(html.indexOf('  let wxxzSession ='),html.indexOf('  /* ---------- 最近一轮渲染')),sandbox);
(async()=>{
  vm.runInContext("wxxzSession='s'",sandbox);
  await sandbox.startWxxzManual();
  assert.match($('wxxzManualState').textContent,/正在录音/);
  assert.equal($('wxxzManualStop').disabled,false);
  await sandbox.startWxxzManual();assert.equal(writes.length,1);
  const finishing=sandbox.stopWxxzManual(false);
  await sandbox.stopWxxzManual(false);assert.equal(writes.length,2);
  assert.equal(writes[1].url,'/wxxz/manual/stop');
  assert.equal($('wxxzManualStop').disabled,true);
  release();await finishing;
  assert.equal(rendered,1);assert.match($('wxxzManualState').textContent,/完整录音8.0秒/);
  assert.equal($('wxxzManualStart').disabled,false);
  assert.equal($('wxxzManualStop').disabled,true);
  console.log('Manual recording UI passed: explicit start/stop, busy, duplicate rejection and complete result.');
})().catch(e=>{console.error(e);process.exit(1);});
