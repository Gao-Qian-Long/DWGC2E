import {test} from 'node:test';
import assert from 'node:assert/strict';
import {setup} from './helpers/worker.mjs';
import worker from '../src/index.ts';
import {captchaBinding,captchaHash,consumeCaptcha,issueCaptcha,renderCaptchaSvg} from '../src/captcha.ts';
import {allowedProviderSecrets,providerEndpoint,routeCompletion} from '../src/ai-router.ts';
import {adminAiRoute} from '../src/admin/ai.ts';
import {entitlementSnapshot} from '../src/entitlements.ts';

const adminKey='hardening-admin-key-'.repeat(3);
const json=(path,body,extra={})=>new Request('https://local.test'+path,{method:'POST',headers:{'content-type':'application/json',...extra},body:JSON.stringify(body)});
async function seedCode(db,email,purpose='register',code='123456'){
 const hash=Buffer.from(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(code+'|'+email+'|'+purpose))).toString('hex');
 db.prepare('INSERT INTO email_verification_codes(id,email,purpose,code_hash,expires_at,created_at) VALUES(?,?,?,?,?,?)')
  .run(crypto.randomUUID(),email,purpose,hash,new Date(Date.now()+600000).toISOString(),new Date().toISOString());
}
function solvedCaptcha(x,email,purpose,clientKey='unknown',code='12345'){
 return (async()=>{
  const issued=await issueCaptcha(x.env,email,purpose,clientKey);
  x.db.prepare('UPDATE numeric_captchas SET answer_hash=? WHERE id=?').run(await captchaHash(issued.captcha_id+'|'+code+'|'+x.env.PASSWORD_PEPPER),issued.captcha_id);
  return {...issued,captcha_code:code};
 })();
}

// P2-1: five wrong guesses used to burn a mailbox' verification code for free.
test('P2-1 password reset is throttled per mailbox after five wrong codes',async t=>{
 const x=setup(t);
 const id=crypto.randomUUID();
 x.db.prepare('INSERT INTO email_verification_codes(id,email,purpose,code_hash,expires_at,attempts,created_at) VALUES(?,?,?,?,?,0,?)')
  .run(id,'alice@example.com','password_reset','0'.repeat(64),new Date(Date.now()+600000).toISOString(),new Date().toISOString());
 const reset=email=>x.request('/v1/auth/password/reset',{method:'POST',body:{email,code:'000000',new_password:'BrandNewPass1!'}});
 for(let attempt=1;attempt<=5;attempt++)assert.equal((await reset('alice@example.com')).status,400,`attempt ${attempt}`);
 const blocked=await reset('alice@example.com');
 assert.equal(blocked.status,429);
 assert.equal((await blocked.json()).error_code,'rate_limited');
 assert.equal(x.db.prepare('SELECT attempts FROM email_verification_codes WHERE id=?').get(id).attempts,5);
 assert.equal(x.db.prepare("SELECT password_hash FROM users WHERE id='alice'").get().password_hash.startsWith('v2:'),true);
 // The window is per mailbox: another address is not affected by this counter.
 assert.equal((await reset('bob@example.com')).status,400);
});

test('P2-1 registration is throttled per mailbox after five wrong codes',async t=>{
 const x=setup(t);
 await seedCode(x.db,'new@example.com');
 const register=()=>x.request('/v1/auth/register',{method:'POST',body:{email:'new@example.com',account:'new-account',password:'TestPassword123!',verification_code:'000000'}});
 for(let attempt=1;attempt<=5;attempt++)assert.equal((await register()).status,400,`attempt ${attempt}`);
 assert.equal((await register()).status,429);
 assert.equal(x.db.prepare("SELECT attempts FROM email_verification_codes WHERE email='new@example.com'").get().attempts,5);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM users').get().n,2);
});

// P2-2: existence probing must not be free.
test('P2-2 unsolved probing cannot distinguish a registered mailbox from an unknown one',async t=>{
 const x=setup(t);
 const probe=email=>worker.fetch(json('/v1/auth/register/request-code',{email}),x.env);
 const known=await probe('alice@example.com'),unknown=await probe('nobody@example.com');
 assert.equal(known.status,400);
 assert.equal(unknown.status,400);
 assert.deepEqual(await known.json(),await unknown.json());
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM numeric_captchas').get().n,0);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM email_verification_codes').get().n,0);
 // A solved challenge still tells the user the mailbox is taken, so the product keeps its hint.
 const solved=await solvedCaptcha(x,'alice@example.com','register');
 const disclosed=await worker.fetch(json('/v1/auth/register/request-code',{email:'alice@example.com',captcha_id:solved.captcha_id,captcha_code:solved.captcha_code}),x.env);
 assert.equal(disclosed.status,409);
 assert.equal((await disclosed.json()).error_code,'email_exists');
});

test('P2-2 the probing endpoint is rate limited per mailbox',async t=>{
 const x=setup(t);
 const probe=()=>worker.fetch(json('/v1/auth/register/request-code',{email:'target@example.com'}),x.env);
 for(let attempt=1;attempt<=10;attempt++)assert.equal((await probe()).status,400,`attempt ${attempt}`);
 assert.equal((await probe()).status,429);
});

// P2-3: the response used to be a deterministic encoding of the answer.
test('P2-3 the rendered CAPTCHA no longer encodes the answer and keeps the glyph structure',()=>{
 const glyphs=['abcdef','bc','abged','abgcd','fgbc','afgcd','afgecd','abc','abcdefg','abfgcd'];
 const paths={a:'M3 1H15',b:'M17 3V15',c:'M17 19V31',d:'M3 33H15',e:'M1 19V31',f:'M1 3V15',g:'M3 17H15'};
 const letterOf=new Map(Object.entries(paths).map(([letter,path])=>[path,letter]));
 const mulberry32=seed=>()=>{seed|=0;seed=seed+0x6D2B79F5|0;let t=Math.imul(seed^seed>>>15,1|seed);t=t+Math.imul(t^t>>>7,61|t)^t;return (t^t>>>14)>>>0;};
 const groups=svg=>[...svg.matchAll(/<g transform="translate\(\d+,9\)[^>]*>([\s\S]*?)<\/g>/g)].map(group=>[...group[1].matchAll(/d="([^"]+)"/g)].map(path=>letterOf.get(path[1])||'?').join(''));
 // The reported attack: read the stroke sequence and look the digit up in the published table.
 let decoded=0;
 for(let index=0;index<100;index++){
  const code=String((index*7919)%100000).padStart(5,'0');
  const strokes=groups(renderCaptchaSvg(code,mulberry32(index+1)));
  const guess=strokes.map(stroke=>glyphs.indexOf(stroke)).map(digit=>digit<0?'?':String(digit)).join('');
  if(guess===code)decoded++;
 }
 assert.ok(decoded<=20,`offline stroke decoding recovered ${decoded}/100 images`);
 assert.notEqual(renderCaptchaSvg('12345',mulberry32(1)),renderCaptchaSvg('12345',mulberry32(2)));
 const rendered=renderCaptchaSvg('11111',mulberry32(7));
 assert.equal(groups(rendered).length,5);
 assert.ok(!rendered.includes('<text'));
 let noise=0;
 for(let index=0;index<50;index++)if(groups(renderCaptchaSvg('11111',mulberry32(100+index))).some(strokes=>strokes.length>2))noise++;
 assert.ok(noise>10,`expected displaced noise strokes in most renders, saw ${noise}/50`);
});

// P2-4 / P2-6: a secret name is a capability, and the module authenticates its own caller.
test('P2-4 provider secret names are restricted to the operator allowlist and never sent',async t=>{
 const x=setup(t);
 assert.ok(allowedProviderSecrets({}).has('DEEPSEEK_API_KEY'));
 for(const forbidden of ['EZFPY_KEY','ADMIN_API_KEY','AI_CONFIG_ENCRYPTION_KEY'])
  assert.equal(allowedProviderSecrets({}).has(forbidden),false,forbidden);
 const call=body=>adminAiRoute(new Request('https://local.test/v1/admin/ai/providers',{method:'POST',headers:{authorization:'Bearer '+adminKey,'content-type':'application/json'},body:JSON.stringify(body)}),{DB:x.DB,ADMIN_API_KEY:adminKey});
 const rejected=await call({name:'Exfil',base_url:'https://evil.example/v1',model:'m',secret_name:'EZFPY_KEY'});
 assert.equal(rejected.status,400);
 const message=await rejected.text();
 assert.ok(!message.includes('DEEPSEEK_API_KEY'),'the error must not list the allowed names');
 assert.equal((await call({name:'Allowed',base_url:'https://api.deepseek.com',model:'deepseek-chat',secret_name:'DEEPSEEK_API_KEY'})).status,201);
 // Even a row that already references another secret must not be able to read it.
 const stamp=new Date().toISOString();
 x.db.prepare("INSERT INTO ai_providers(id,name,base_url,model,secret_name,enabled,weight,priority,timeout_ms,max_failures,cooldown_seconds,temperature,revision,created_at,updated_at) VALUES('exfil','Exfil','https://evil.example/v1','m','EZFPY_KEY',1,100,100,9000,3,60,0.1,1,?,?)").run(stamp,stamp);
 x.db.exec("UPDATE ai_providers SET enabled=0 WHERE secret_name='DEEPSEEK_API_KEY'");
 x.env.EZFPY_KEY='payment-signing-key';
 let calls=0;
 t.mock.method(globalThis,'fetch',async()=>{calls++;return Response.json({});});
 await assert.rejects(()=>routeCompletion(x.env,'secret-allowlist-probe',{}),/provider_key_missing/);
 assert.equal(calls,0);
});

test('P2-6 the AI administration module authenticates its own caller',async t=>{
 const x=setup(t);
 assert.equal((await adminAiRoute(new Request('https://local.test/v1/admin/ai/providers'),{DB:x.DB})).status,401);
 assert.equal((await adminAiRoute(new Request('https://local.test/v1/admin/ai/providers'),{DB:x.DB,ADMIN_API_KEY:adminKey})).status,401);
 assert.equal((await adminAiRoute(new Request('https://local.test/v1/admin/ai/providers',{headers:{authorization:'Bearer '+adminKey}}),{DB:x.DB,ADMIN_API_KEY:adminKey})).status,200);
 assert.equal((await adminAiRoute(new Request('https://local.test/v1/admin/ai/history'),{DB:x.DB,ADMIN_API_KEY:adminKey})).status,401);
});

test('P2-4 provider endpoints reject hidden queries, IP literals and hosts outside the allowlist',()=>{
 assert.equal(providerEndpoint('https://api.deepseek.com'),'https://api.deepseek.com/chat/completions');
 assert.equal(providerEndpoint('https://api.deepseek.com',['api.deepseek.com']),'https://api.deepseek.com/chat/completions');
 for(const value of ['https://api.example/v1?token=secret','https://1.2.3.4/v1','https://[2606:4700::1]/v1','https://evil.example/v1','http://api.deepseek.com'])
  assert.throws(()=>providerEndpoint(value,['api.deepseek.com']),/invalid_provider_url/,value);
 assert.throws(()=>providerEndpoint('https://api.deepseek.com/v1#fragment'),/invalid_provider_url/);
});

// P2-5: a challenge is bound to the client that requested it.
test('P2-5 a CAPTCHA is bound to the requesting client and only one challenge stays open',async t=>{
 const x=setup(t);
 const issued=await issueCaptcha(x.env,'alice@example.com','register','203.0.113.9');
 x.db.prepare('UPDATE numeric_captchas SET answer_hash=? WHERE id=?').run(await captchaHash(issued.captcha_id+'|12345|'+x.env.PASSWORD_PEPPER),issued.captcha_id);
 assert.equal(await consumeCaptcha(x.env,'alice@example.com','register',issued.captcha_id,'12345','198.51.100.7'),false,'a different client cannot spend the challenge');
 const second=await issueCaptcha(x.env,'alice@example.com','register','198.51.100.7');
 x.db.prepare('UPDATE numeric_captchas SET answer_hash=? WHERE id=?').run(await captchaHash(second.captcha_id+'|12345|'+x.env.PASSWORD_PEPPER),second.captcha_id);
 assert.equal(await consumeCaptcha(x.env,'alice@example.com','register',second.captcha_id,'12345','198.51.100.7'),true);
 const first=await issueCaptcha(x.env,'bob@example.com','register','203.0.113.9');
 await issueCaptcha(x.env,'bob@example.com','register','203.0.113.9');
 const bobBinding=await captchaBinding('bob@example.com','register','203.0.113.9');
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM numeric_captchas WHERE used=0 AND binding=?').get(bobBinding).n,1);
 assert.equal(await consumeCaptcha(x.env,'bob@example.com','register',first.captcha_id,'12345','203.0.113.9'),false);
 assert.equal((await captchaBinding('a@example.com','register','1.2.3.4'))!==(await captchaBinding('a@example.com','register','5.6.7.8')),true);
});

// P2-7: an action taken from the browser session must not be recorded as the API key.
test('P2-7 administrator sessions are audited as sessions, not as the operator key',async t=>{
 const x=setup(t);x.env.ADMIN_API_KEY=adminKey;
 const login=await worker.fetch(json('/v1/admin/session',{username:'admin'},{authorization:'Bearer '+adminKey}),x.env);
 assert.equal(login.status,200);
 const cookie=login.headers.get('set-cookie').split(';')[0];
 const revision=x.db.prepare("SELECT revision FROM plan_catalog WHERE id='pro'").get().revision;
 const write=await worker.fetch(json('/v1/admin/plans/pro',{price_cents:3900,quota:1000000,duration_days:30,enabled:1,revision,reason:'审计主体测试',request_id:crypto.randomUUID()},{cookie,origin:'https://cad.pocketter.dpdns.org'}),x.env);
 assert.equal(write.status,200);
 const change=x.db.prepare('SELECT actor FROM plan_changes_v3 ORDER BY created_at DESC,id DESC LIMIT 1').get();
 assert.match(change.actor,/^session:[a-f0-9]{16}$/);
 const keyed=await worker.fetch(json('/v1/admin/plans/pro',{price_cents:3900,quota:1000000,duration_days:30,enabled:1,revision:revision+1,reason:'密钥主体测试',request_id:crypto.randomUUID()},{authorization:'Bearer '+adminKey}),x.env);
 assert.equal(keyed.status,200);
 assert.match(x.db.prepare('SELECT actor FROM plan_changes_v3 ORDER BY created_at DESC,id DESC LIMIT 1').get().actor,/^key:[a-f0-9]{16}$/);
});

// P2-8: the administrator sign-in ceremony had no budget at all.
test('P2-8 repeated administrator sign-in attempts are throttled',async t=>{
 const x=setup(t);x.env.ADMIN_API_KEY=adminKey;
 const wrong='wrong-admin-key-'.repeat(3);
 const attempt=()=>worker.fetch(json('/v1/admin/session',{username:'admin'},{authorization:'Bearer '+wrong}),x.env);
 for(let index=1;index<=10;index++)assert.equal((await attempt()).status,401,`attempt ${index}`);
 assert.equal((await attempt()).status,429);
 assert.equal((await worker.fetch(json('/v1/admin/session',{username:'admin'},{authorization:'Bearer '+adminKey}),x.env)).status,429);
});

// P2-9: the compensation ledger arrives with a later migration than this module.
test('P2-9 entitlement snapshots degrade instead of failing when the compensation ledger is absent',async t=>{
 const x=setup(t);
 x.db.exec('DROP TABLE quota_compensations_v3');
 const degraded=await entitlementSnapshot(x.env,'alice');
 assert.equal(degraded.usage.compensation_quota,0);
 assert.equal(degraded.usage.base_quota,100000);
 x.db.prepare('INSERT INTO quota_compensations(id,user_id,year_month,amount,actor,reason,created_at) VALUES(?,?,?,?,?,?,?)')
  .run(crypto.randomUUID(),'alice',new Date().toISOString().slice(0,7),500,'actor','legacy compensation ledger',new Date().toISOString());
 const legacy=await entitlementSnapshot(x.env,'alice');
 assert.equal(legacy.usage.compensation_quota,500);
 assert.equal(legacy.usage.base_quota,100500);
});

// P2-17: a failed provider read must not look like "no provider configured".
test('P2-17 a provider read failure is reported instead of silently using the built-in provider',async t=>{
 const x=setup(t);
 const prepare=x.DB.prepare.bind(x.DB);
 x.DB.prepare=sql=>{if(/LEFT JOIN ai_provider_health/.test(sql))throw new Error('d1_unavailable');return prepare(sql);};
 let calls=0;
 t.mock.method(globalThis,'fetch',async()=>{calls++;return Response.json({choices:[{message:{content:'[]'}}]});});
 await assert.rejects(()=>routeCompletion(x.env,'provider-read-failure-1',{}),/ai_provider_read_failed/);
 assert.equal(calls,0);
});

test('P2-17 the built-in fallback is announced when no provider is configured',async t=>{
 const x=setup(t);
 const logs=[];
 t.mock.method(console,'error',message=>logs.push(String(message)));
 t.mock.method(globalThis,'fetch',async()=>Response.json({choices:[{message:{content:JSON.stringify([{id:0,translated_text:'ok'}])}}]}));
 await routeCompletion(x.env,'builtin-fallback-0001',{items:[]});
 assert.ok(logs.some(line=>line.includes('ai_builtin_fallback')),'degradation must be visible to operators');
});

// P3-10: registration accepted an unbounded display name.
test('P3-10 registration enforces the profile display-name limit',async t=>{
 const x=setup(t);
 await seedCode(x.db,'new@example.com');
 const register=display_name=>worker.fetch(json('/v1/auth/register',{email:'new@example.com',account:'new-account',password:'TestPassword123!',verification_code:'123456',...display_name===undefined?{}:{display_name}}),x.env);
 assert.equal((await register('x'.repeat(81))).status,400);
 assert.equal((await register(12345)).status,400);
 assert.equal((await register('   ')).status,400);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM users').get().n,2);
 assert.equal((await register('Normal Name')).status,201);
 assert.equal(x.db.prepare("SELECT display_name FROM users WHERE email='new@example.com'").get().display_name,'Normal Name');
});
