import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mailProviders, deliverMail } from '../src/mail.ts';
const env = { MAIL_FROM: 'DWGC2E <noreply@example.com>', BREVO_API_KEY: 'test-brevo', RESEND_API_KEY: 'test-resend' };
const message = { id: 'test-id', to: 'user@example.com', subject: 'test', html: '123456' };
function mock(statuses) {
 const calls=[];
 const fetcher=async (url, init) => { calls.push({ url, ...init, payload: JSON.parse(init.body) }); return new Response(null,{status:statuses.shift()}); };
 return {calls,fetcher};
}
test('provider selection and configuration validation',()=>{
 assert.deepEqual(mailProviders(env),['brevo','resend']);
 assert.deepEqual(mailProviders({...env,MAIL_PROVIDER:'resend'}),['resend','brevo']);
 assert.deepEqual(mailProviders({...env,MAIL_FALLBACK_ENABLED:'false'}),['brevo']);
 assert.deepEqual(mailProviders({...env,BREVO_API_KEY:undefined}),['resend']);
 for(const invalid of [{MAIL_PROVIDER:'unknown'},{MAIL_FALLBACK_ENABLED:'yes'},{MAIL_FROM:''},{MAIL_FROM:'a\r\nb'},{BREVO_API_KEY:undefined,RESEND_API_KEY:undefined}]) assert.throws(()=>mailProviders({...env,...invalid}));
 assert.throws(()=>mailProviders({...env,BREVO_API_KEY:undefined,MAIL_FALLBACK_ENABLED:'false'}));
});
test('Brevo success does not send a second email',async()=>{
 const m=mock([201]); assert.deepEqual(await deliverMail(env,message,m.fetcher),{ok:true});
 assert.equal(m.calls.length,1); assert.equal(m.calls[0].payload.sender.email,'noreply@example.com');
});
for(const status of [401,402,403,429,500,503]) test('fallback on '+status,async()=>{
 const m=mock([status,200]); assert.equal((await deliverMail(env,message,m.fetcher)).ok,true);
 assert.equal(m.calls.length,2); assert.match(m.calls[1].url,/resend/);
 assert.equal(m.calls[0].payload.htmlContent,m.calls[1].payload.html);
 assert.equal(m.calls[1].headers['Idempotency-Key'],'verification-test-id');
});
test('Resend primary can fall back to Brevo',async()=>{
 const m=mock([429,201]); assert.equal((await deliverMail({...env,MAIL_PROVIDER:'resend'},message,m.fetcher)).ok,true);
 assert.match(m.calls[0].url,/resend/); assert.match(m.calls[1].url,/brevo/);
});
test('bad request does not fall back',async()=>{
 const m=mock([422]); assert.equal((await deliverMail(env,message,m.fetcher)).ok,false); assert.equal(m.calls.length,1);
});
test('fallback disabled and both providers failing',async()=>{
 const m=mock([503]); assert.equal((await deliverMail({...env,MAIL_FALLBACK_ENABLED:'false'},message,m.fetcher)).ok,false); assert.equal(m.calls.length,1);
 const n=mock([503,429]); assert.equal((await deliverMail(env,message,n.fetcher)).ok,false); assert.equal(n.calls.length,2);
});
test('network error is uncertain, not duplicated',async()=>{
 let count=0; const result=await deliverMail(env,message,async()=>{count++; throw Error('network');});
 assert.deepEqual(result,{ok:false,uncertain:true}); assert.equal(count,1);
});
test('timeout is bounded and does not duplicate',async()=>{
 let count=0;
 const result=await deliverMail(env,message,async(url,init)=>{ count++; return new Promise((resolve,reject)=>init.signal.addEventListener('abort',()=>reject(Error('timeout')))); },10);
 assert.deepEqual(result,{ok:false,uncertain:true}); assert.equal(count,1);
});

test('earlier server error preserves uncertain delivery on fallback rejection',async()=>{
 const m=mock([503,422]); assert.deepEqual(await deliverMail(env,message,m.fetcher),{ok:false,uncertain:true});
});
