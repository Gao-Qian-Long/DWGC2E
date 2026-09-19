import {test} from 'node:test';import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';import {build} from 'esbuild';import {Miniflare,convertV4MiniflareOptions} from 'miniflare';
test('history projection SQL executes in real workerd/D1, including detail and account filter',{timeout:30000},async t=>{
 const code=await build({stdin:{contents:`import {translationHistoryRoute} from './src/account/history.ts';export default {async fetch(r,e){return await translationHistoryRoute(r,e,'alice','https://site.test')||new Response(null,{status:404})}};`,resolveDir:fileURLToPath(new URL('../',import.meta.url)),loader:'ts'},bundle:true,write:false,format:'esm',platform:'browser',target:'es2022'});
 const mf=new Miniflare(convertV4MiniflareOptions({workers:[{name:'history-test',modules:true,script:code.outputFiles[0].text,d1Databases:{DB:'history-isolated'},compatibilityDate:'2026-09-13'}]}));t.after(()=>mf.dispose());const DB=await mf.getD1Database('DB','history-test');
 await DB.prepare('CREATE TABLE translation_requests(user_id TEXT,request_id TEXT,reserved INTEGER,billed INTEGER,state TEXT,response_status INTEGER,expires_at INTEGER,source_language TEXT,target_language TEXT,started_at TEXT,completed_at TEXT)').run();
 await DB.prepare('CREATE TABLE usage_logs(id TEXT,user_id TEXT,chars INTEGER,src_lang TEXT,tgt_lang TEXT,created_at TEXT,cached INTEGER)').run();
 for(const user of ['alice','bob'])await DB.prepare("INSERT INTO translation_requests VALUES(?,'test-request',4,2,'settled',200,1770000180,NULL,NULL,NULL,NULL)").bind(user).run();
 const r=await mf.dispatchFetch('https://test/v1/translation/history');assert.equal(r.status,200);const h=await r.json();assert.equal(h.items.length,1);assert.equal(h.items[0].status,'partial');assert.equal(h.items[0].created_at,'2026-02-02T02:40:00.000Z');
 assert.deepEqual(await(await mf.dispatchFetch('https://test/v1/translation/tasks/'+h.items[0].id)).json(),h.items[0]);
 assert.deepEqual((await(await mf.dispatchFetch('https://test/v1/translation/history?before='+encodeURIComponent(h.items[0].created_at+h.items[0].id))).json()).items,[]);
 await DB.prepare("INSERT INTO translation_requests VALUES('alice','modern-request',4,4,'settled',200,1770000180,'zh','en','2026-09-16T01:00:00.123Z','2026-09-16T01:00:02.456Z')").run();
 const modern=await(await mf.dispatchFetch('https://test/v1/translation/tasks/request_modern-request')).json();
 assert.equal(modern.created_at,'2026-09-16T01:00:00.123Z');assert.equal(modern.started_at,modern.created_at);assert.equal(modern.completed_at,'2026-09-16T01:00:02.456Z');assert.equal(modern.completion_kind,'recorded');assert.equal(modern.source_language,'zh');assert.equal(modern.target_language,'en');
 await DB.prepare("UPDATE translation_requests SET response_status=504,completed_at=strftime('%Y-%m-%dT%H:%M:%fZ',expires_at,'unixepoch') WHERE request_id='modern-request'").run();
 const expired=await(await mf.dispatchFetch('https://test/v1/translation/tasks/request_modern-request')).json();assert.equal(expired.completion_kind,'expiry_deadline');assert.equal(expired.completed_at,'2026-02-02T02:43:00.000Z');
});

