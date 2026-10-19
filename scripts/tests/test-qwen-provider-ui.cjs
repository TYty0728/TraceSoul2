const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const html = fs.readFileSync(path.join(__dirname, '../../Tools/Host/wwwroot/index.html'), 'utf8');
const nodes = new Map();
const node = id => {
  if (!nodes.has(id)) nodes.set(id, {value:'',textContent:'',innerHTML:'',checked:false,hidden:false,pause(){},removeAttribute(){}});
  return nodes.get(id);
};
const writes = [];
let fail = false;
const sandbox = { $: node, esc: String, logLine(){}, invalidateDailyPlan(){},
  alert(message){throw Error(message);}, URL:{createObjectURL:()=> 'blob:preview',revokeObjectURL(){}},
  api:async (url,method,body)=>{writes.push({url,method,body});},
  fetch:async (url,options)=>{
    writes.push({url,method:options.method,body:JSON.parse(options.body)});
    return {ok:!fail,json:async()=>({error:'鉴权失败'}),blob:async()=>({type:'audio/wav'})};
  }
};
vm.createContext(sandbox);
vm.runInContext(html.slice(html.indexOf('  const ROLE_LABELS ='),html.indexOf('  function historyWindowMin(')),sandbox);
vm.runInContext(`refreshProviders=async()=>{};renderRoleFilter=()=>{};
providerCache=[{id:'qwen',type:'qwen_tts',displayName:'千问',baseUrl:'http://127.0.0.1:5088/v1',
  voice:'character',model:'qwen3-tts-local',hasApiKey:true,models:[{id:'qwen3-tts-local',enabled:true,roles:['speech']}]}];
selectedProviderId='qwen';`,sandbox);
sandbox.fillProviderForm(vm.runInContext('providerCache[0]',sandbox));
assert.equal(node('ttsOptions').hidden,false);
assert.equal(node('decisionOptions').hidden,true);
assert.equal(node('thinking').disabled,true);
assert.equal(node('proxy').disabled,true);
assert.equal(node('apiKey').value,'');
assert.equal(node('ttsVoice').value,'character');
assert.equal(node('ttsWorkbench').href,'/providers/qwen/speech/workbench');
assert.match(node('modelBox').innerHTML,/用于语音/);
assert.doesNotMatch(node('modelBox').innerHTML,/设为主模型|useModel\(/);
(async()=>{
  node('ttsVoice').value='demo';
  await sandbox.saveProvider();
  assert.equal(writes[0].body.voice,'demo');
  assert.equal(writes[0].body.apiKey,'','空Key保留服务端已保存值');
  assert.equal(writes.length,1,'保存不隐式生成或切换槽');
  node('ttsPreviewText').value='本地试听。';
  await sandbox.previewTts();
  assert.equal(writes.at(-1).url,'/providers/qwen/speech-test');
  assert.deepEqual(writes.at(-1).body,{text:'本地试听。'});
  assert.equal(node('ttsPreviewAudio').src,'blob:preview');
  assert.equal(node('ttsPreviewAudio').hidden,false);
  assert.ok(writes.every(x=> !/moments|wxxz|current/.test(x.url)),'试听不提交Moment、不播放板子、不改主模型');
  await sandbox.useSpeechModel('qwen3-tts-local');
  assert.deepEqual(JSON.parse(JSON.stringify(writes.at(-1).body)),{speech:{providerId:'qwen',model:'qwen3-tts-local'}});
  fail=true;
  await sandbox.previewTts();
  assert.match(node('ttsPreviewState').textContent,/鉴权失败/);
  assert.equal(node('ttsPreviewButton').disabled,false);
  console.log('Qwen supplier UI checks passed: configuration, speech-only choice, workbench, browser preview and failures.');
})().catch(e=>{console.error(e);process.exitCode=1;});
