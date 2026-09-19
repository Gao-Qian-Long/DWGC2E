// Isolated workerd/D1 verification, never production writes.
import {test} from 'node:test';
import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';
import {build} from 'esbuild';
import {Miniflare,convertV4MiniflareOptions} from 'miniflare';
test('workerd D1 glossary: APP round-trip, web delete and stale revision rejection', {timeout:30000}, async t=>{
 const bundled=await build({stdin:{contents:`import {glossaryRoute} from './src/account/glossary.ts';
 import {accountDataRoute} from './src/account/data.ts';
 export default {async fetch(r,e){const read=r=>r.json();return new URL(r.url).pathname==='/v1/glossary'?glossaryRoute(r,e,'alice','https://site.test',read):await accountDataRoute(r,e,{user_id:'alice'},'https://site.test',read)||new Response(null,{status:404});}};`,resolveDir:fileURLToPath(new URL('../',import.meta.url)),loader:'ts'},bundle:true,write:false,format:'esm',platform:'browser',target:'es2022'});
 const mf=new Miniflare(convertV4MiniflareOptions({workers:[{name:'glossary-test',modules:true,script:bundled.outputFiles[0].text,d1Databases:{DB:'glossary-isolated'},compatibilityDate:'2026-09-13'}]}));t.after(()=>mf.dispose());
 const DB=await mf.getD1Database('DB','glossary-test');
 await DB.prepare('CREATE TABLE user_glossaries(user_id TEXT PRIMARY KEY,entries_json TEXT NOT NULL,updated_at TEXT NOT NULL)').run();
 const call=(path,method='GET',body)=>mf.dispatchFetch('https://test'+path,{method,...(body===undefined?{}:{headers:{'content-type':'application/json'},body:JSON.stringify(body)})});
 const made=await call('/v1/terminology','POST',{source:'法兰',target:'Flange',note:'keep me'});assert.equal(made.status,201);const item=await made.json();
 const initial=await(await call('/v1/glossary')).json();
 const put=await call('/v1/glossary','PUT',{entries:[{source:'法兰',target:'Flange'}],expected_revision:initial.revision});assert.equal(put.status,200);
 const saved=await put.json();assert.equal(saved.entries[0].id,item.id);assert.equal(saved.entries[0].note,item.note);
 assert.equal((await call('/v1/glossary','PUT',{entries:[],expected_revision:initial.revision})).status,409);
 assert.equal(JSON.parse((await DB.prepare("SELECT entries_json FROM user_glossaries WHERE user_id='alice'").first()).entries_json).length,1);
 assert.equal((await call('/v1/terminology/'+item.id,'DELETE')).status,200);
 assert.equal((await call('/v1/glossary','PUT',{entries:saved.entries,expected_revision:saved.revision})).status,409);
 assert.deepEqual((await(await call('/v1/glossary')).json()).entries,[]);
});
