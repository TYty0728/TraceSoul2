const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');
const html=fs.readFileSync(path.join(__dirname,'../../Tools/Host/wwwroot/index.html'),'utf8');
const nodes=new Map();
const $=id=>{if(!nodes.has(id))nodes.set(id,{value:'',textContent:'',checked:false,disabled:false,dataset:{},children:[],appendChild(x){this.children.push(x);}});return nodes.get(id);};
const writes=[];
let enrolled=false,listens=0,active=false;
let wake={enabled:true,name:'小汐',phonetic:true,pronunciation:'xiao xi',aliases:[],reply:'诶，我在。'};
const sandbox={$,sending:false,crypto:{randomUUID:()=> 'wake-test-request'},
  setInterval:()=>1,clearInterval(){},setTimeout,window:{addEventListener(){}},
  document:{createElement:()=>({style:{}})},logLine(){},renderTurn(){},refreshTranscript:async()=>{},
  api:async(url,method,body)=>{
    writes.push({url,method,body});
    const status={sessionId:'wake-session',wake,voiceprint:{enrolled,samples:enrolled?3:0},attention:{state:active?'standby':'stopped'},
      monitor:{id:'wake-monitor',active,phase:active?'listening':'stopped',callId:active?'wake-call':null,turns:0}};
    if(url==='/wxxz'||url==='/wxxz/session')return status;
    if(url==='/wxxz/wake'){wake=body;return {...status,wake};}
    if(url==='/wxxz/wake/preview')return {rawText:body.text,text:'小汐你好',mentioned:true,pronunciation:'xiao xi',matchMode:'phonetic'};
    if(url==='/wxxz/monitor/start'){assert.equal(body.wake,true);active=true;return {...status,monitor:{...status.monitor,active:true,callId:'wake-call',phase:'listening'}};}
    if(url==='/wxxz/monitor/stop'){active=false;return {...status,attention:{state:'stopped'},monitor:{...status.monitor,active:false,callId:null,phase:'stopped'}};}
    if(url==='/wxxz/call/start'){assert.equal(body.wake,true);return {callId:'wake-call',attention:{state:'standby'}};}
    if(url==='/wxxz/call/listen'){
      assert.equal($('wxxzWakeSave').disabled,true,'监听期间禁止改配置');
      assert.equal($('wxxzWakeStop').disabled,false,'设置面板可以直接停止');
      listens++;
      if(listens===1)return {status:'standby',text:'',attention:{state:'standby'}};
      if(listens===2)return {status:'wake_ack',text:'',attention:{state:'focused',focusRemainingMs:30000}};
      if(listens===3)return {status:'verified',text:'小汐帮我看看',rawText:'小溪帮我看看',corrected:true,captureId:'verified-owner',score:0.85};
      if(listens===4)return {status:'speaker_unconfirmed',text:'',score:0.1};
      await sandbox.stopWxxzCall();
      return {status:'verified',text:'迟到的语音',captureId:'late'};
    }
    if(url==='/wxxz/moments')return {wxxz:{...status,attention:{state:'focused',focusRemainingMs:30000},delivery:{audio:'completed',display:'completed'}}};
    return {};
  }};
vm.createContext(sandbox);
vm.runInContext(html.slice(html.indexOf('  let wxxzSession ='),html.indexOf('  /* ---------- 最近一轮渲染')),sandbox);
(async()=>{
  sandbox.renderWxxzStatus({wake});
  assert.equal($('wxxzWakeName').value,'小汐');assert.equal($('wxxzWakeAliases').value,'');
  assert.equal($('wxxzWakePhonetic').checked,true);assert.equal($('wxxzWakePronunciation').value,'xiao xi');
  $('wxxzWakeAliases').value='小溪，小希、 小西';
  await sandbox.saveWxxzWake();
  assert.deepEqual(Array.from(writes.find(x=>x.url==='/wxxz/wake').body.aliases),['小溪','小希','小西']);
  assert.equal(writes.find(x=>x.url==='/wxxz/wake').body.pronunciation,'xiao xi');
  assert.equal(writes.find(x=>x.url==='/wxxz/wake').body.phonetic,true);
  $('wxxzWakePreviewText').value='小溪你好'; await sandbox.previewWxxzWake();
  assert.match($('wxxzWakeResult').textContent,/原文：小溪你好 → 校正：小汐你好/);
  assert.match($('wxxzWakeResult').textContent,/读音：xiao xi；按读音匹配/);
  assert.ok(writes.every(x=>!['/wxxz/listen','/wxxz/call/start','/wxxz/moments'].includes(x.url)),'设置和预览不采音、不对话');
  await sandbox.connectWxxz();
  assert.equal(writes.some(x=>x.url==='/wxxz/monitor/start'),false,'未录声纹时不会自动采音');
  enrolled=true;
  await sandbox.connectWxxz(); await new Promise(resolve=>setImmediate(resolve));
  assert.equal(listens,0,'浏览器不逐句采音');
  const moments=writes.filter(x=>x.url==='/wxxz/moments');
  assert.equal(moments.length,0,'浏览器不重复提交后台Moment');
  await sandbox.stopWxxzCall();
  assert.equal($('wxxzWakeSave').disabled,false);
  assert.equal($('wxxzWakeStop').disabled,true);
  assert.match($('wxxzAttentionState').textContent,/暂停/);
  console.log('WXXZ wake UI checks passed: settings/preview, explicit connect, short wake, owner-only admission, stop/late result.');
})().catch(e=>{console.error(e);process.exit(1);});
