import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';

// Exercise the real Worker fetch router. Transpile only TS syntax; keep the real mail module.
const source = readFileSync(new URL('../src/index.ts', import.meta.url), 'utf8');
const compiled = stripTypeScriptTypes(source);
const moduleSource = compiled.replace('"./payments/index.ts"',JSON.stringify(new URL('../src/payments/index.ts',import.meta.url).href)).replace('"./mail"', JSON.stringify(new URL('../src/mail.ts',import.meta.url).href));
const {default: worker} = await import('data:text/javascript;base64,' + Buffer.from(moduleSource).toString('base64'));
const schema = readFileSync(new URL('../schema.sql',import.meta.url),'utf8');
const registerCode = '/v1/auth/register/request-code';
const passwordCode = '/v1/auth/password/request-code';
function setup(t) {
  const sqlite = new DatabaseSync(':memory:'); sqlite.exec(schema); t.after(()=>sqlite.close());
  const writes=[]; const mails=[];
  const DB={
    prepare(sql) {
      const statement={sql,values:[],bind(...values){this.values=values;return this;},
        async first(){return sqlite.prepare(sql).get(...this.values) ?? null;},
        async run(){writes.push(sql);const result=sqlite.prepare(sql).run(...this.values);return {success:true,meta:{changes:Number(result.changes)}};}};
      return statement;
    },
    async batch(statements){
      writes.push('batch');sqlite.exec('BEGIN');
      try {
        const result=statements.map(x=>({meta:{changes:Number(sqlite.prepare(x.sql).run(...x.values).changes)}}));sqlite.exec('COMMIT');return result;
      } catch(error) {sqlite.exec('ROLLBACK');throw error;}
    }
  };
  t.mock.method(globalThis,'fetch',async(url,init)=>{mails.push({url,payload:JSON.parse(init.body)});return new Response(null,{status:201});});
  const env={DB,PASSWORD_PEPPER:'test-only',MAIL_PROVIDER:'round_robin',MAIL_FALLBACK_ENABLED:'true',MAIL_FROM:'DWGC2E <noreply@example.com>',BREVO_API_KEY:'fake-brevo',RESEND_API_KEY:'fake-resend'};
  const post=(path,body)=>worker.fetch(new Request('https://worker.example.com'+path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}),env);
  return {sqlite,DB,env,writes,mails,post};
}
function user(db,email='existing@example.com',account='existing',active=1) {
  db.prepare('INSERT INTO users(id,account,password_hash,email,created_at,is_active) VALUES(?,?,?,?,?,?)').run(crypto.randomUUID(),account,'untouched-password',email,new Date().toISOString(),active);
}
async function code(db,email,purpose='register',value='123456') {
  const hash=Buffer.from(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(value+'|'+email+'|'+purpose))).toString('hex');
  const id=crypto.randomUUID();
  db.prepare('INSERT INTO email_verification_codes(id,email,purpose,code_hash,expires_at,created_at) VALUES(?,?,?,?,?,?)').run(id,email,purpose,hash,new Date(Date.now()+600000).toISOString(),new Date().toISOString());
  return id;
}
const registration=(email,account='new-account')=>({email,account,password:'TestPassword123!',verification_code:'123456'});

test('registered mailbox cannot request registration email; no writes or rotation',async t=>{
  const x=setup(t);user(x.sqlite);await code(x.sqlite,'existing@example.com');
  const response=await x.post(registerCode,{email:'existing@example.com'});
  assert.equal(response.status,409);const body=await response.json();assert.equal(body.error_code,'email_exists');assert.match(body.message,/已注册/);
  assert.equal(x.mails.length,0);assert.deepEqual(x.writes,[]);
  assert.equal(x.sqlite.prepare('SELECT count(*) n FROM mail_routing_state').get().n,0);
  assert.equal(x.sqlite.prepare('SELECT used_at FROM email_verification_codes').get().used_at,null);
});
test('case and surrounding input whitespace cannot bypass email guard',async t=>{
  const x=setup(t);user(x.sqlite,'Existing@Example.COM');
  const response=await x.post(registerCode,{email:'  EXISTING@example.com  '});
  assert.equal(response.status,409);assert.equal(x.mails.length,0);assert.deepEqual(x.writes,[]);
});
test('inactive users also reserve their email address',async t=>{
  const x=setup(t);user(x.sqlite,'existing@example.com','inactive',0);
  assert.equal((await x.post(registerCode,{email:'existing@example.com'})).status,409);
  assert.equal(x.mails.length,0);
});
test('duplicate email blocked even when mail configuration is missing',async t=>{
  const x=setup(t);user(x.sqlite);delete x.env.BREVO_API_KEY;delete x.env.RESEND_API_KEY;
  assert.equal((await x.post(registerCode,{email:'existing@example.com'})).status,409);
});
test('unregistered email still sends, persists code and advances rotation',async t=>{
  const x=setup(t);
  assert.equal((await x.post(registerCode,{email:'  NEW@example.com '})).status,200);
  assert.equal(x.mails.length,1);assert.match(x.mails[0].url,/brevo/);
  assert.equal(x.sqlite.prepare('SELECT email FROM email_verification_codes').get().email,'new@example.com');
  assert.equal(x.sqlite.prepare('SELECT slot FROM mail_routing_state').get().slot,0);
});
test('registered email can still receive password-reset email',async t=>{
  const x=setup(t);user(x.sqlite);
  assert.equal((await x.post(passwordCode,{email:'existing@example.com'})).status,200);
  assert.equal(x.mails.length,1);
  assert.equal(x.sqlite.prepare('SELECT purpose FROM email_verification_codes').get().purpose,'password_reset');
});
test('duplicate registration email rejected before code validation or consumption',async t=>{
  const x=setup(t);user(x.sqlite);const id=await code(x.sqlite,'existing@example.com');
  for(const value of ['123456','000000']) {
    const response=await x.post('/v1/auth/register',{...registration(' EXISTING@example.com '),verification_code:value});
    assert.equal(response.status,409);assert.equal((await response.json()).error_code,'email_exists');
  }
  assert.deepEqual({...x.sqlite.prepare('SELECT used_at,attempts FROM email_verification_codes WHERE id=?').get(id)},{used_at:null,attempts:0});
  assert.deepEqual(x.writes,[]);
});
test('duplicate account does not consume another mailbox verification code',async t=>{
  const x=setup(t);user(x.sqlite);const id=await code(x.sqlite,'new@example.com');
  const response=await x.post('/v1/auth/register',registration('new@example.com',' EXISTING '));
  assert.equal(response.status,409);assert.equal((await response.json()).error_code,'account_exists');
  assert.equal(x.sqlite.prepare('SELECT used_at FROM email_verification_codes WHERE id=?').get(id).used_at,null);
});
test('normal registration creates user and subscription; further registration code is blocked',async t=>{
  const x=setup(t);await code(x.sqlite,'new@example.com');
  assert.equal((await x.post('/v1/auth/register',registration('new@example.com'))).status,201);
  assert.equal(x.sqlite.prepare('SELECT count(*) n FROM users').get().n,1);
  assert.equal(x.sqlite.prepare('SELECT count(*) n FROM subscriptions').get().n,1);
  assert.equal((await x.post(registerCode,{email:'new@example.com'})).status,409);
  assert.equal(x.mails.length,0);
});
for(const collision of ['email','account']) test('concurrent '+collision+' registration returns conflict, not uncaught DB error',async t=>{
  const x=setup(t);const emails=collision==='email'?['race@example.com','race@example.com']:['a@example.com','b@example.com'];
  for(const email of new Set(emails)) await code(x.sqlite,email);
  const accounts=collision==='account'?['race-account','race-account']:['account-a','account-b'];
  const responses=await Promise.all(emails.map((email,i)=>x.post('/v1/auth/register',registration(email,accounts[i]))));
  assert.deepEqual(responses.map(r=>r.status).sort(),[201,409]);
  const failure=responses.find(r=>r.status===409);assert.equal((await failure.json()).error_code,collision+'_exists');
  assert.equal(x.sqlite.prepare('SELECT count(*) n FROM users').get().n,1);
  assert.equal(x.sqlite.prepare('SELECT count(*) n FROM subscriptions').get().n,1);
});
test('unrelated batch failure is not mislabeled as an existing account',async t=>{
  const x=setup(t);await code(x.sqlite,'new@example.com');
  x.DB.batch=async()=>{throw Error('test database failure');};
  const response=await x.post('/v1/auth/register',registration('new@example.com'));
  assert.equal(response.status,500);
  assert.equal((await response.json()).error_code,'internal_error');
  assert.equal(x.sqlite.prepare('SELECT used_at FROM email_verification_codes').get().used_at,null);
});
