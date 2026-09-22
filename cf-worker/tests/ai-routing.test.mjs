import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {setup} from './helpers/worker.mjs';
import worker from '../src/index.ts';
import {routeCompletion,stableProviderOrder,translationContext} from '../src/ai-router.ts';

const masterKey=Buffer.alloc(32,17).toString('base64');
const adminKey='admin-test-key-'.padEnd(40,'x');
const completion=text=>Response.json({choices:[{message:{content:JSON.stringify([{id:0,translated_text:text}])}}]});

function adminRequest(x,path,{method='GET',body}={}){
 return worker.fetch(new Request('https://local.test'+path,{method,headers:{authorization:'Bearer '+adminKey,'content-type':'application/json'},...(body===undefined?{}:{body:JSON.stringify(body)})}),x.env);
}
function seedProvider(x,{id='provider-a',name=id,base='https://a.example/v1',model='model-a',secret='PROVIDER_A_KEY',enabled=1,weight=100,priority=100,maxFailures=3,cooldown=60}={}){
 const timestamp=new Date().toISOString();
 x.db.prepare(`INSERT INTO ai_providers(id,name,base_url,model,secret_name,enabled,weight,priority,timeout_ms,max_failures,cooldown_seconds,temperature,revision,created_at,updated_at)
 VALUES(?,?,?,?,?,?,?,?,5000,?,?,0.1,1,?,?)`).run(id,name,base,model,secret,enabled,weight,priority,maxFailures,cooldown,timestamp,timestamp);
 x.env[secret]='test-secret-'+id;
 // A Worker Secret is only readable by the router when the operator names it explicitly.
 x.env.AI_PROVIDER_SECRET_ALLOWLIST=[...(x.env.AI_PROVIDER_SECRET_ALLOWLIST||'').split(',').filter(Boolean),secret].join(',');
}

function configured(t){
 const x=setup(t);
 x.env.ADMIN_API_KEY=adminKey;
 x.env.AI_CONFIG_ENCRYPTION_KEY=masterKey;
 // Provider egress is host-allowlisted; without this the built-in DeepSeek-only default would
 // reject every *.example fixture used below.
 x.env.AI_PROVIDER_HOST_ALLOWLIST='example';
 return x;
}

test('admin provider CRUD encrypts credentials, never returns keys, probes the real endpoint and enforces revisions',async t=>{
 const x=configured(t);
 let seenAuthorization='';
 t.mock.method(globalThis,'fetch',async(_url,init)=>{seenAuthorization=new Headers(init.headers).get('authorization')||'';return completion('DWGC2E provider health check');});
 const create=await adminRequest(x,'/v1/admin/ai/providers',{method:'POST',body:{name:'Primary',base_url:'https://models.example/v1',model:'commercial-model',api_key:'provider-secret-value',weight:200,priority:10,reason:'initial provider'}});
 assert.equal(create.status,201);
 const provider=await create.json();
 assert.equal(provider.has_credential,true);
 assert.equal(provider.credential_source,'encrypted_store');
 assert.ok(!JSON.stringify(provider).includes('provider-secret-value'));
 const stored=x.db.prepare('SELECT * FROM ai_providers WHERE id=?').get(provider.id);
 assert.notEqual(stored.credential_ciphertext,'provider-secret-value');
 assert.ok(stored.credential_ciphertext&&stored.credential_nonce);
 assert.ok(!JSON.stringify(x.db.prepare('SELECT * FROM ai_config_changes').all()).includes('provider-secret-value'));
 const listed=await(await adminRequest(x,'/v1/admin/ai/providers')).json();
 assert.equal(listed.items.length,1);
 assert.ok(!JSON.stringify(listed).includes('provider-secret-value'));
 const probe=await adminRequest(x,`/v1/admin/ai/providers/${provider.id}/test`,{method:'POST',body:{}});
 assert.equal(probe.status,200);
 const probeBody=await probe.json();
 assert.equal(probeBody.success,true);
 assert.ok(Number.isInteger(probeBody.latency_ms));
 assert.ok(!Object.hasOwn(probeBody,'content'));
 assert.equal(seenAuthorization,'Bearer provider-secret-value');
 const stale=await adminRequest(x,`/v1/admin/ai/providers/${provider.id}`,{method:'PUT',body:{revision:99,model:'other'}});
 assert.equal(stale.status,409);
 const update=await adminRequest(x,`/v1/admin/ai/providers/${provider.id}`,{method:'PUT',body:{revision:1,model:'commercial-model-v2',reason:'upgrade model'}});
 assert.equal(update.status,200);
 assert.equal((await update.json()).revision,2);
 const conflict=await adminRequest(x,`/v1/admin/ai/providers/${provider.id}`,{method:'DELETE',body:{revision:1,reason:'stale disable'}});
 assert.equal(conflict.status,409);
});

test('encrypted credentials fail closed with missing or wrong master keys and private endpoints are rejected',async t=>{
 const x=configured(t);
 const privateUrl=await adminRequest(x,'/v1/admin/ai/providers',{method:'POST',body:{name:'Private',base_url:'https://127.0.0.1/v1',model:'m',secret_name:'PRIVATE_KEY'}});
 assert.equal(privateUrl.status,400);
 const create=await adminRequest(x,'/v1/admin/ai/providers',{method:'POST',body:{name:'Encrypted',base_url:'https://safe.example/v1',model:'m',api_key:'long-enough-secret'}});
 assert.equal(create.status,201);
 let calls=0;
 t.mock.method(globalThis,'fetch',async()=>{calls++;return completion('ok');});
 x.env.AI_CONFIG_ENCRYPTION_KEY=Buffer.alloc(32,18).toString('base64');
 await assert.rejects(()=>routeCompletion(x.env,'wrong-key-request',{}),/provider_key_decrypt_failed/);
 assert.equal(calls,0);
 delete x.env.AI_CONFIG_ENCRYPTION_KEY;
 await assert.rejects(()=>routeCompletion(x.env,'missing-key-request',{}),/ai_encryption_key_missing/);
 assert.equal(calls,0);
});

test('weighted ordering is deterministic and favors higher weight without bypassing priority',()=>{
 const providers=[
  {id:'heavy',priority:20,weight:9},
  {id:'light',priority:20,weight:1},
  {id:'preferred-priority',priority:10,weight:1}
 ];
 assert.deepEqual(stableProviderOrder(providers,'same-request').map(x=>x.id),stableProviderOrder(providers,'same-request').map(x=>x.id));
 for(let i=0;i<100;i++)assert.equal(stableProviderOrder(providers,'request-'+i)[0].id,'preferred-priority');
 let heavy=0,light=0;
 for(let i=0;i<1000;i++){
  const first=stableProviderOrder(providers.filter(x=>x.priority===20),'weighted-'+i)[0].id;
  if(first==='heavy')heavy++;else light++;
 }
 assert.ok(heavy>light*5,{heavy,light});
});

test('router fails over, opens the circuit at the configured threshold, and skips cooling providers',async t=>{
 const x=configured(t);
 delete x.env.DEEPSEEK_API_KEY;
 seedProvider(x,{id:'primary',base:'https://primary.example/v1',secret:'PRIMARY_KEY',priority:10,maxFailures:1,cooldown:300});
 seedProvider(x,{id:'secondary',base:'https://secondary.example/v1',secret:'SECONDARY_KEY',priority:20});
 const calls=[];
 t.mock.method(globalThis,'fetch',async url=>{calls.push(String(url));return String(url).includes('primary.example')?new Response('failed',{status:503}):completion('ok');});
 const first=await routeCompletion(x.env,'failover-request-1',{items:[{id:0,text:'x'}]});
 assert.match(first.contextVersion,/^builtin-v1\.[a-f0-9]{12}$/);
 assert.equal(calls.length,3);
 const health=x.db.prepare("SELECT * FROM ai_provider_health WHERE provider_id='primary'").get();
 assert.equal(health.consecutive_failures,1);
 assert.ok(Date.parse(health.cooldown_until)>Date.now());
 await routeCompletion(x.env,'failover-request-2',{items:[{id:0,text:'x'}]});
 assert.equal(calls.filter(x=>x.includes('primary.example')).length,2);
 assert.equal(calls.filter(x=>x.includes('secondary.example')).length,2);
});

test('all configured providers failing refunds quota and idempotent replay does not call them again',async t=>{
 const x=configured(t),auth=await x.login('app');
 delete x.env.DEEPSEEK_API_KEY;
 seedProvider(x,{id:'broken',base:'https://broken.example/v1',secret:'BROKEN_KEY',priority:1});
 let calls=0;
 t.mock.method(globalThis,'fetch',async()=>{calls++;return new Response('unavailable',{status:503});});
 const request=()=>worker.fetch(new Request('https://local.test/v1/translate',{method:'POST',headers:{authorization:'Bearer '+auth.token,'content-type':'application/json','Idempotency-Key':'configured-failure-01'},body:JSON.stringify({source_lang:'zh',target_lang:'en',items:[{id:0,text:'法兰'}]})}),x.env);
 assert.equal((await request()).status,502);
 assert.equal((await request()).status,502);
 assert.equal(calls,2);
 const row=x.db.prepare("SELECT billed,state,response_status FROM translation_requests WHERE request_id='configured-failure-01'").get();
 assert.equal(row.billed,0);assert.equal(row.state,'settled');assert.equal(row.response_status,502);
 assert.equal(x.db.prepare("SELECT chars_used FROM usage_monthly WHERE user_id='alice'").get().chars_used,0);
});

test('publishing and rollback expose stable context versions and reject stale administrator state',async t=>{
 const x=configured(t),auth=await x.login('app');
 seedProvider(x,{id:'published-provider',base:'https://published.example/v1',secret:'PUBLISHED_KEY'});
 const publishA=await adminRequest(x,'/v1/admin/ai/publish',{method:'POST',body:{name:'Policy A',context_version:'ctx-a',system_prompt:'Translate CAD text and return only the required structured JSON without altering protected engineering values.',reason:'publish initial policy',expected_active_profile_id:''}});
 assert.equal(publishA.status,200);
 const profileA=x.db.prepare("SELECT id FROM ai_routing_profiles WHERE context_version='ctx-a'").get().id;
 const contextA=await(await x.request('/v1/translation-context',{token:auth.token})).json();
 assert.match(contextA.context_version,/^ctx-a\.[a-f0-9]{12}$/);
 assert.equal(contextA.context_version,await translationContext(x.env));
 const publishB=await adminRequest(x,'/v1/admin/ai/publish',{method:'POST',body:{name:'Policy B',context_version:'ctx-b',system_prompt:'Translate technical drawing labels and return only strict JSON while preserving all protected terminology and numeric values.',reason:'publish revised policy',expected_active_profile_id:profileA}});
 assert.equal(publishB.status,200);
 const profileB=x.db.prepare("SELECT id FROM ai_routing_profiles WHERE context_version='ctx-b'").get().id;
 const stale=await adminRequest(x,'/v1/admin/ai/publish',{method:'POST',body:{context_version:'ctx-c',system_prompt:'Translate technical drawing labels and return only strict JSON while preserving all protected terminology and numeric values.',reason:'stale publish attempt',expected_active_profile_id:profileA}});
 assert.equal(stale.status,409);
 const rollback=await adminRequest(x,'/v1/admin/ai/rollback',{method:'POST',body:{profile_id:profileA,reason:'rollback after validation',expected_active_profile_id:profileB}});
 assert.equal(rollback.status,200);
 assert.match((await rollback.json()).context_version,/^ctx-a$/);
 assert.match(await translationContext(x.env),/^ctx-a\.[a-f0-9]{12}$/);
 const duplicate=await adminRequest(x,'/v1/admin/ai/publish',{method:'POST',body:{context_version:'ctx-a',system_prompt:'Translate technical drawing labels and return only strict JSON while preserving all protected terminology and numeric values.',reason:'duplicate context version',expected_active_profile_id:profileA}});
 assert.equal(duplicate.status,409);
});

test('provider revisions change checkpoint context while health telemetry does not',async t=>{
 const x=configured(t);
 seedProvider(x,{id:'context-provider',base:'https://context.example/v1',secret:'CONTEXT_KEY'});
 const before=await translationContext(x.env);
 x.db.prepare("UPDATE ai_providers SET model='model-b',revision=revision+1 WHERE id='context-provider'").run();
 const changed=await translationContext(x.env);
 assert.notEqual(changed,before);
 const timestamp=new Date().toISOString();
 x.db.prepare("INSERT INTO ai_provider_health(provider_id,consecutive_failures,last_success_at,updated_at) VALUES('context-provider',0,?,?)").run(timestamp,timestamp);
 assert.equal(await translationContext(x.env),changed);
});

test('AI provider migration is repeatable and preserves configured rows',t=>{
 const x=configured(t);
 seedProvider(x,{id:'migration-provider',base:'https://migration.example/v1',secret:'MIGRATION_KEY'});
 const sql=readFileSync(new URL('../migrations/0021_ai_provider_routing.sql',import.meta.url),'utf8');
 x.db.exec(sql);
 x.db.exec(sql);
 assert.equal(x.db.prepare("SELECT model FROM ai_providers WHERE id='migration-provider'").get().model,'model-a');
});

test('provider shuffle is seeded per drawing so one drawing never mixes models across batches',async t=>{
 const x=configured(t);
 delete x.env.DEEPSEEK_API_KEY;
 // Equal priority, so the weighted shuffle alone decides which provider a batch starts on.
 seedProvider(x,{id:'alpha',base:'https://alpha.example/v1',secret:'ALPHA_KEY',priority:10,weight:3});
 seedProvider(x,{id:'beta',base:'https://beta.example/v1',secret:'BETA_KEY',priority:10,weight:3});
 seedProvider(x,{id:'gamma',base:'https://gamma.example/v1',secret:'GAMMA_KEY',priority:10,weight:2});
 seedProvider(x,{id:'delta',base:'https://delta.example/v1',secret:'DELTA_KEY',priority:10,weight:1});
 const calls=[];
 t.mock.method(globalThis,'fetch',async url=>{calls.push(new URL(String(url)).host);return completion('ok');});
 const firstHost=async(requestId,payload)=>{await routeCompletion(x.env,requestId,payload);return calls.at(-1);};
 const drawing='drawing-task-0001';
 const chosen=await firstHost('batch-request-01',{billing_task_id:drawing,items:[{id:0,text:'x'}]});
 // Every batch of one drawing must start on the same provider, otherwise a single drawing gets
 // translated by several models and its wording drifts mid-document.
 for(const batch of ['batch-request-02','batch-request-03','batch-request-04','batch-request-05'])
  assert.equal(await firstHost(batch,{billing_task_id:drawing,items:[{id:0,text:'x'}]}),chosen);
 // The seed is the drawing id alone: passing it as the request id must agree, which also pins the
 // backward-compatible fallback for clients that still omit billing_task_id.
 assert.equal(await firstHost(drawing,{items:[{id:0,text:'x'}]}),chosen);
 const spread=new Set([chosen]);
 for(const task of ['drawing-task-0002','drawing-task-0003','drawing-task-0004','drawing-task-0005','drawing-task-0006','drawing-task-0007'])
  spread.add(await firstHost('batch-request-01',{billing_task_id:task,items:[{id:0,text:'x'}]}));
 assert.ok(spread.size>1,{spread:[...spread]});
});
