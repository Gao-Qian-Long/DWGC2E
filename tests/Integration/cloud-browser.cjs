// Real browser + actual Worker routes on isolated SQLite. No production network.
const {test,before,after}=require('node:test');
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const site=path.resolve(process.env.WEBSITE_TEST_ROOT||path.resolve(__dirname,'../../../DWGC2E_Website'));let browser,server,origin,setup,worker;
const csp=fs.readFileSync(path.join(site,'_headers'),'utf8').match(/^\s*Content-Security-Policy:\s*(.+)$/m)?.[1];
assert.ok(csp,'Browser integration requires the actual site CSP');
before(async()=>{
 ({setup}=await import('../../cf-worker/tests/helpers/worker.mjs'));({default:worker}=await import('../../cf-worker/src/index.ts'));
 server=http.createServer((req,res)=>{const f=path.resolve(site,'.'+new URL(req.url,'http://local').pathname);if(!f.startsWith(site+path.sep)){res.writeHead(403);return res.end();}fs.readFile(f,(e,b)=>{res.writeHead(e?404:200,{'Content-Security-Policy':csp,'content-type':({'.html':'text/html; charset=utf-8','.js':'application/javascript','.css':'text/css'})[path.extname(f)]||'application/octet-stream'});res.end(e?'missing':b);});});
 await new Promise(r=>server.listen(0,'127.0.0.1',r));origin='http://127.0.0.1:'+server.address().port;browser=await chromium.launch({channel:'msedge',headless:true});
});
after(async()=>{await browser?.close();await new Promise(r=>server?.close(r));});
async function page(t,options={}){
 const x=setup(t),login=await x.login('web');const p=await browser.newPage({viewport:{width:options.width||390,height:900}});p.setDefaultTimeout(6000);const errors=[];p.on('pageerror',e=>errors.push(e.message));t.after(async()=>{await p.close();assert.deepEqual(errors,[]);});
 await p.addInitScript(({token,local})=>{if(sessionStorage.getItem('fixture-initialized'))return;sessionStorage.setItem('fixture-initialized','1');sessionStorage.setItem('dwgc2e.session',JSON.stringify({token,userId:'alice',expiresAt:'2099-01-01'}));if(local)localStorage.setItem('dwgc2e.glossary.alice',JSON.stringify(local));},{token:login.token,local:options.local});
 const state={puts:0,block:false,readFail:false,loseReply:false};
 await p.route('**/*',async route=>{const req=route.request(),u=new URL(req.url());if(u.origin!==origin)return route.abort();if(!u.pathname.startsWith('/api/'))return route.continue();
  if(u.pathname==='/api/v1/glossary'){
   if(req.method()==='GET'&&state.readFail)return route.fulfill({status:503,json:{message:'读取失败'}});
   if(req.method()==='PUT'){state.puts++;if(state.block)return route.fulfill({status:503,json:{message:'保存失败'}});}
  }
  const response=await worker.fetch(new Request('https://local'+u.pathname.slice(4)+u.search,{method:req.method(),headers:req.headers(),...(['GET','HEAD'].includes(req.method())?{}:{body:req.postData()})}),x.env);
  if(req.method()==='PUT'&&state.loseReply){state.loseReply=false;return route.abort('failed');}
  return route.fulfill({status:response.status,headers:Object.fromEntries(response.headers),body:await response.text()});
 });
 // Playwright response is supplied from Fetch Response, not an APIResponse.
 return {x,p,state,token:login.token};
}
async function open(p,file='terminology.html'){await p.goto(origin+'/'+file);}
async function saved(p){await p.waitForFunction(()=>document.querySelector('#glossaryMessage').textContent.startsWith('已保存到云端'));}
async function add(p,source='法兰'){await p.locator('[name=source]').fill(source);await p.locator('[name=target]').fill('Flange');await p.locator('[name=note]').fill('keep note');await p.locator('#saveGlossary').click();}
const cloud=async ctx=>(await(await ctx.x.request('/v1/glossary',{token:ctx.token})).json()).entries;
for(const width of [390,1440])test('web create/edit/delete persists through reload and APP read '+width,async t=>{
 const c=await page(t,{width});await open(c.p);await add(c.p);await saved(c.p);let entries=await cloud(c);assert.equal(entries.length,1);assert.equal(entries[0].note,'keep note');const id=entries[0].id;
 await c.p.reload();await c.p.locator('[data-edit]').click();await c.p.locator('[name=source]').fill('法兰盘');await c.p.locator('#saveGlossary').click();await saved(c.p);entries=await cloud(c);assert.equal(entries[0].id,id);assert.equal(entries[0].source,'法兰盘');
 const app=await c.x.login('app');assert.equal((await(await c.x.request('/v1/glossary',{token:app.token})).json()).entries[0].source,'法兰盘');
 c.p.once('dialog',d=>d.accept());await c.p.locator('[data-delete]').click();await saved(c.p);assert.deepEqual(await cloud(c),[]);assert.equal(await c.p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),true);
});
test('conflicting writes never replace newer cloud; discard reads latest',async t=>{const c=await page(t);await open(c.p);await c.p.waitForFunction(()=>!document.querySelector('#saveGlossary').disabled);await c.x.request('/v1/terminology',{method:'POST',token:c.token,body:{source:'云端新词',target:'Latest'}});await add(c.p);await c.p.waitForFunction(()=>document.querySelector('#glossaryMessage').textContent.includes('未覆盖云端'));assert.equal((await cloud(c))[0].source,'云端新词');await c.p.locator('#retryGlossary').click();await c.p.waitForFunction(()=>!document.querySelector('#retryGlossary').disabled);assert.equal((await cloud(c)).length,1);c.p.once('dialog',d=>d.accept());await c.p.locator('#discardPendingGlossary').click();await c.p.getByText('云端新词',{exact:true}).waitFor();assert.equal(c.state.puts,2);});
test('lost response after committed write is not blindly re-uploaded',async t=>{const c=await page(t);await open(c.p);c.state.loseReply=true;await add(c.p);await c.p.waitForFunction(()=>!document.querySelector('#retryGlossary').disabled);assert.equal((await cloud(c)).length,1);await c.p.locator('#retryGlossary').click();await c.p.waitForFunction(()=>document.querySelector('#glossaryMessage').textContent.includes('未覆盖云端'));assert.equal((await cloud(c)).length,1);});
test('legacy local entries require explicit merge, retain cloud ID/note, then clear legacy cache',async t=>{const c=await page(t,{local:[{source:'旧词',target:'Old'}]});await c.x.request('/v1/terminology',{method:'POST',token:c.token,body:{source:'云端词',target:'Cloud',note:'keep'}});await open(c.p);await c.p.locator('[data-edit]').waitFor();assert.equal(c.state.puts,0);c.p.once('dialog',d=>d.accept());await c.p.getByRole('button',{name:'合并本机词条到云端'}).click();await saved(c.p);assert.equal((await cloud(c)).length,2);assert.equal((await cloud(c))[0].note,'keep');assert.equal(await c.p.evaluate(()=>localStorage.getItem('dwgc2e.glossary.alice')),null);});
test('read failure disables mutations; refresh recovers',async t=>{const c=await page(t);c.state.readFail=true;await open(c.p);await c.p.waitForFunction(()=>document.querySelector('#glossaryMessage').textContent.includes('读取失败'));assert.equal(await c.p.locator('#saveGlossary').isDisabled(),true);c.state.readFail=false;await c.p.locator('#syncGlossary').click();await c.p.waitForFunction(()=>!document.querySelector('#saveGlossary').disabled);assert.equal(c.state.puts,0);});
test('history page reads settled requests and paginates with truthful status and no download',async t=>{const c=await page(t);c.x.db.prepare("INSERT INTO usage_monthly VALUES('alice','2026-09',0,100000,0)").run();for(let i=0;i<52;i++)c.x.db.prepare("INSERT INTO translation_requests(user_id,request_id,payload_hash,year_month,reserved,billed,state,response_status,expires_at) VALUES('alice',?,'h','2026-09',4,2,'settled',200,?)").run('history-'+i,Math.floor(Date.now()/1000));await open(c.p,'history.html');await c.p.waitForFunction(()=>document.querySelectorAll('#historyList tr').length===50);assert.match(await c.p.locator('#historyList').textContent(),/部分成功/);assert.match(await c.p.locator('#historyList').textContent(),/未记录/);assert.equal(await c.p.locator('#historyList a').count(),0);await c.p.getByRole('button',{name:'加载更多'}).click();await c.p.waitForFunction(()=>document.querySelectorAll('#historyList tr').length===52);await c.p.locator('[data-detail]').first().click();await c.p.waitForFunction(()=>document.querySelector('#historyMessage').textContent.includes('已刷新'));});
test('CSV quoted commas, multiline notes and empty cells are parsed without silent loss',async t=>{const c=await page(t);await open(c.p);await c.p.locator('#importGlossary').setInputFiles({name:'terms.csv',mimeType:'text/csv',buffer:Buffer.from('source,target,note\r\n"法兰,盘",Flange,"line1\nline2"\r\n钢板,Plate,')});await saved(c.p);const rows=await cloud(c);assert.equal(rows.length,2);assert.equal(rows[0].source,'法兰,盘');assert.equal(rows[0].note,'line1\nline2');assert.equal(rows[1].note,'');});
test('invalid import is rejected entirely without partial cloud save',async t=>{const c=await page(t);await open(c.p);await c.p.locator('#importGlossary').setInputFiles({name:'terms.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify([{source:'ok',target:'ok'},{source:'bad',target:''}]))});await c.p.waitForFunction(()=>document.querySelector('#glossaryMessage').textContent.includes('导入失败'));assert.equal(c.state.puts,0);assert.deepEqual(await cloud(c),[]);});

test('merge preview preserves independent cloud addition and local draft, explicit confirmation saves both',async t=>{
 const c=await page(t);await open(c.p);await c.p.waitForFunction(()=>!document.querySelector('#saveGlossary').disabled);
 await c.x.request('/v1/terminology',{method:'POST',token:c.token,body:{source:'云端新增',target:'Cloud'}});await add(c.p);
 await c.p.waitForFunction(()=>document.querySelector('#glossaryMessage').textContent.includes('未覆盖云端'));
 await c.p.locator('#mergePendingGlossary').click();await c.p.getByRole('dialog').waitFor();assert.equal((await cloud(c)).length,1);
 await c.p.getByRole('button',{name:'确认合并并保存云端'}).click();await saved(c.p);assert.equal((await cloud(c)).length,2);
});
test('conflicting modifications require selection; cancel keeps original draft; stale preview remains protected',async t=>{
 const c=await page(t);await c.x.request('/v1/terminology',{method:'POST',token:c.token,body:{source:'冲突词',target:'Base'}});await open(c.p);await c.p.locator('[data-edit]').click();
 await c.p.locator('[name=target]').fill('Local');const original=await c.x.request('/v1/glossary',{token:c.token});const basis=await original.json();await c.x.request('/v1/glossary',{method:'PUT',token:c.token,body:{entries:basis.entries.map(e=>({...e,target:'Remote'})),expected_revision:basis.revision}});
 await c.p.locator('#saveGlossary').click();await c.p.waitForFunction(()=>document.querySelector('#glossaryMessage').textContent.includes('未覆盖云端'));await c.p.locator('#mergePendingGlossary').click();await c.p.getByRole('dialog').waitFor();
 await c.p.getByRole('button',{name:'确认合并并保存云端'}).click();await c.p.getByRole('alert').getByText('请为每个冲突选择本次修改或最新云端。').waitFor();assert.equal((await cloud(c))[0].target,'Remote');
 await c.p.getByRole('button',{name:'取消，保留草稿'}).click();await c.p.locator('#mergePendingGlossary').click();await c.p.getByRole('dialog').waitFor();await c.p.getByRole('radio',{name:'1 本次修改',exact:true}).check();
 await c.x.request('/v1/terminology',{method:'POST',token:c.token,body:{source:'预览后新增',target:'Keep'}});await c.p.getByRole('button',{name:'确认合并并保存云端'}).click();await c.p.waitForFunction(()=>!document.querySelector('#mergePendingGlossary').disabled);assert.equal((await cloud(c))[0].target,'Remote');
 await c.p.locator('#mergePendingGlossary').click();await c.p.getByRole('dialog').waitFor();await c.p.getByRole('button',{name:'确认合并并保存云端'}).click();await saved(c.p);const result=await cloud(c);assert.equal(result.length,2);assert.equal(result[0].target,'Local');
});
test('merge dialog closes on account token change without submitting another write',async t=>{
 const c=await page(t);await open(c.p);await c.p.waitForFunction(()=>!document.querySelector('#saveGlossary').disabled);await c.x.request('/v1/terminology',{method:'POST',token:c.token,body:{source:'云端',target:'Cloud'}});await add(c.p);await c.p.waitForFunction(()=>document.querySelector('#glossaryMessage').textContent.includes('未覆盖云端'));await c.p.locator('#mergePendingGlossary').click();await c.p.getByRole('dialog').waitFor();const puts=c.state.puts;
 await c.p.evaluate(()=>sessionStorage.setItem('dwgc2e.session',JSON.stringify({token:'other-account',userId:'bob'})));await c.p.waitForTimeout(700);assert.equal(c.state.puts,puts);assert.equal(await c.p.locator('dialog[open]').count(),0);
});
test('lost-response reconciliation keeps one cloud entry and server identity',async t=>{
 const c=await page(t);await open(c.p);c.state.loseReply=true;await add(c.p);await c.p.waitForFunction(()=>!document.querySelector('#mergePendingGlossary').disabled);const before=await cloud(c);assert.equal(before.length,1);await c.p.locator('#mergePendingGlossary').click();await c.p.getByRole('dialog').waitFor();await c.p.getByRole('button',{name:'确认合并并保存云端'}).click();await saved(c.p);const after=await cloud(c);assert.equal(after.length,1);assert.equal(after[0].id,before[0].id);
});
test('delete versus changed cloud row is explicit, escaped and keyboard cancellable',async t=>{
 const c=await page(t,{width:390});await c.x.request('/v1/terminology',{method:'POST',token:c.token,body:{source:'<img src=x onerror=alert(1)>',target:'Base'}});await open(c.p);await c.p.locator('[data-delete]').waitFor();const basis=await(await c.x.request('/v1/glossary',{token:c.token})).json();await c.x.request('/v1/glossary',{method:'PUT',token:c.token,body:{entries:basis.entries.map(e=>({...e,note:'云端保留的备注'})),expected_revision:basis.revision}});c.p.once('dialog',d=>d.accept());await c.p.locator('[data-delete]').click();await c.p.waitForFunction(()=>!document.querySelector('#mergePendingGlossary').disabled);await c.p.locator('#mergePendingGlossary').click();await c.p.getByRole('dialog').waitFor();assert.equal(await c.p.locator('dialog img').count(),0);assert.equal((await cloud(c)).length,1);if(process.env.MERGE_SCREENSHOT)await c.p.screenshot({path:process.env.MERGE_SCREENSHOT,fullPage:true});await c.p.keyboard.press('Escape');assert.equal(await c.p.locator('dialog').count(),0);await c.p.locator('#mergePendingGlossary').click();await c.p.getByRole('dialog').waitFor();await c.p.getByRole('radio',{name:'1 最新云端',exact:true}).check();await c.p.getByRole('button',{name:'确认合并并保存云端'}).click();await saved(c.p);assert.equal((await cloud(c))[0].note,'云端保留的备注');
});

test('history shows recorded language, exact start/end and clearly labelled legacy estimates',async t=>{
 const c=await page(t,{width:390});
 c.x.db.exec("INSERT INTO usage_monthly VALUES('alice','2026-09',0,100000,0)");
 c.x.db.exec("INSERT INTO translation_requests(user_id,request_id,payload_hash,year_month,reserved,expires_at,source_language,target_language,started_at,completed_at,state,response_status,billed) VALUES('alice','modern-history','h','2026-09',2,1770000180,'ZH','en-US','2026-09-16T01:02:03.123Z','2026-09-16T01:02:05.456Z','settled',200,2)");
 c.x.db.exec("INSERT INTO translation_requests(user_id,request_id,payload_hash,year_month,reserved,expires_at) VALUES('alice','legacy-history','h','2026-09',2,1770000180)");
 await open(c.p,'history.html');await c.p.locator('[data-detail]').first().waitFor();
 const modern=c.p.locator('#historyList tr').filter({hasText:'en-US'});assert.equal(await modern.count(),1);
 assert.match(await modern.innerText(),/2026-09-16T01:02:03.123Z/);assert.match(await modern.innerText(),/结束：2026-09-16T01:02:05.456Z/);
 const legacy=c.p.locator('#historyList tr').filter({hasText:'约 '});assert.match(await legacy.innerText(),/未记录/);
 await c.p.locator('#historyFilter').fill('01:02:05.456');assert.equal(await c.p.locator('#historyList [data-detail]').count(),1);
 assert.equal(await c.p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),true);
 if(process.env.HISTORY_SCREENSHOT)await c.p.screenshot({path:process.env.HISTORY_SCREENSHOT,fullPage:true});
});
test('history explicitly labels timeout deadline instead of implying delayed cleanup completion',async t=>{
 const c=await page(t);c.x.db.exec("INSERT INTO usage_monthly VALUES('alice','2026-09',0,100000,0)");
 c.x.db.exec("INSERT INTO translation_requests(user_id,request_id,payload_hash,year_month,reserved,expires_at,completed_at,state,response_status,billed) VALUES('alice','expired-history','h','2026-09',2,1770000180,'2026-02-02T02:43:00.000Z','settled',504,0)");
 await open(c.p,'history.html');await c.p.locator('[data-detail]').first().waitFor();assert.match(await c.p.locator('#historyList').innerText(),/超时截止：2026-02-02T02:43:00.000Z/);
});

// Unlike the UI fixture matrix, rejection here comes from the actual Worker and DB.
for(const file of ['account','profile','devices','history','terminology'])test(`server logout invalidates open ${file} page on next real API action`,{timeout:20000},async t=>{
 const c=await page(t);
 c.x.db.prepare("UPDATE users SET display_name='PRIVATE-ALICE' WHERE id='alice'").run();
 const app=await c.x.login('app'),bob=await c.x.login('web','','bob');
 assert.equal(app.status,200);assert.equal(bob.status,200);
 const seed=await c.x.request('/v1/terminology',{method:'POST',token:c.token,body:{source:'PRIVATE-TERM',target:'Keep'}});assert.equal(seed.status,201);
 await open(c.p,file+'.html');
 const ready={account:'#refreshButton:enabled',profile:'#displayName:enabled',devices:'[data-revoke]',history:'#retryHistory:enabled',terminology:'#saveGlossary:enabled'};
 await c.p.locator(ready[file]).waitFor();
 if(file==='account')await c.p.waitForFunction(()=>document.querySelector('#accountPanel').textContent.includes('PRIVATE-ALICE'));
 if(file==='terminology')await c.p.waitForFunction(()=>document.querySelector('#glossaryList').textContent.includes('PRIVATE-TERM'));
 const before=c.x.db.prepare('SELECT * FROM user_glossaries ORDER BY user_id').all();
 assert.equal((await c.x.request('/v1/auth/logout',{method:'POST',token:c.token})).status,200);
 const trigger={account:'#refreshButton',profile:'#retryProfile',devices:'#retryDevices',history:'#retryHistory',terminology:'#syncGlossary'};
 const rejected=c.p.waitForResponse(r=>new URL(r.url()).pathname.startsWith('/api/')&&r.status()===401);
 await c.p.locator(trigger[file]).click();await rejected;
 await c.p.waitForFunction(()=>sessionStorage.getItem('dwgc2e.session')===null);
 if(['history','terminology'].includes(file)){await c.p.waitForURL('**/account.html?return=*');await c.p.locator('#authPanel:not([hidden])').waitFor();}
 else if(file==='account')await c.p.locator('#authPanel:not([hidden])').waitFor();
 else {await c.p.locator('.portal-login-link').waitFor();if(file==='profile'){assert.equal(await c.p.locator('#displayName').inputValue(),'');assert.equal(await c.p.locator('#displayName').isDisabled(),true);}else assert.equal(await c.p.locator('[data-revoke]').count(),0);}
 assert.deepEqual(c.x.db.prepare('SELECT * FROM user_glossaries ORDER BY user_id').all(),before);
 assert.equal((await c.x.request('/v1/profile',{token:app.token})).status,200,'web logout must not revoke APP');
 assert.equal((await c.x.request('/v1/profile',{token:bob.token})).status,200,'another account unaffected');
 assert.equal(c.x.db.prepare('SELECT COUNT(*) n FROM orders').get().n,0);
 assert.equal(c.x.db.prepare('SELECT COUNT(*) n FROM translation_requests').get().n,0);
});

test('server-revoked glossary session rejects attempted write and preserves cloud entries',{timeout:20000},async t=>{
 const c=await page(t);await open(c.p);await c.p.locator('#saveGlossary:enabled').waitFor();
 await c.p.locator('[name=source]').fill('DO-NOT-SAVE');await c.p.locator('[name=target]').fill('Rejected');
 assert.equal((await c.x.request('/v1/auth/logout',{method:'POST',token:c.token})).status,200);
 const rejected=c.p.waitForResponse(r=>r.request().method()==='PUT'&&r.status()===401);
 await c.p.locator('#saveGlossary').click();await rejected;
 await c.p.waitForURL('**/account.html?return=*');assert.equal(await c.p.evaluate(()=>sessionStorage.getItem('dwgc2e.session')),null);
 assert.equal(c.state.puts,1);assert.equal(c.x.db.prepare('SELECT COUNT(*) n FROM user_glossaries').get().n,0);
});

for(const action of ['view','confirm','hide'])test(`server-revoked billing session blocks ${action} without changing order or entitlement`,{timeout:20000},async t=>{
 const c=await page(t);const app=await c.x.login('app');
 c.x.db.prepare(`INSERT INTO orders(order_no,user_id,plan_id,plan_name,duration_days,membership_level,amount_cents,payable_cents,channel,idempotency_key,created_at,expires_at,create_state,qr_code) VALUES('PRIVATE-ORDER','alice','test_pro_019','PRIVATE-PLAN',7,'pro',19,19,'alipay','fixture-order-key',?,?,'ready','TEST-ONLY-NOT-PAYABLE')`).run(new Date().toISOString(),new Date(Date.now()+3600000).toISOString());
 await open(c.p,'billing.html');await c.p.getByRole('button',{name:'查看订单',exact:true}).waitFor();
 if(action==='confirm'){
  await c.p.getByRole('button',{name:'查看订单',exact:true}).click();
  await c.p.locator('#checkout:not([hidden])').waitFor();
 }
 const tables=['orders','subscriptions','payment_events','payment_settlements','payment_order_hidden'];
 const snapshot=()=>Object.fromEntries(tables.map(table=>[table,c.x.db.prepare(`SELECT * FROM ${table}`).all()]));
 const before=snapshot();assert.equal((await c.x.request('/v1/auth/logout',{method:'POST',token:c.token})).status,200);
 const rejected=c.p.waitForResponse(r=>new URL(r.url()).pathname.startsWith('/api/')&&r.status()===401);
 if(action==='view')await c.p.getByRole('button',{name:'查看订单',exact:true}).click();
 if(action==='confirm')await c.p.locator('#refreshOrder').click();
 if(action==='hide'){c.p.once('dialog',d=>d.accept());await c.p.getByRole('button',{name:'删除记录',exact:true}).click();}
 await rejected;await c.p.waitForFunction(()=>sessionStorage.getItem('dwgc2e.session')===null);
 await c.p.locator('#loginLink:not([hidden])').waitFor();
 assert.equal(await c.p.locator('#checkout').isVisible(),false);
 assert.equal(await c.p.locator('#qrBox').innerText(),'');
 assert.equal(await c.p.locator('#orderList').innerText(),'');
 assert.equal(await c.p.locator('#plans button').count(),0);
 assert.deepEqual(snapshot(),before,'revoked action must not mutate financial records');
 assert.equal((await c.x.request('/v1/profile',{token:app.token})).status,200);
});
