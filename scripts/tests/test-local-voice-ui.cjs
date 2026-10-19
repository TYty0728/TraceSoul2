const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const html = fs.readFileSync(path.join(__dirname, '../../Tools/LocalVoice/index.html'), 'utf8');
const nodes = new Map(), writes = [], saved = [];
function node() { return {value:'',textContent:'',disabled:false,dataset:{},children:[],
  append(...children){this.children.push(...children)},replaceChildren(){this.children=[]},scrollIntoView(){},focus(){}}; }
const element = id => {if(!nodes.has(id))nodes.set(id,node());return nodes.get(id)};
let active = null, failAt = 0, stopAt = 0;
const buttons = () => element('labCards').children.flatMap(card => card.children).filter(n=>n.dataset.activate);
const sandbox = {document:{getElementById:element,createElement:node,querySelectorAll:buttons},
  setInterval:()=>1, clearInterval(){}, performance:{now:()=>0},
  URL:{createObjectURL:()=> 'blob:local-preview',revokeObjectURL(){}},
  fetch:async (url,options={})=>{
    const body=options.body?JSON.parse(options.body):undefined;
    writes.push({url,method:options.method||'GET',body});
    let data;
    if(url==='/health')data={ready:true,phase:'idle',elapsed_ms:0,voices:[{id:'character',ready:true},{id:'source_remote',ready:true}]};
    else if(url==='/lab/cards'&&options.method==='POST'){
      assert.equal(element('labGenerate').disabled,true);
      assert.equal(element('labStop').disabled,false);
      if(failAt&&saved.length+1===failAt)return {ok:false,json:async()=>({detail:'generation_time_limit'})};
      data={...body,id:String(saved.length+1).padStart(32,'a'),timing:{synthesis_ms:1000,audio_ms:4000,rtf:.25},audio_url:'/safe-audio'};
      saved.push(data);
      if(saved.length===stopAt)element('labStop').onclick();
    }else if(url==='/lab/cards')data={active_card:active,cards:saved.map(card=>({...card,active:card.id===active}))};
    else if(/\/preview$/.test(url))return {ok:true,blob:async()=>Buffer.from('RIFF'),headers:{get:name=>name==='X-Audio-Ms'?'4000':'.25'}};
    else if(url==='/lab/activate'){active=body.id;data={active_card:active}}
    else if(url==='/lab/restore'){active=null;data={active_card:null}}
    else throw Error('Unexpected URL '+url);
    return {ok:true,json:async()=>data};
  }};
vm.createContext(sandbox);
vm.runInContext(html.slice(html.indexOf('<script>')+8,html.indexOf('</script>')),sandbox);
const drain=()=>new Promise(resolve=>setImmediate(resolve));
function inputs(){element('labCount').value='2';element('labText').value='统一试听文字';element('labSeed').value='17';element('labTemperature').value='.8';element('labTopP').value='.95'}
(async()=>{
  await drain();inputs();
  await element('labGenerate').onclick();
  assert.deepEqual(saved.map(c=>c.seed),[17,18]);
  assert.equal(active,null,'生成不自动改变日常音色');
  assert.equal(element('labSeed').value,19);
  assert.match(element('labProgress').textContent,/完成 2 张/);
  element('labCards').children[0].children.find(n=>n.dataset.preview).onclick();
  element('text').value='我自己输入的句子';
  await element('generate').onclick();
  assert.equal(writes.at(-1).url,'/lab/cards/'+saved[0].id+'/preview');
  assert.equal(writes.at(-1).body.text,'我自己输入的句子');
  assert.equal(element('audio').hidden,false);
  assert.equal(active,null,'任意文字试读不激活日常音色');
  await buttons()[0].onclick();
  assert.equal(active,saved[0].id);
  assert.equal(buttons()[0].disabled,true,'当前音色按钮保持禁用');
  await element('labRestore').onclick();
  assert.equal(active,null);assert.equal(saved.length,2,'恢复不删除候选');
  assert.equal(element('labStop').disabled,true);
  inputs();stopAt=3;
  await element('labGenerate').onclick();
  assert.equal(saved.length,3,'停止只等当前张完成');
  assert.match(element('labProgress').textContent,/完成 1 张/);
  inputs();stopAt=0;failAt=5;
  await element('labGenerate').onclick();
  assert.equal(saved.length,4,'失败前完成的候选保留');
  assert.match(element('labProgress').textContent,/已保留 1 张.*超过时限/);
  assert.equal(element('labGenerate').disabled,false);
  assert.equal(writes.some(w=>/moment|wxxz|qq/.test(w.url)),false);
  console.log('Local voice UI checks passed');
})().catch(error=>{console.error(error);process.exitCode=1});
