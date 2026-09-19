import {test} from 'node:test';
import assert from 'node:assert/strict';
import {setup} from './helpers/worker.mjs';

const routes = [
 ['/v1/profile','GET'], ['/v1/profile','PATCH',{display_name:'unauthorized'}],
 ['/v1/glossary','GET'], ['/v1/glossary','PUT',{entries:[]}],
 ['/v1/translation/history','GET'], ['/v1/devices','GET'],
 ['/v1/devices/revoke','POST',{device_id:'install-1'}],
 ['/v1/auth/password','PATCH',{current_password:'ExamplePassword1!',new_password:'NoChange123!'}],
 ['/v1/billing/orders','GET'], ['/v1/translate','POST',{items:[{id:1,text:'Motor'}],source_lang:'en',target_lang:'de'}]
];
function isolated(t) {
 const x=setup(t);
 t.mock.method(globalThis,'fetch',()=>{throw new Error('External network forbidden in session acceptance');});
 return x;
}
async function assertRejectedEverywhere(x,token) {
 for(const [path,method,body] of routes) assert.equal((await x.request(path,{method,body,token})).status,401,`${method} ${path}`);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM translation_requests').get().n,0);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM user_glossaries').get().n,0);
}
for(const [name,sql] of [
 ['expired',"UPDATE sessions SET expires_at='2000-01-01T00:00:00Z'"],
 ['malformed expiry',"UPDATE sessions SET expires_at='not-a-date'"],
 ['revoked',"UPDATE sessions SET revoked_at='2026-09-16T00:00:00Z'"],
 ['missing client context','DELETE FROM session_contexts'],
 ['disabled account',"UPDATE users SET is_active=0 WHERE id='alice'"],
 ['revoked APP binding',"UPDATE app_device_bindings SET first_seen='2000-01-01'; UPDATE app_device_bindings SET revoked=1"]
]) test(`invalid session (${name}) fails closed across protected reads and writes`,async t=>{
 const x=isolated(t),login=await x.login();assert.equal(login.status,200);
 x.db.exec(sql);await assertRejectedEverywhere(x,login.token);
});

async function seedCode(x,{expired=false,purpose='password_reset'}={}) {
 const email='alice@example.com',code='123456',id=crypto.randomUUID();
 const hash=Buffer.from(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(`${code}|${email}|${purpose}`))).toString('hex');
 x.db.prepare('INSERT INTO email_verification_codes(id,email,purpose,code_hash,expires_at,created_at) VALUES(?,?,?,?,?,?)')
  .run(id,email,purpose,hash,new Date(Date.now()+(expired?-600000:600000)).toISOString(),new Date().toISOString());
 return {id,email,code,new_password:'ResetPassword123!'};
}
async function reset(x,body){return x.request('/v1/auth/password/reset',{method:'POST',body});}
async function loginWith(x,password){return x.request('/v1/auth/web/login',{method:'POST',body:{account:'alice',password}});}

test('password reset revokes all APP/web sessions, preserves other account and requires new password',async t=>{
 const x=isolated(t),app=await x.login(),web=await x.login('web'),bob=await x.login('web','','bob');
 const body=await seedCode(x);assert.equal((await reset(x,body)).status,200);
 await assertRejectedEverywhere(x,app.token);await assertRejectedEverywhere(x,web.token);
 assert.equal((await x.request('/v1/profile',{token:bob.token})).status,200);
 assert.equal((await loginWith(x,x.password)).status,401);
 const fresh=await loginWith(x,body.new_password);assert.equal(fresh.status,200);
 assert.equal((await x.request('/v1/profile',{token:(await fresh.json()).token})).status,200);
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM app_device_bindings WHERE user_id='alice' AND revoked=0").get().n,1);
 assert.equal((await reset(x,body)).status,400,'reset code cannot be replayed');
});
for(const mode of ['wrong','expired','wrong purpose']) test(`invalid reset (${mode}) preserves credentials and active session`,async t=>{
 const x=isolated(t),app=await x.login(),before=x.db.prepare("SELECT password_hash FROM users WHERE id='alice'").get().password_hash;
 const body=await seedCode(x,{expired:mode==='expired',purpose:mode==='wrong purpose'?'register':'password_reset'});
 if(mode==='wrong')body.code='654321';
 assert.equal((await reset(x,body)).status,400);
 assert.equal(x.db.prepare("SELECT password_hash FROM users WHERE id='alice'").get().password_hash,before);
 assert.equal((await x.request('/v1/profile',{token:app.token})).status,200);
 assert.equal(x.db.prepare('SELECT used_at FROM email_verification_codes WHERE id=?').get(body.id).used_at,null);
});
test('concurrent reset with one code has one winner, and losing password is never installed',async t=>{
 const x=isolated(t),app=await x.login(),body=await seedCode(x);
 const passwords=['WinnerCandidate1!','WinnerCandidate2!'];
 const responses=await Promise.all(passwords.map(new_password=>reset(x,{...body,new_password})));
 assert.deepEqual(responses.map(r=>r.status).sort(),[200,400]);
 const winner=responses.findIndex(r=>r.status===200);
 assert.equal((await loginWith(x,passwords[winner])).status,200);
 assert.equal((await loginWith(x,passwords[1-winner])).status,401);
 assert.equal((await x.request('/v1/profile',{token:app.token})).status,401);
});
test('password-reset transaction failure rolls back code consumption, password and revocation',async t=>{
 const x=isolated(t),app=await x.login(),body=await seedCode(x);
 const before=x.db.prepare("SELECT password_hash FROM users WHERE id='alice'").get().password_hash;
 x.db.exec("CREATE TRIGGER fail_session_revoke BEFORE UPDATE OF revoked_at ON sessions BEGIN SELECT RAISE(ABORT,'controlled revoke failure'); END;");
 // The public handler must report failure without leaking database diagnostics.
 const failed=await reset(x,body); assert.equal(failed.status,500);
 assert.doesNotMatch(await failed.text(),/controlled revoke failure/);
 assert.equal(x.db.prepare("SELECT password_hash FROM users WHERE id='alice'").get().password_hash,before);
 assert.equal(x.db.prepare('SELECT used_at FROM email_verification_codes WHERE id=?').get(body.id).used_at,null);
 assert.equal((await x.request('/v1/profile',{token:app.token})).status,200);
 x.db.exec('DROP TRIGGER fail_session_revoke');
 assert.equal((await reset(x,body)).status,200);
 assert.equal((await x.request('/v1/profile',{token:app.token})).status,401);
});

