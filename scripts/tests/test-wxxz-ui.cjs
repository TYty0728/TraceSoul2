const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const html = fs.readFileSync(path.join(__dirname, '../../Tools/Host/wwwroot/index.html'), 'utf8');
const nodes = new Map();
const $ = id => { if (!nodes.has(id)) nodes.set(id, { value:'', textContent:'', checked:false, disabled:false, dataset:{},children:[],appendChild(child){this.children.push(child);} }); return nodes.get(id); };
const writes = [];
let fail = false;
let callListens=0;
let monitorActive=false;
let tick=null;
const countdownStates=[];
let recordingState='';
let enrollResult={accepted:false, reason:'有效语音不足4秒', quality:{speech_ms:2546,full_text:'本段过短的听写'},voiceprint:{enrolled:false,samples:0}};
let enrollmentRecordingState='';
const sandbox = { $, sending:false, crypto:{randomUUID:()=> 'test-request'},
  setInterval:fn=>{tick=fn;return 1;},clearInterval(){},setTimeout:fn=>{countdownStates.push($('wxxzCheckState').textContent);fn();return 1;},
  window:{addEventListener(){}},
  document:{createElement:()=>({style:{}})},
  api:async (url, method, body) => {
    writes.push({url,method,body});
    if (fail && url === '/wxxz/moments') throw new Error('unknown result');
    if (url === '/wxxz/session') return {sessionId:method==='DELETE' ? null : 'session-a',privateSession:body?.privateSession};
    if (url === '/wxxz/monitor/start') {monitorActive=true;return {sessionId:'session-a',monitor:{id:'monitor-a',active:true,state:'running',phase:'listening',callId:'call-a',turns:0}};}
    if (url === '/wxxz/monitor/stop') {monitorActive=false;return {sessionId:'session-a',monitor:{id:'monitor-a',active:false,state:'stopped',phase:'stopped',turns:0}};}
    if (url === '/wxxz') return {delivery:{audio:'failed_or_unknown'}};
    if (url === '/wxxz/listen') return {text:'实际听写测试',captureId:'capture-a'};
    if (url === '/wxxz/speech') return {sessionId:'session-a',voice:'远端角色语音',speech:{...body,description:'远端角色语音',remoteConfigured:true}};
    if (url === '/wxxz/speech/test') return {voice:'远端角色语音'};
    if (url === '/wxxz/voice/enroll') {await tick();enrollmentRecordingState=$('wxxzEnrollState').textContent;return enrollResult;}
    if (url === '/wxxz/voice/check/state') return {phase:'recording',recorded_ms:640};
    if (url === '/wxxz/voice/check') {
      await tick(); recordingState=$('wxxzCheckState').textContent;
      return {quality:{capture_id:1234,recorded_ms:8000,speech_ms:6000,volume_500ms:[20,100],segments:[{start_ms:2000,duration_ms:6000}],full_text:'本地测试语音'},diagnostic:{wholeScore:0.85,score:0.85,minimumWindowScore:0.5757,decisionBasis:'whole_utterance',threshold:0.65,matched:true,windowScores:[0.85,0.5757],windowMs:[2000,2000]}};
    }
    if (url === '/wxxz/call/start') return {callId:'call-a'};
    if (url === '/wxxz/call/listen') {
      assert.equal($('wxxzSpeechSave').disabled,true,'通话中禁止切换音色');
      assert.equal($('wxxzSpeechTest').disabled,true,'通话中禁止额外试听');
      callListens++;
      if (callListens===1) return {status:'speaker_unconfirmed',text:''};
      if (callListens===4) await sandbox.stopWxxzCall();
      return {status:'verified',text:'连续通话测试',captureId:'call-capture'};
    }
    return {reply:'test', wxxz:{delivery:{display:'completed',audio:'completed'}}};
  }, logLine(){}, renderTurn(){}, refreshTranscript:async()=>{} };
vm.createContext(sandbox);
vm.runInContext(html.slice(html.indexOf('  let wxxzSession ='), html.indexOf('  /* ---------- 最近一轮渲染')),sandbox);
(async()=>{
  $('momentSource').value='wxxz'; $('content').value='synthetic';
  await sandbox.sendMoment();
  assert.equal(writes.length,0,'未连接不产生 Moment/推理');
  $('wxxzPrivate').checked=true;
  await sandbox.connectWxxz();
  assert.equal(writes.find(x=>x.url==='/wxxz/session').body.privateSession,true);
  $('wxxzSpeechEngine').value='remote'; $('wxxzSpeechLocalVoice').value='character';
  $('wxxzSpeechRemoteModel').value='voice-2-hd'; $('wxxzSpeechRemoteVoice').value='test-voice';
  await sandbox.saveWxxzSpeech();
  assert.equal(writes.find(x=>x.url==='/wxxz/speech').body.engine,'remote');
  assert.match($('wxxzState').textContent,/远端角色语音/,'当前语音显示真实选择，不再固定系统测试音色');
  await sandbox.testWxxzSpeech();
  assert.match($('wxxzSpeechState').textContent,/试听播放完成/);
  assert.equal(writes.find(x=>x.url==='/wxxz/speech/test').body.sessionId,'session-a');
  assert.equal(writes.some(x=>x.url==='/wxxz/moments'),false,'音色试听不触发对话');
  await sandbox.enrollWxxzVoice();
  assert.match($('wxxzEnrollState').textContent,/2.55秒/,'失败的有效语音时长显示在录入按钮旁');
  assert.match($('wxxzEnroll').textContent,/第1\/3段/,'失败不前进');
  assert.match($('wxxzEnroll').textContent,/8秒/,'声纹录入显示8秒，手动听写入口独立');
  assert.match($('wxxzEnrollTranscript').textContent,/本段过短的听写/,'未接受的录音也显示本次听写');
  assert.match(enrollmentRecordingState,/正在录音.*0.6\/8秒/,'录入进度也来自实际采音');
  const savedSlots=[1,2,3].map(number=>({number,accepted:true,speechMs:6000,text:'旧段'+number}));
  sandbox.renderWxxzVoice({enrolled:true,samples:3,slots:savedSlots});
  assert.equal($('wxxzSampleText2').textContent,'旧段2');
  enrollResult={accepted:false,reason:'声音不一致',quality:{speech_ms:6000,full_text:'失败的新听写'},voiceprint:{enrolled:true,samples:3,slots:savedSlots}};
  await sandbox.enrollWxxzVoice(2);
  const replaceRequest=writes.filter(x=>x.url==='/wxxz/voice/enroll').at(-1);
  assert.equal(replaceRequest.body.sampleNumber,2,'指定单段重录');
  assert.equal(replaceRequest.body.restart,false,'单段重录不能清空三段');
  assert.equal($('wxxzSampleText2').textContent,'旧段2','失败保留原段听写');
  assert.match($('wxxzEnrollTranscript').textContent,/失败的新听写/);
  enrollResult={accepted:true,quality:{speech_ms:8000,full_text:'新的第二段'},voiceprint:{enrolled:true,samples:3,slots:savedSlots.map(s=>s.number===2?{...s,text:'新的第二段',speechMs:8000}:s)}};
  await sandbox.enrollWxxzVoice(2);
  assert.equal($('wxxzSampleText1').textContent,'旧段1');
  assert.equal($('wxxzSampleText2').textContent,'新的第二段');
  assert.equal($('wxxzSampleText3').textContent,'旧段3');
  assert.match($('wxxzSampleRecord2').textContent,/重录第2段/);
  assert.equal(writes.some(x=>x.url==='/wxxz/moments'),false,'声纹听写与重录不发送对话');
  countdownStates.length=0;
  const beforeCheck=writes.length;
  await sandbox.checkWxxzVoice();
  assert.match($('wxxzCheckResult').textContent,/0.8500/);
  assert.match($('wxxzCheckResult').textContent,/0.5757/);
  assert.match($('wxxzCheckResult').textContent,/实际采音：8.00秒/);
  assert.match($('wxxzCheckResult').textContent,/本地测试语音/);
  assert.match($('wxxzCheckResult').textContent,/重叠检查窗/,'区分检查窗与三段录入样本');
  assert.match($('wxxzCheckResult').textContent,/按现有规则：通过/,'短窗低于门槛时整句匹配仍显示通过');
  assert.match($('wxxzCheckResult').textContent,/判定依据：完整一句/);
  assert.match($('wxxzCheckResult').textContent,/仅诊断，不单独否决/);
  assert.equal($('wxxzCheckVolume').children.length,2);
  assert.equal($('wxxzCheckProgress').value,8);
  assert.ok(writes.slice(beforeCheck).every(x=>x.url.startsWith('/wxxz/voice/check')),'诊断不调用录入、Moment或通话入口');
  assert.deepEqual(countdownStates.map(x=>x.match(/(\d)秒/)[1]),['3','2','1']);
  assert.match(recordingState,/正在录音.*0.6\/8秒/,'录音提示和进度来自实际采音回执');
  $('content').value='';
  await sandbox.listenWxxz();
  assert.equal($('content').value,'实际听写测试');
  assert.equal(writes.some(x=>x.url==='/wxxz/moments'),false,'录音听写不自动提交');
  await sandbox.sendMoment();
  const call=writes.find(x=>x.url==='/wxxz/moments');
  assert.equal(call.body.sessionId,'session-a');
  assert.equal(call.body.requestId,'test-request');
  assert.equal(call.body.captureId,'capture-a');
  assert.equal(writes.some(x=>x.url==='/moments'),false,'WXXZ 不退到控制台入口');
  const beforeCall=writes.filter(x=>x.url==='/wxxz/moments').length;
  await sandbox.startWxxzCall();
  assert.equal(monitorActive,true);
  assert.equal(callListens,0,'网页不再逐句驱动采音');
  assert.equal($('wxxzSpeechSave').disabled,true);
  assert.equal(writes.filter(x=>x.url==='/wxxz/moments').length-beforeCall,0,'语音Moment由后台提交，网页不重复提交');
  await sandbox.stopWxxzCall();
  assert.equal($('wxxzCallStop').disabled,true);
  assert.equal($('wxxzListen').disabled,false);
  fail=true; $('content').value='synthetic failure';
  await sandbox.sendMoment();
  assert.match($('wxxzState').textContent,/失败或结果未知/,'失败仍可查看实际交付状态');
  await sandbox.disconnectWxxz();
  const count=writes.length;
  await sandbox.sendMoment();
  assert.equal(writes.length,count,'关闭后不再提交');
  fail=false; $('momentSource').value='console';
  await sandbox.sendMoment();
  assert.ok(writes.some(x=>x.url==='/moments'));
  console.log('WXXZ UI checks passed: explicit connection, host-owned start/stop, no browser pumping, receipts and console compatibility.');
})().catch(e=>{console.error(e);process.exitCode=1;});
