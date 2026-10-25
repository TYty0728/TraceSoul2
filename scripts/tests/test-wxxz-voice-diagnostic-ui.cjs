const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');
const html=fs.readFileSync(path.join(__dirname,'../../Tools/Host/wwwroot/index.html'),'utf8');
const nodes=new Map();
const $=id=>{if(!nodes.has(id))nodes.set(id,{value:'',textContent:'',disabled:false,checked:false,dataset:{}});return nodes.get(id);};
const writes=[];
let pending, fail=false;
const sandbox={$,sending:false,setInterval:()=>1,clearInterval(){},window:{addEventListener(){}},
  api:async(url,method,body)=>{
    writes.push({url,method,body});
    if(pending)await pending;
    if(fail)throw new Error('连接失败');
    return {sessionId:'test-session',voiceprint:{enrolled:true,samples:3,threshold:body.threshold,
      diagnosticBypass:body.threshold===0,diagnosticUntilUnixMs:Date.now()+900000},
      monitor:{active:false,state:'stopped',turns:0}};
  }};
vm.createContext(sandbox);
vm.runInContext(html.slice(html.indexOf('  let wxxzSession ='),html.indexOf('  /* ---------- 最近一轮渲染')),sandbox);
(async()=>{
  assert.match(html,/id="wxxzDirectStart" onclick="startWxxzCall\(false\)"/);
  let release;pending=new Promise(resolve=>release=resolve);
  const enabling=sandbox.configureWxxzVoiceDiagnostic(0);
  assert.equal($('wxxzVoiceBypass').disabled,true);
  await sandbox.configureWxxzVoiceDiagnostic(0);
  assert.equal(writes.length,1);
  release();await enabling;pending=null;
  assert.deepEqual(JSON.parse(JSON.stringify(writes[0].body)),{threshold:0,durationSeconds:900});
  assert.equal(writes[0].url,'/wxxz/voice/diagnostic');
  assert.equal(writes[0].method,'PUT');
  assert.match($('wxxzVoiceDiagnosticState').textContent,/阈值0.*所有声音.*恢复0.65/);
  await sandbox.configureWxxzVoiceDiagnostic(0.65);
  assert.match($('wxxzVoiceDiagnosticState').textContent,/核验已开启.*0.65/);
  vm.runInContext('wxxzMonitorActive=true',sandbox);
  await sandbox.configureWxxzVoiceDiagnostic(0);
  assert.equal(writes.length,2,'监听期间不更改准入规则');
  vm.runInContext('wxxzMonitorActive=false',sandbox);
  fail=true;await sandbox.configureWxxzVoiceDiagnostic(0);
  assert.match($('wxxzVoiceDiagnosticState').textContent,/设置失败/);
  assert.equal($('wxxzVoiceBypass').disabled,false);
  assert.ok(writes.every(x=>x.url==='/wxxz/voice/diagnostic'),'只更改调试门槛，不隐式采音或提交对话');
  console.log('Voice diagnostic UI passed: explicit lease, restore, busy, errors and no implicit recording.');
})().catch(e=>{console.error(e);process.exit(1);});
