// Transport lifecycle tests run against an explicit production candidate or website root.
import {test} from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFileSync} from 'node:fs';
import path from 'node:path';
const root=process.env.WEBSITE_TEST_ROOT||'D:/DWGC2E_Website';
const source=readFileSync(path.join(root,'js/api-client.js'),'utf8');
function setup(fetch){
 const data=new Map([['dwgc2e.session',JSON.stringify({token:'old',expiresAt:'2099-01-01'})]]);
 const storage={getItem:k=>data.get(k)||null,removeItem:k=>data.delete(k),setItem:(k,v)=>data.set(k,v)};
 let now=Date.now();class ClockDate extends Date {static now(){return now;}}
 const context={Date:ClockDate,window:{QLCAD_SITE:{apiBaseUrl:'/api'}},sessionStorage:storage,fetch,AbortController,FormData,setTimeout,clearTimeout,TypeError};
 vm.runInNewContext(source,context);return {api:context.window.QLCAD_API,storage,advance:ms=>now+=ms};
}
for(const replacement of [null,'new'])for(const status of [200,401])test(`late ${status} after ${replacement?'account switch':'logout'} is discarded`,async()=>{
 let finish;const x=setup(()=>new Promise(r=>finish=r));const request=x.api.account.profile();
 if(replacement)x.storage.setItem('dwgc2e.session',JSON.stringify({token:replacement}));else x.storage.removeItem('dwgc2e.session');
 finish(Response.json(status===200?{private:'previous user'}:{error_code:'unauthenticated'},{status}));
 await assert.rejects(request,e=>e.code==='session_changed');
 assert.equal(x.storage.getItem('dwgc2e.session'),replacement?JSON.stringify({token:replacement}):null);
});
for(const session of [{token:123},{token:'old',expiresAt:'2000-01-01'},{token:'old',expiresAt:'not-a-date'}])test('invalid/expired stored credential is never transmitted '+JSON.stringify(session),async()=>{
 let bearer;const x=setup(async(_,o)=>{bearer=o.headers.Authorization;return Response.json({error_code:'unauthenticated'},{status:401});});
 x.storage.setItem('dwgc2e.session',JSON.stringify(session));await assert.rejects(x.api.account.profile());assert.equal(bearer,undefined);
});
test('credential expiring while request is pending cannot expose successful private response',async()=>{
 let finish;const x=setup(()=>new Promise(r=>finish=r));const request=x.api.account.profile();
 x.storage.setItem('dwgc2e.session',JSON.stringify({token:'old',expiresAt:'2000-01-01'}));finish(Response.json({private:'stale'}));
 await assert.rejects(request,e=>e.code==='session_changed');
});
test('invalid JSON storage never becomes a bearer',async()=>{
 const x=setup(async(_,o)=>{assert.equal(o.headers.Authorization,undefined);return Response.json({error_code:'unauthenticated'},{status:401});});
 x.storage.setItem('dwgc2e.session','{broken');await assert.rejects(x.api.account.profile());
});
test('alternate authorization failure does not discard the saved user session',async()=>{
 const x=setup(async(_,o)=>{assert.equal(o.headers.authorization,'Bearer other');assert.equal(o.headers.Authorization,undefined);return Response.json({error_code:'unauthenticated'},{status:401});});
 await assert.rejects(x.api.request('/v1/profile',{headers:{authorization:'Bearer other'}}),e=>!e.authExpired);assert.equal(JSON.parse(x.storage.getItem('dwgc2e.session')).token,'old');
});
test('anonymous failed login preserves current session',async()=>{
 const x=setup(async(_,o)=>{assert.equal(o.headers.Authorization,undefined);return Response.json({error_code:'invalid_credentials'},{status:401});});
 await assert.rejects(x.api.auth.login({}),e=>!e.authExpired);assert.equal(JSON.parse(x.storage.getItem('dwgc2e.session')).token,'old');
});

test('wall-clock expiry during request rejects stale data without storage changes',async()=>{
 let finish;const x=setup(()=>new Promise(r=>finish=r));
 const stored=JSON.stringify({token:'old',expiresAt:new Date(Date.now()+60000).toISOString()});x.storage.setItem('dwgc2e.session',stored);
 const request=x.api.account.profile();x.advance(120000);finish(Response.json({private:'stale'}));
 await assert.rejects(request,e=>e.code==='session_changed');assert.equal(x.storage.getItem('dwgc2e.session'),stored);
});
