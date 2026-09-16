const {test,before,after}=require('node:test');
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE);
if(!process.env.SITE_ROOT)throw Error('Set SITE_ROOT to the isolated production-based public directory.');
const root=path.resolve(process.env.SITE_ROOT);
const csp=fs.readFileSync(path.join(root,'_headers'),'utf8').match(/Content-Security-Policy: (.+)/)[1];
let server,browser,origin;
before(async()=>{server=http.createServer((req,res)=>{let p=new URL(req.url,'http://local').pathname;if(!path.extname(p))p+='.html';const file=path.resolve(root,'.'+p);if(!file.startsWith(root+path.sep)){res.writeHead(403);return res.end();}fs.readFile(file,(e,b)=>{res.writeHead(e?404:200,{'content-type':({'.html':'text/html; charset=utf-8','.js':'application/javascript','.css':'text/css','.svg':'image/svg+xml'})[path.extname(file)]||'application/octet-stream','Content-Security-Policy':csp});res.end(e?'not found':b);});});await new Promise(r=>server.listen(0,'127.0.0.1',r));origin=`http://127.0.0.1:${server.address().port}`;browser=await chromium.launch({channel:'msedge',headless:true});});
after(async()=>{await browser?.close();await new Promise(r=>server.close(r));});
const session={token:'ALICE-FIXTURE',userId:'alice',expiresAt:'2099-01-01'};
const row={id:'task-alice',file_name:'ALICE-PRIVATE.dwg',status:'completed',source_language:'ZH',target_language:'EN',created_at:'2026-09-16T01:00:00Z',completed_at:'2026-09-16T01:00:01Z'};
async function pageFor(t,file,width=1280,override){const p=await browser.newPage({viewport:{width,height:900}});p.setDefaultTimeout(2500);const calls=[],errors=[];p.on('pageerror',e=>errors.push(e.message));t.after(async()=>{await p.close();assert.deepEqual(errors,[]);});await p.addInitScript(s=>{if(!sessionStorage.getItem('fixture-initialized')){sessionStorage.setItem('fixture-initialized','1');sessionStorage.setItem('dwgc2e.session',JSON.stringify(s));}},session);await p.route('**/*',async r=>{const u=new URL(r.request().url());if(u.origin!==origin)return r.abort();if(!u.pathname.startsWith('/api/')){if(process.env.LOCAL_ASSET_ROUTES!=='1')return r.continue();let pathname=u.pathname;if(!path.extname(pathname))pathname+='.html';const local=path.resolve(root,'.'+pathname);if(!local.startsWith(root+path.sep))return r.abort();return r.fulfill({status:fs.existsSync(local)?200:404,headers:{'content-security-policy':csp,'content-type':({'.html':'text/html; charset=utf-8','.js':'application/javascript','.css':'text/css','.svg':'image/svg+xml'})[path.extname(local)]||'application/octet-stream'},body:fs.existsSync(local)?fs.readFileSync(local):'missing'});}calls.push({path:u.pathname,method:r.request().method(),token:r.request().headers().authorization});if(override&&await override(u,r))return;const data={'/api/v1/profile':{user_id:'alice',display_name:'ALICE-PRIVATE',email:'alice@example.invalid'},'/api/v1/subscription':{plan_name:'pro'},'/api/v1/usage':{monthly_quota:10000,used:10},'/api/v1/devices':{devices:[{device_id:'alice-device',device_name:'ALICE-DEVICE'}]},'/api/v1/billing/entitlements':{userId:'alice',subscription:{plan_name:'pro'},usage:{monthly_quota:10000,used:10}},'/api/v1/billing/plans':{paymentsEnabled:false,plans:[]},'/api/v1/billing/orders':{orders:[]},'/api/v1/translation/history':{items:[row],nextCursor:null},'/api/v1/glossary':{entries:[],version:1},'/api/v1/health':{api:'operational',database:'operational'}};return r.fulfill({json:data[u.pathname]||{success:true}});});await p.goto(origin+'/'+file+'.html');return {p,calls};}
const loaded={account:'#accountPanel:not([hidden])',profile:'#displayName:enabled',devices:'[data-revoke]',history:'[data-detail]',terminology:'#glossaryMessage',billing:'#membership'};
async function protectedDataGone(p,file){return p.evaluate(f=>{if(location.pathname.endsWith('/account.html')&&f!=='account')return true;const main=document.querySelector('main');if(main?.hidden)return true;if(f==='account')return document.querySelector('#accountPanel').hidden;if(f==='profile')return document.querySelector('#displayName').disabled&&!document.querySelector('#displayName').value;if(f==='devices')return !document.querySelector('[data-revoke]');if(f==='billing')return !document.querySelector('#orderList').textContent.trim()&&document.querySelector('#loginLink').hidden===false&&!document.querySelector('#membership').textContent.includes('pro');return false;},file);}
for(const event of ['focus','pageshow','storage'])for(const file of Object.keys(loaded))for(const state of ['removed','replaced','expired'])test(`${file}: ${state} session on ${event} hides old data`,{timeout:15000},async t=>{const {p,calls}=await pageFor(t,file);await p.locator(loaded[file]).waitFor();if(file==='billing')await p.waitForFunction(()=>document.querySelector('#membership').textContent.includes('pro'));await p.evaluate(({state,event})=>{if(state==='removed')sessionStorage.removeItem('dwgc2e.session');else sessionStorage.setItem('dwgc2e.session',JSON.stringify({token:state==='replaced'?'BOB-FIXTURE':'ALICE-FIXTURE',userId:state==='replaced'?'bob':'alice',expiresAt:state==='expired'?'2000-01-01':'2099-01-01'}));dispatchEvent(new Event(event));},{state,event});await p.waitForTimeout(100);assert.equal(await protectedDataGone(p,file),true);assert.equal(calls.some(c=>c.method!=='GET'),false);if(state==='replaced')assert.equal(await p.evaluate(()=>JSON.parse(sessionStorage.getItem('dwgc2e.session'))?.token),'BOB-FIXTURE');});
for(const status of [200,401])test(`history delayed ${status} cannot replace switched session`,{timeout:15000},async t=>{let release,started;const startedP=new Promise(r=>started=r);const barrier=new Promise(r=>release=r);const {p}=await pageFor(t,'history',390,async(u,r)=>{if(!u.pathname.endsWith('/translation/history'))return false;started();await barrier;await r.fulfill({status,json:status===200?{items:[row]}:{error:'unauthenticated'}});return true;});await startedP;await p.evaluate(()=>sessionStorage.setItem('dwgc2e.session',JSON.stringify({token:'BOB-FIXTURE',expiresAt:'2099-01-01'})));release();await p.waitForURL('**/account.html?return=history.html');assert.equal(await p.evaluate(()=>JSON.parse(sessionStorage.getItem('dwgc2e.session'))?.token),'BOB-FIXTURE');});
for(const file of Object.keys(loaded))test(`${file}: idle expiry hides old account without user action`,{timeout:15000},async t=>{const {p}=await pageFor(t,file);await p.locator(loaded[file]).waitFor();if(file==='billing')await p.waitForFunction(()=>document.querySelector('#membership').textContent.includes('pro'));await p.evaluate(()=>{const s=JSON.parse(sessionStorage.getItem('dwgc2e.session'));s.expiresAt=new Date(Date.now()+100).toISOString();sessionStorage.setItem('dwgc2e.session',JSON.stringify(s));});await p.waitForTimeout(1400);assert.equal(await protectedDataGone(p,file),true);});
for(const width of [320,390,1280])test(`history pagination, retry, legacy and safe content at ${width}px`,{timeout:15000},async t=>{let pageTwo=false,fail=true;const hostile='<img src=x onerror="window.historyXss=1">';const {p,calls}=await pageFor(t,'history',width,async(u,r)=>{if(u.pathname==='/api/v1/translation/history'){if(u.searchParams.has('before')){pageTwo=true;await r.fulfill(fail?{status:503,json:{message:'isolated unavailable'}}:{json:{items:[{id:'legacy',file_name:'LEGACY',status:'completed',download_url:'javascript:window.historyXss=2'}],nextCursor:null}});}else await r.fulfill({json:{items:[{...row,file_name:hostile}],nextCursor:'cursor-one'}});return true;}if(u.pathname==='/api/v1/translation/tasks/legacy'){await r.fulfill({status:404,json:{error:'not_found'}});return true;}return false;});await p.locator('[data-detail]').waitFor();await p.getByRole('button',{name:'加载更多'}).click();await p.waitForFunction(()=>document.querySelector('#historyMessage').textContent.includes('isolated unavailable'));assert.equal(await p.locator('#historyList tr').count(),1);assert.equal(pageTwo,true);fail=false;await p.getByRole('button',{name:'加载更多'}).click();await p.waitForFunction(()=>document.querySelectorAll('#historyList tr').length===2);assert.match(await p.locator('#historyList').textContent(),/未记录/);assert.equal(await p.locator('#historyList img, #historyList a').count(),0);assert.equal(await p.evaluate(()=>window.historyXss),undefined);await p.locator('#historyFilter').fill('LEGACY');assert.equal(await p.locator('#historyList tr').count(),1);await p.locator('[data-detail="legacy"]').click();await p.waitForFunction(()=>document.querySelector('#historyMessage').textContent.includes('记录不存在'));assert.match(await p.locator('#historyList').textContent(),/LEGACY/);await p.locator('#historyFilter').fill('missing-task');assert.match(await p.locator('#historyList').textContent(),/没有匹配/);assert.equal(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),true);assert.equal(calls.some(c=>c.method!=='GET'),false);});
test('account unconfirmed logout preserves session; confirmed logout clears it',{timeout:15000},async t=>{let fail=true;const {p}=await pageFor(t,'account',390,async(u,r)=>{if(u.pathname!=='/api/v1/auth/logout')return false;await r.fulfill(fail?{status:503,json:{error:'unavailable'}}:{json:{success:true}});return true;});await p.locator('#accountPanel:not([hidden])').waitFor();await p.locator('#logoutButton').click();await p.waitForFunction(()=>document.querySelector('#dashboardMessage').textContent.includes('尚未确认'));assert.equal(await p.evaluate(()=>JSON.parse(sessionStorage.getItem('dwgc2e.session')).token),session.token);fail=false;await p.locator('#logoutButton').click();await p.locator('#authPanel:not([hidden])').waitFor();assert.equal(await p.evaluate(()=>sessionStorage.getItem('dwgc2e.session')),null);});
for(const initialFailure of [false,true])test(`history network disconnect and retry; initial failure=${initialFailure}`,{timeout:15000},async t=>{let offline=initialFailure;const {p,calls}=await pageFor(t,'history',390,async(u,r)=>{if(!u.pathname.includes('/translation/'))return false;if(offline){await r.abort('internetdisconnected');return true;}if(u.pathname.includes('/tasks/')){await r.fulfill({json:{...row,status:'partial'}});return true;}return false;});if(initialFailure){await p.waitForFunction(()=>document.querySelector('#historyMessage').textContent.includes('无法连接'));assert.match(await p.locator('#historyList').textContent(),/暂未加载/);offline=false;await p.locator('#retryHistory').click();}await p.locator('[data-detail]').waitFor();offline=true;await p.locator('#retryHistory').click();await p.waitForFunction(()=>document.querySelector('#historyMessage').textContent.includes('无法连接'));assert.match(await p.locator('#historyList').textContent(),/ALICE-PRIVATE/);assert.equal(await p.locator('#retryHistory').isEnabled(),true);await p.locator('[data-detail]').click();await p.waitForFunction(()=>document.querySelector('#historyMessage').textContent.includes('无法连接'));assert.equal(await p.locator('[data-detail]').isEnabled(),true);offline=false;await p.locator('[data-detail]').click();await p.waitForFunction(()=>document.querySelector('#historyList').textContent.includes('部分成功'));assert.equal(await p.evaluate(()=>JSON.parse(sessionStorage.getItem('dwgc2e.session')).token),session.token);assert.equal(calls.some(c=>c.method!=='GET'),false);});
for(const width of [320,390,1280])test(`history keyboard-only search and refresh ${width}px`,{timeout:15000},async t=>{let details=0;const {p}=await pageFor(t,'history',width,async(u,r)=>{if(!u.pathname.includes('/translation/tasks/'))return false;details++;await r.fulfill({json:{...row,status:'partial'}});return true;});await p.locator('[data-detail]').waitFor();let found=false;for(let i=0;i<45;i++){await p.keyboard.press('Tab');if(await p.locator('#historyFilter').evaluate(e=>e===document.activeElement)){found=true;break;}}assert.equal(found,true,'search must be reachable with Tab');await p.keyboard.type('no-match');assert.match(await p.locator('#historyList').textContent(),/没有匹配/);await p.keyboard.press('Control+A');await p.keyboard.press('Backspace');let refreshFocused=false;for(let i=0;i<8;i++){await p.keyboard.press('Tab');if(await p.locator('[data-detail]').evaluate(e=>e===document.activeElement)){refreshFocused=true;break;}}assert.equal(refreshFocused,true,'row refresh reachable through scroll-container tab stop');await p.keyboard.press('Enter');await p.waitForFunction(()=>document.querySelector('#historyList').textContent.includes('部分成功'));assert.equal(details,1);});
test('history blocks duplicate list requests while a refresh is pending',{timeout:15000},async t=>{let count=0,release,started;const pending=new Promise(r=>release=r),began=new Promise(r=>started=r);const {p}=await pageFor(t,'history',1280,async(u,r)=>{if(!u.pathname.endsWith('/translation/history'))return false;count++;if(count===2){started();await pending;}await r.fulfill({json:{items:[row],nextCursor:null}});return true;});await p.locator('[data-detail]').waitFor();await p.locator('#retryHistory').click();await began;try{assert.equal(await p.locator('#retryHistory').isDisabled(),true);await p.locator('#retryHistory').dispatchEvent('click');assert.equal(count,2);}finally{release();}await p.waitForFunction(()=>!document.querySelector('#retryHistory').disabled);assert.equal(count,2);});
for(const file of ['profile','devices'])test(`${file} transport failure preserves data and retry restores service`,{timeout:15000},async t=>{let offline=false;const endpoint=file==='profile'?'/api/v1/profile':'/api/v1/devices';const {p,calls}=await pageFor(t,file,390,async(u,r)=>{if(u.pathname!==endpoint||!offline)return false;await r.abort('internetdisconnected');return true;});await p.locator(loaded[file]).waitFor();const retry=p.locator(file==='profile'?'#retryProfile':'#retryDevices');offline=true;await retry.click();await p.waitForFunction(()=>document.querySelector('#portalMessage').textContent.includes('重试'));if(file==='profile')assert.equal(await p.locator('#displayName').inputValue(),'ALICE-PRIVATE');else assert.match(await p.locator('#deviceList').textContent(),/ALICE-DEVICE/);assert.equal(await retry.isEnabled(),true);offline=false;await retry.click();await p.waitForFunction(()=>!document.querySelector('#portalMessage').textContent.includes('失败')&&!document.querySelector('#portalMessage').textContent.includes('无法'));assert.equal(calls.some(c=>c.method!=='GET'),false);assert.equal(await p.evaluate(()=>JSON.parse(sessionStorage.getItem('dwgc2e.session')).token),session.token);});

// Payment UI acceptance uses fixture API responses only; never invokes a live gateway.
const fixturePlan={id:'fixture-pro',name:'FIXTURE PRO',duration_days:30,price_cents:100};
const fixtureOrder={orderNo:'FIXTURE-ORDER-A',planName:'FIXTURE PRO',planId:'fixture-pro',payableCents:100,channel:'alipay',status:'pending',displayState:'awaiting_payment',createState:'ready',createdAt:'2026-09-16T00:00:00Z',expiresAt:'2099-01-01T00:00:00Z',qrCode:'fixture-only-not-a-payment',allowedActions:{pay:true}};
const pendingStorageKey='dwgc2e.payment.pending.alice';
for(const reload of [false,true])test(`billing lost checkout response reuses original intent; reload=${reload}`,{timeout:15000},async t=>{
 const attempts=[];
 const {p}=await pageFor(t,'billing',390,async(u,r)=>{
  if(u.pathname==='/api/v1/billing/plans'){await r.fulfill({json:{paymentsEnabled:true,plans:[fixturePlan]}});return true;}
  if(u.pathname==='/api/v1/billing/checkout'){
   attempts.push({key:r.request().headers()['idempotency-key'],body:r.request().postDataJSON()});
   if(attempts.length===1)await r.abort('internetdisconnected');else await r.fulfill({json:fixtureOrder});
   return true;
  }
  return false;
 });
 await p.getByRole('button',{name:'立即购买',exact:true}).click();
 await p.waitForFunction(()=>document.querySelector('#portalMessage').textContent.includes('复用原购买标识'));
 const saved=await p.evaluate(k=>JSON.parse(localStorage.getItem(k)),pendingStorageKey);
 assert.equal(saved.key,attempts[0].key);assert.equal(saved.orderNo,undefined);
 if(reload){await p.reload();await p.getByRole('button',{name:'确认原购买',exact:true}).click();}
 else await p.getByRole('button',{name:'立即购买',exact:true}).click();
 await p.locator('#checkout:not([hidden])').waitFor();
 assert.equal(attempts.length,2);assert.deepEqual(attempts[1],attempts[0]);
 assert.equal(await p.locator('#orderNo').textContent(),fixtureOrder.orderNo);
 assert.equal(await p.evaluate(k=>JSON.parse(localStorage.getItem(k)).orderNo,pendingStorageKey),fixtureOrder.orderNo);
});

test('billing offline confirmation preserves order and paid retry updates membership without checkout',{timeout:15000},async t=>{
 let offline=true,confirms=0;
 const {p,calls}=await pageFor(t,'billing',390,async(u,r)=>{
  if(u.pathname==='/api/v1/billing/orders'){await r.fulfill({json:{orders:[fixtureOrder],nextCursor:null}});return true;}
  if(u.pathname.endsWith('/confirm')){confirms++;if(offline)await r.abort('internetdisconnected');else await r.fulfill({json:{...fixtureOrder,status:'paid',allowedActions:{pay:false}}});return true;}
  if(u.pathname==='/api/v1/billing/orders/'+fixtureOrder.orderNo){await r.fulfill({json:fixtureOrder});return true;}
  return false;
 });
 await p.getByRole('button',{name:'查看订单',exact:true}).click();
 await p.locator('#qrBox canvas').waitFor();
 await p.locator('#refreshOrder').click();
 await p.waitForFunction(()=>document.querySelector('#portalMessage').textContent.includes('无法连接'));
 assert.equal(await p.locator('#orderNo').textContent(),fixtureOrder.orderNo);
 assert.equal(await p.locator('#refreshOrder').isEnabled(),true);
 offline=false;await p.locator('#refreshOrder').click();
 await p.waitForFunction(()=>document.querySelector('#portalMessage').textContent.includes('支付成功，会员与额度已同步'));
 assert.equal(confirms,2);assert.equal(await p.locator('#qrBox canvas').count(),0);
 assert.match(await p.locator('#qrBox').textContent(),/无需再次扫码/);
 assert.equal(calls.some(c=>c.path.endsWith('/checkout')),false);
});

for(const qrState of ['expired','unknown'])test(`billing ${qrState} order never displays payable QR`,{timeout:15000},async t=>{
 const order={...fixtureOrder,...(qrState==='expired'?{expiresAt:'2020-01-01T00:00:00Z'}:{createState:'unknown',allowedActions:{pay:false}})};
 const {p,calls}=await pageFor(t,'billing',320,async(u,r)=>{
  if(u.pathname==='/api/v1/billing/orders'){await r.fulfill({json:{orders:[order],nextCursor:null}});return true;}
  if(u.pathname==='/api/v1/billing/orders/'+order.orderNo){await r.fulfill({json:order});return true;}
  return false;
 });
 await p.getByRole('button',{name:'查看订单',exact:true}).click();
 await p.locator('#checkout:not([hidden])').waitFor();
 assert.equal(await p.locator('#qrBox canvas, #qrBox img').count(),0);
 assert.match(await p.locator('#qrBox').textContent(),qrState==='expired'?/付款窗口已结束/:/请勿重复下单或付款/);
 assert.equal(await p.locator('#scanHint').isHidden(),true);
 assert.equal(calls.some(c=>c.method!=='GET'),false);
});

test('billing stale status response cannot overwrite newly selected order',{timeout:15000},async t=>{
 let release,started;const pending=new Promise(r=>release=r),began=new Promise(r=>started=r);
 const other={...fixtureOrder,orderNo:'FIXTURE-ORDER-B',planName:'SECOND FIXTURE'};
 const {p}=await pageFor(t,'billing',1280,async(u,r)=>{
  if(u.pathname==='/api/v1/billing/orders'){await r.fulfill({json:{orders:[fixtureOrder,other],nextCursor:null}});return true;}
  if(u.pathname==='/api/v1/billing/orders/'+fixtureOrder.orderNo){started();await pending;await r.fulfill({json:{...fixtureOrder,status:'paid'}});return true;}
  if(u.pathname==='/api/v1/billing/orders/'+other.orderNo){await r.fulfill({json:other});return true;}
  return false;
 });
 try{
  await p.getByRole('button',{name:'查看订单',exact:true}).nth(0).click();await began;
  await p.getByRole('button',{name:'查看订单',exact:true}).nth(1).click();
  await p.waitForFunction(()=>document.querySelector('#orderNo').textContent==='FIXTURE-ORDER-B');
  const received=p.waitForResponse(r=>new URL(r.url()).pathname.endsWith('/FIXTURE-ORDER-A'));
  release();await received;
  // Browser round-trip after response processing; no arbitrary network sleep.
  await p.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
  assert.equal(await p.locator('#orderNo').textContent(),other.orderNo);
  assert.equal(await p.locator('#checkoutTitle').textContent(),other.planName);
  assert.match(await p.locator('#paymentStatus').textContent(),/等待付款/);
 }finally{release();}
});
