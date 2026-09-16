// G01 and H01 are positive acceptance tests for glossary compatibility and billed-request history.
import {test} from 'node:test';
import assert from 'node:assert/strict';
import {setup} from '../../cf-worker/tests/helpers/worker.mjs';
test('G01 acceptance: APP glossary replacement preserves web ID/note',async t=>{
 const x=setup(t),a=await x.login('app'),web=await x.login('web');
 const created=await x.request('/v1/terminology',{method:'POST',token:web.token,body:{source:'法兰',target:'Flange',note:'keep me'}});
 const item=await created.json();assert.equal(created.status,201);
 assert.equal((await x.request('/v1/glossary',{method:'PUT',token:a.token,body:{entries:[{source:item.source,target:item.target,enabled:true}]}})).status,200);
 const cloud=await(await x.request('/v1/terminology',{token:web.token})).json();
 assert.equal(cloud.items[0].id,item.id);assert.equal(cloud.items[0].note,'keep me');
 assert.equal((await x.request('/v1/terminology/'+item.id,{method:'DELETE',token:web.token})).status,200);
});
test('H01 acceptance: billed translation appears in web history',async t=>{
 const x=setup(t),a=await x.login('app'),web=await x.login('web');
 t.mock.method(globalThis,'fetch',async()=>Response.json({choices:[{message:{content:JSON.stringify([{id:0,translated_text:'Flange'}])}}]}));
 const r=await x.request('/v1/translate',{method:'POST',token:a.token,body:{source_lang:'zh',target_lang:'en',items:[{id:0,text:'法兰'}]}});
 assert.equal(r.status,200);assert.equal((await r.json()).characters_used,2);
 assert.equal(x.db.prepare("SELECT chars_used FROM usage_monthly WHERE user_id='alice'").get().chars_used,2);
 const history=await(await x.request('/v1/translation/history',{token:web.token})).json();assert.equal(history.items.length,1);assert.equal(history.items[0].characters,2);assert.equal(history.items[0].status,'completed');
});
