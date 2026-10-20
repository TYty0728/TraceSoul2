const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const html=fs.readFileSync(path.join(__dirname,'../../Tools/Host/wwwroot/index.html'),'utf8');
const makeNode=()=>({value:'',textContent:'',checked:false,disabled:false,dataset:{},style:{},children:[],
  appendChild(child){this.children.push(child);}});
const nodes=new Map(),$=id=>{if(!nodes.has(id))nodes.set(id,makeNode());return nodes.get(id);};
const events={},writes=[];
let state={sessionId:'session',wake:{enabled:true,name:'小汐',phonetic:true,pronunciation:'xiao xi',aliases:[],reply:'我在。'},
  voiceprint:{enrolled:true,samples:3},attention:{state:'standby'},monitor:{id:'server-monitor',active:true,phase:'listening',callId:'call',turns:0},
  diagnostics:[{sequence:1,code:'standby',reason:'未叫名字',rawText:'<img src=x onerror=alert(1)>',text:'未准入',voiceConfirmed:false,admitted:false}]};
let failRead=false,failStop=false,refreshes=0;
const sandbox={$,sending:false,crypto:{randomUUID:()=> 'request'},setInterval:()=>1,clearInterval(){},setTimeout,
  window:{addEventListener:(key,fn)=>{events[key]=fn;}},document:{createElement:makeNode},
  logLine(){},renderTurn(){},refreshTranscript:async()=>{refreshes++;},
  api:async(url,method,body)=>{
    writes.push({url,method,body});
    if(url==='/wxxz'){if(failRead)throw new Error('offline');return state;}
    if(url==='/wxxz/device/control'){
      state={...state,deviceControl:{enabled:body.enabled,online:false,error:null}};return state;
    }
    if(url==='/wxxz/monitor/start')return state;
    if(url==='/wxxz/monitor/stop'){
      if(failStop)throw new Error('offline');
      assert.equal(body.monitorId,'server-monitor');
      state={...state,monitor:{...state.monitor,phase:'stopping'}};return state;
    }
    if(url==='/wxxz/attention/preview')return {simulated:true,note:'未采音、不写入Moment',correction:{rawText:body.text,text:'小汐',pronunciation:'xiao xi'},
      decision:{reason:'名字命中，仅简短应答'},wouldAcknowledge:true,wouldCreateMoment:false,nextState:'focused'};
    return {};
  }};
vm.createContext(sandbox);
vm.runInContext(html.slice(html.indexOf('  let wxxzSession ='),html.indexOf('  /* ---------- 最近一轮渲染')),sandbox);
(async()=>{
  $('momentSource').value='wxxz';
  await sandbox.pollWxxzMonitor();
  assert.equal(writes.filter(x=>x.method==='POST').length,0,'重开页面只观察，不重复开始');
  assert.equal($('wxxzWakeName').value,'小汐','正在监听也显示当前名字');
  assert.equal($('wxxzWakeName').disabled,true);
  assert.equal($('content').disabled,true);
  $('momentSource').value='console';sandbox.applyWxxzMonitorLocks();
  assert.equal($('content').disabled,false,'后台监听不阻止控制台文字输入');
  assert.equal($('momentSource').disabled,false);
  const row=$('wxxzDiagnostics').children[0];
  assert.match(row.children[1].textContent,/<img src=x onerror=alert\(1\)>/,'原文作为文字显示');
  assert.equal(row.children[1].innerHTML,undefined,'诊断不拼接用户HTML');
  await sandbox.pollWxxzMonitor();
  assert.equal($('wxxzDiagnostics').children[0],row,'无新诊断不重建详情，保留展开状态');
  $('wxxzWakeName').value='未保存的名字';vm.runInContext('wxxzWakeDirty=true',sandbox);
  await sandbox.pollWxxzMonitor();
  assert.equal($('wxxzWakeName').value,'未保存的名字','轮询不覆盖编辑草稿');
  events.pagehide();
  assert.equal(writes.some(x=>x.url==='/wxxz/monitor/stop'),false,'关闭页面不停止后台');
  const before=writes.length;
  $('wxxzSandboxText').value='小希';$('wxxzSandboxSpeaker').value='short';
  await sandbox.previewWxxzAttention();
  assert.equal(writes.length,before+1);assert.equal(writes.at(-1).url,'/wxxz/attention/preview');
  assert.match($('wxxzSandboxResult').textContent,/非实际采音/);
  assert.match($('wxxzSandboxResult').textContent,/会进入Moment：否/);
  state={...state,monitor:{...state.monitor,turns:1}};
  await sandbox.pollWxxzMonitor();assert.equal(refreshes,1,'后台完成一轮后刷新真实经历');
  failStop=true;await sandbox.stopWxxzCall();
  assert.match($('wxxzCallState').textContent,/后台可能仍在监听/,'断线不伪造停止成功');
  assert.equal($('wxxzCallStop').disabled,false,'停止结果未知时仍可再次请求');
  failStop=false;await sandbox.stopWxxzCall();
  assert.match($('wxxzMonitorState').textContent,/收尾/);
  assert.equal($('wxxzWakeSave').disabled,true,'收尾期间不允许新采音或改设置');
  state={...state,attention:{state:'stopped'},monitor:{...state.monitor,active:false,phase:'stopped'}};
  await sandbox.pollWxxzMonitor();assert.equal($('wxxzWakeSave').disabled,false);
  assert.equal($('wxxzCallStop').disabled,true);
  failRead=true;await sandbox.pollWxxzMonitor();
  assert.match($('wxxzMonitorState').textContent,/断线不等于监听已停止/);
  assert.equal(writes.some(x=>['/wxxz/call/listen','/wxxz/moments'].includes(x.url)),false,'网页既不采音也不代提交语音');
  const startsBefore=writes.filter(x=>x.url==='/wxxz/monitor/start').length;
  await sandbox.setWxxzDeviceControl(true);
  assert.equal(writes.at(-1).url,'/wxxz/device/control');
  assert.equal(writes.at(-1).method,'PUT');
  assert.equal($('wxxzDeviceAllowed').checked,true);
  assert.match($('wxxzDeviceState').textContent,/等待开发板/);
  assert.equal(writes.filter(x=>x.url==='/wxxz/monitor/start').length,startsBefore,'触屏授权本身不开始采音');
  await sandbox.setWxxzDeviceControl(false);
  assert.equal($('wxxzDeviceAllowed').checked,false);
  assert.match($('wxxzDeviceState').textContent,/尚未授权/);
  console.log('WXXZ monitor UI checks passed: read-only reattach, persistent listening, explicit stop/unknown outcome, sandbox, escaped diagnostics and draft preservation.');
})().catch(e=>{console.error(e);process.exit(1);});
