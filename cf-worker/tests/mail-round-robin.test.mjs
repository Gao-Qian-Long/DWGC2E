import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync, mkdtempSync, rmSync, rmdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { Worker } from 'node:worker_threads';
import { join } from 'node:path';
import { MAIL_ROTATION_SQL, selectMailProviders, deliverMail } from '../src/mail.ts';
const migration = readFileSync(new URL('../migrations/0001_mail_round_robin.sql', import.meta.url), 'utf8');
const env = { MAIL_PROVIDER: 'round_robin', MAIL_FROM: 'DWGC2E <noreply@example.com>', BREVO_API_KEY: 'fake-brevo', RESEND_API_KEY: 'fake-resend' };
const message = { id: 'rotation-test', to: 'test@example.com', subject: 'test', html: 'same code' };
function adapter(sqlite) {
  let writes = 0;
  return { get writes() { return writes; }, prepare(sql) { return { async first() { writes++; return sqlite.prepare(sql).get(); } }; } };
}
function database(t) { const sqlite = new DatabaseSync(':memory:'); sqlite.exec(migration); t.after(() => sqlite.close()); return {sqlite, DB: adapter(sqlite)}; }
test('round robin alternates both providers; repeated migration preserves cursor', async t => {
  const {sqlite,DB} = database(t);
  assert.deepEqual(await selectMailProviders({...env,DB}), ['brevo','resend']);
  sqlite.exec(migration);
  assert.deepEqual(await selectMailProviders({...env,DB}), ['resend','brevo']);
  assert.deepEqual(await selectMailProviders({...env,DB}), ['brevo','resend']);
  assert.equal(DB.writes,3);
});
test('concurrent callers using separate connections share a persisted cursor', async t => {
  const dir = mkdtempSync(join(tmpdir(),'dwgc2e-mail-'));
  const path = join(dir,'routing.sqlite');
  const a = new DatabaseSync(path), b = new DatabaseSync(path); a.exec(migration);
  try {
    const calls = await Promise.all(Array.from({length:100},(_,i) => selectMailProviders({...env, DB:adapter(i % 2 ? a : b)})));
    assert.equal(calls.filter(x => x[0] === 'brevo').length,50);
    assert.equal(calls.filter(x => x[0] === 'resend').length,50);
    // Truly parallel SQLite writers (separate threads), not just Promise callers.
    const workerCode = `const {parentPort,workerData}=require('node:worker_threads');
      const {DatabaseSync}=require('node:sqlite');
      const db=new DatabaseSync(workerData.path); db.exec('PRAGMA busy_timeout=5000');
      const rows=[]; for(let i=0;i<50;i++) rows.push(db.prepare(workerData.sql).get().slot);
      db.close(); parentPort.postMessage(rows);`;
    const run = () => new Promise((resolve,reject) => {
      const worker = new Worker(workerCode,{eval:true,workerData:{path,sql:MAIL_ROTATION_SQL}});
      worker.once('message',resolve); worker.once('error',reject);
      worker.once('exit',code=>{if(code) reject(Error('worker exit '+code));});
    });
    const slots=(await Promise.all([run(),run()])).flat();
    assert.equal(slots.filter(x=>x===0).length,50);
    assert.equal(slots.filter(x=>x===1).length,50);
    a.close(); b.close();
    const reopened = new DatabaseSync(path);
    try { assert.deepEqual(await selectMailProviders({...env,DB:adapter(reopened)}),['brevo','resend']); }
    finally { reopened.close(); }
  } finally { if(a.isOpen) a.close(); if(b.isOpen) b.close(); rmSync(path,{force:true}); rmdirSync(dir); }
});
test('disabled fallback still rotates primary',async t=>{
  const {DB}=database(t);
  assert.deepEqual(await selectMailProviders({...env,DB,MAIL_FALLBACK_ENABLED:'false'}),['brevo']);
  assert.deepEqual(await selectMailProviders({...env,DB,MAIL_FALLBACK_ENABLED:'false'}),['resend']);
});
test('single key works without database with or without fallback',async()=>{
  for(const flag of ['true','false']) {
    assert.deepEqual(await selectMailProviders({...env,MAIL_FALLBACK_ENABLED:flag,RESEND_API_KEY:undefined}),['brevo']);
    assert.deepEqual(await selectMailProviders({...env,MAIL_FALLBACK_ENABLED:flag,BREVO_API_KEY:undefined}),['resend']);
  }
});
test('invalid config and missing keys fail before database access',async()=>{
  let touched=false; const DB={prepare(){touched=true;throw Error('unexpected');}};
  for(const values of [{MAIL_PROVIDER:'invalid'},{MAIL_FALLBACK_ENABLED:'invalid'},{MAIL_FROM:''},{BREVO_API_KEY:undefined,RESEND_API_KEY:undefined}]) {
    await assert.rejects(selectMailProviders({...env,DB,...values}));
  }
  assert.equal(touched,false);
});
test('missing table or routing failure sends nothing',async()=>{
  const sqlite=new DatabaseSync(':memory:'); let sent=0;
  try {
    for(const DB of [undefined,adapter(sqlite),{prepare(){throw Error('DB unavailable');}},{prepare(){return {async first(){return {slot:9};}};}}]) {
      await assert.rejects(deliverMail({...env,DB},message,async()=>{sent++;return new Response(null,{status:200});}));
    }
    assert.equal(sent,0);
  } finally {sqlite.close();}
});
test('pinned modes never require routing database',async()=>{
  for(const primary of ['brevo','resend']) assert.equal((await selectMailProviders({...env,MAIL_PROVIDER:primary}))[0],primary);
});
test('delivery and reverse fallback consume exactly one turn per message',async t=>{
  const {DB}=database(t); const calls=[];
  const fetcher=async(url,init)=>{calls.push({url,payload:JSON.parse(init.body)});return new Response(null,{status:calls.length%2 ? 429 : 200});};
  for(let i=0;i<2;i++) assert.equal((await deliverMail({...env,DB},{...message,id:String(i)},fetcher)).ok,true);
  assert.equal(DB.writes,2);
  assert.deepEqual(calls.map(x=>x.url.includes('brevo')?'brevo':'resend'),['brevo','resend','resend','brevo']);
  assert.equal(calls[0].payload.htmlContent,calls[1].payload.html);
});
test('preselected request route is not allocated a second time during delivery',async t=>{
  const {DB}=database(t); const e={...env,DB}; const order=await selectMailProviders(e);
  assert.equal((await deliverMail(e,message,async()=>new Response(null,{status:200}),8000,order)).ok,true);
  assert.equal(DB.writes,1);
});
test('no fallback sends one attempt yet next request selects the other provider',async t=>{
  const {DB}=database(t); const calls=[];
  for(let i=0;i<2;i++) {
    const result=await deliverMail({...env,DB,MAIL_FALLBACK_ENABLED:'false'},message,async url=>{calls.push(url);return new Response(null,{status:429});});
    assert.equal(result.ok,false);
  }
  assert.equal(calls.length,2); assert.match(calls[0],/brevo/); assert.match(calls[1],/resend/);
});
