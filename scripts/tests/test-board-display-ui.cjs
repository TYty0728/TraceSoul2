const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');
const html=fs.readFileSync(path.join(__dirname,'../../Tools/SensoryBoard/display-preview.html'),'utf8');
const nodes=new Map();
const $=id=>{if(!nodes.has(id))nodes.set(id,{value:'',textContent:'',hidden:false,disabled:false,src:'',events:{},addEventListener(name,fn){this.events[name]=fn;}});return nodes.get(id);};
$('name').value='小汐';$('state').value='speaking';$('subtitle').value='测试字幕';$('remaining').value='30';
const calls=[];let fail=false;const deferred=[];
const sandbox={document:{getElementById:$},setTimeout:()=>1,clearTimeout(){},
 fetch:async(url,options)=>{
  const body=JSON.parse(options.body);calls.push({url,body,method:options.method});
  if(deferred.length)return deferred.shift()();
  return {ok:!fail,json:async()=>fail?{error:'无效字幕'}:{ok:true,preview:'png',display:{page_count:2,page:body.page}}};
 }};
vm.createContext(sandbox);vm.runInContext(html.match(/<script>([\s\S]*)<\/script>/)[1],sandbox);
const tick=()=>new Promise(resolve=>setImmediate(resolve));
(async()=>{
 await tick();
 assert.match($('result').textContent,/没有发送到开发板/);
 assert.equal($('screen').src,'data:image/png;base64,png');
 assert.equal($('prev').disabled,true);assert.equal($('next').disabled,false);
 $('next').onclick();await tick();assert.equal(calls.at(-1).body.page,1);
 assert.equal($('next').disabled,true);assert.equal($('prev').disabled,false);
 $('prev').onclick();await tick();assert.equal(calls.at(-1).body.page,0);
 $('state').value='focused';$('remaining').value='21';
 await sandbox.render();assert.equal($('remainingLabel').hidden,false);assert.equal($('seconds').textContent,'21');
 assert.equal(calls.at(-1).body.remaining,21);
 fail=true;await sandbox.render();assert.match($('result').textContent,/未更新画面：无效字幕/);fail=false;
 let release;
 deferred.push(()=>new Promise(resolve=>release=resolve));
 const stale=sandbox.render();
 await sandbox.render();
 release({ok:true,json:async()=>({ok:true,preview:'stale',display:{page_count:1,page:0}})});
 await stale;assert.notEqual($('screen').src,'data:image/png;base64,stale','迟到请求不能覆盖新状态');
 assert.ok(calls.every(x=>x.url==='/api/display/preview'&&x.method==='POST'),'页面永远只生成预览，不调用设备或模型');
 assert.match(html,/当前没有接入自动翻页或真实通话状态/);
 console.log('Board display UI checks passed: preview-only requests, pagination, focus controls, errors and stale-response suppression.');
})().catch(error=>{console.error(error);process.exit(1);});
