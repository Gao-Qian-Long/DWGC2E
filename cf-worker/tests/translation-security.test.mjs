import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
const {default:worker} = await import(process.env.WORKER_CANDIDATE || '../src/index.ts');
async function setup(t, answer) {
 const db=new DatabaseSync(':memory:');db.exec(readFileSync(new URL('../schema.sql',import.meta.url),'utf8'));t.after(()=>db.close());
 const DB={prepare(sql){return {sql,values:[],bind(...v){this.values=v;return this;},async first(){return db.prepare(sql).get(...this.values)||null;},async all(){return {results:db.prepare(sql).all(...this.values)};},async run(){return {meta:{changes:Number(db.prepare(sql).run(...this.values).changes)}};}};},async batch(statements){db.exec('BEGIN');try{const results=statements.map(s=>({meta:{changes:Number(db.prepare(s.sql).run(...s.values).changes)}}));db.exec('COMMIT');return results;}catch(e){db.exec('ROLLBACK');throw e;}}};
 const ts=new Date().toISOString(),expiry=new Date(Date.now()+86400000).toISOString();
 db.prepare('INSERT INTO users(id,account,password_hash,email,created_at) VALUES(?,?,?,?,?)').run('u','test','unused','test@example.com',ts);
 db.prepare('INSERT INTO devices(device_id,user_id,first_seen,last_seen) VALUES(?,?,?,?)').run('d','u',ts,ts);
 const tokenHash=Buffer.from(await crypto.subtle.digest('SHA-256',new TextEncoder().encode('token'))).toString('hex');
 db.prepare('INSERT INTO sessions(id,user_id,token_hash,device_id,expires_at,created_at) VALUES(?,?,?,?,?,?)').run('s','u',tokenHash,'d',expiry,ts);
 db.exec("INSERT INTO app_device_bindings(user_id,device_id,first_seen,last_seen) VALUES('u','d','',''); INSERT INTO session_contexts VALUES('s','app');");
 const calls=[];t.mock.method(globalThis,'fetch',async(url,init)=>{calls.push(JSON.parse(init.body));const value=typeof answer==='function'?await answer(JSON.parse(init.body)):answer;return Response.json({choices:[{message:{content:JSON.stringify(value)}}]});});
 const env={DB,PASSWORD_PEPPER:'test',DEFAULT_PLAN:'free',DEEPSEEK_API_KEY:'fake'};
 const payload={source_lang:'ZH',target_lang:'EN',items:[{id:0,text:'法兰'},{id:1,text:'管道'}]};
 const post=(key='request-0001',body=payload)=>worker.fetch(new Request('https://test/v1/translate',{method:'POST',headers:{authorization:'Bearer token','content-type':'application/json','Idempotency-Key':key},body:JSON.stringify(body)}),env);
 const usage=()=>db.prepare('SELECT chars_used,task_count FROM usage_monthly').get();
 return {db,env,calls,post,payload,usage};
}
test('malformed upstream array refunds reservation and replays without another call',async t=>{const x=await setup(t,[null]);assert.equal((await x.post()).status,502);assert.equal(x.usage().chars_used,0);assert.equal((await x.post()).status,502);assert.equal(x.calls.length,1);});
test('partial upstream response bills successful items only and replays once',async t=>{const x=await setup(t,[{id:0,translated_text:'Flange'}]);const r=await (await x.post()).json();assert.equal(r.characters_used,2);assert.equal(r.items[1].error_code,'missing_result');assert.equal(x.usage().chars_used,2);assert.deepEqual(await (await x.post()).json(),r);assert.equal(x.usage().task_count,1);assert.equal(x.calls.length,1);});
test('duplicate IDs and changed engineering numbers remain unbilled',async t=>{const x=await setup(t,[{id:0,translated_text:'Flange 20'},{id:1,translated_text:'Pipe'},{id:1,translated_text:'Pipe'}]);x.payload.items.push({id:2,text:'阀门'});x.payload.items[0].text='法兰 10';const r=await(await x.post()).json();assert.equal(r.characters_used,0);assert.deepEqual(r.items.map(i=>i.error_code),['protected_value_changed','duplicate_result','missing_result']);assert.equal(x.usage().chars_used,0);});
test('request ID cannot be reused for different input',async t=>{const x=await setup(t,[{id:0,translated_text:'Flange'}]);await x.post();x.payload.target_lang='FR';assert.equal((await x.post()).status,409);assert.equal(x.calls.length,1);});
test('concurrent duplicate requests run upstream once',async t=>{let release;const gate=new Promise(r=>release=r);const x=await setup(t,async()=>{await gate;return [{id:0,translated_text:'Flange'}];});const first=x.post();while(!x.calls.length)await new Promise(r=>setTimeout(r,1));const second=await x.post();assert.equal(second.status,409);assert.equal((await second.json()).error_code,'request_in_progress');release();assert.equal((await first).status,200);assert.equal(x.calls.length,1);assert.equal(x.usage().chars_used,2);});
test('quota guard rejects before upstream and never overdraws',async t=>{const x=await setup(t,[]);x.db.prepare('INSERT INTO usage_monthly(user_id,year_month,chars_used,chars_quota) VALUES(?,?,?,?)').run('u',new Date().toISOString().slice(0,7),99999,100000);assert.equal((await x.post()).status,402);assert.equal(x.calls.length,0);assert.equal(x.usage().chars_used,99999);});
test('expired reservation is recovered before next request',async t=>{const x=await setup(t,[{id:0,translated_text:'Flange'}]);await x.post();x.db.prepare("INSERT INTO translation_requests(user_id,request_id,payload_hash,year_month,reserved,expires_at) VALUES('u','expired-key','old',?,5,0)").run(new Date().toISOString().slice(0,7));assert.equal(x.usage().chars_used,7);await x.post('request-0002');assert.equal(x.usage().chars_used,4);assert.equal(x.db.prepare("SELECT state FROM translation_requests WHERE request_id='expired-key'").get().state,'settled');});
test('glossary and flags reach structured upstream prompt',async t=>{const x=await setup(t,[{id:0,translated_text:'Flange'}]);x.payload.glossary=[{source:'法兰',target:'Flange'}];x.payload.protection={protect_models:false};await x.post();const prompt=JSON.parse(x.calls[0].messages[1].content);assert.deepEqual(prompt.glossary,[]);assert.match(prompt.items[0].text,/^__DWGTERM_/);assert.equal(prompt.protection.protect_models,false);});
test('revoked device cannot translate even with unexpired session',async t=>{const x=await setup(t,[]);x.db.exec("UPDATE app_device_bindings SET first_seen='2000-01-01'; UPDATE app_device_bindings SET revoked=1");assert.equal((await x.post()).status,401);assert.equal(x.calls.length,0);});
test('database guards reject device theft and fourth active device',async t=>{const x=await setup(t,[]);x.db.prepare('INSERT INTO users(id,account,password_hash,email,created_at) VALUES(?,?,?,?,?)').run('other','other','unused','other@example.com',new Date().toISOString());assert.throws(()=>x.db.exec("INSERT INTO devices(device_id,user_id,first_seen,last_seen) VALUES('d','other','','') ON CONFLICT(device_id) DO UPDATE SET user_id='other'"),/device_conflict/);x.db.exec("INSERT INTO devices(device_id,user_id,first_seen,last_seen) VALUES('d2','u','',''),('d3','u','','')");assert.throws(()=>x.db.exec("INSERT INTO devices(device_id,user_id,first_seen,last_seen) VALUES('d4','u','','')"),/device_limit/);});
test('offline billing rounds cumulatively, replays, and rejects mode switching',async t=>{
 const x=await setup(t,[{id:0,translated_text:'Flange'}]);
 x.payload.billing_mode='offline';x.payload.billing_task_id='drawing-0001';
 let result=await(await x.post()).json();assert.equal(result.characters_used,1);assert.equal(result.original_characters,2);assert.equal(result.billing_percent,30);
 assert.deepEqual(await(await x.post()).json(),result);assert.equal(x.calls.length,1);
 result=await(await x.post('request-0002')).json();assert.equal(result.characters_used,1);
 result=await(await x.post('request-0003')).json();assert.equal(result.characters_used,0);
 assert.equal(x.usage().chars_used,2);assert.equal(x.db.prepare('SELECT original_chars FROM translation_billing_tasks').get().original_chars,6);
 x.payload.billing_mode='online';assert.equal((await x.post('request-0004')).status,409);assert.equal(x.calls.length,3);
});
test('offline failure refunds and concurrent chunks preserve cumulative rounding',async t=>{
 const x=await setup(t,[{id:0,translated_text:'Flange'}]);x.payload.billing_mode='offline';x.payload.billing_task_id='drawing-0002';
 const results=await Promise.all(['request-1001','request-1002','request-1003','request-1004'].map(k=>x.post(k)));
 assert.ok(results.every(r=>r.status===200));assert.equal(x.usage().chars_used,3);
});
test('offline requires valid task context and browser sessions cannot claim discount',async t=>{
 const x=await setup(t,[]);x.payload.billing_mode='offline';assert.equal((await x.post()).status,400);
 x.payload.billing_task_id='drawing-0003';x.db.exec("UPDATE session_contexts SET client_kind='web'");assert.equal((await x.post()).status,403);assert.equal(x.calls.length,0);
});
test('failed offline upstream refunds discounted reservation and replay never charges',async t=>{
 const x=await setup(t,[null]);x.payload.billing_mode='offline';x.payload.billing_task_id='drawing-refund';
 assert.equal((await x.post()).status,502);assert.equal(x.usage().chars_used,0);
 assert.equal(x.db.prepare('SELECT original_chars FROM translation_billing_tasks').get().original_chars,0);
 assert.equal((await x.post()).status,502);assert.equal(x.calls.length,1);assert.equal(x.usage().chars_used,0);
});

test('glossary spans are protected upstream, restored exactly, and billed using original text',async t=>{
 const x=await setup(t,body=>JSON.parse(body.messages[1].content).items.map(i=>({id:i.id,translated_text:i.text.replace('安装','Install ')})));
 x.payload.items=[{id:0,text:'安装法兰 10'}];x.payload.glossary=[{source:'法兰',target:'Custom flange'}];x.payload.protection={glossary_first:false};
 const result=await(await x.post()).json();assert.equal(result.items[0].translated_text,'Install Custom flange 10');
 assert.equal(result.characters_used,'安装法兰 10'.length);assert.equal(x.usage().chars_used,result.characters_used);
 assert.deepEqual(await(await x.post()).json(),result);assert.equal(x.calls.length,1);
});
test('AI replacing authoritative glossary is a failed item and fully refunded',async t=>{
 const x=await setup(t,[{id:0,translated_text:'Install flange'}]);x.payload.items=[{id:0,text:'安装法兰'}];x.payload.glossary=[{source:'法兰',target:'Custom flange'}];
 const result=await(await x.post()).json();assert.equal(result.items[0].error_code,'glossary_not_preserved');assert.equal(result.characters_used,0);assert.equal(x.usage().chars_used,0);
});
test('ambiguous glossary rejected before quota reservation or upstream call',async t=>{
 const x=await setup(t,[]);x.payload.glossary=[{source:'法兰',target:'Flange'},{source:'法兰',target:'Different'}];
 assert.equal((await x.post()).status,400);assert.equal(x.calls.length,0);assert.equal(x.db.prepare('SELECT COUNT(*) AS n FROM translation_requests').get().n,0);
});
