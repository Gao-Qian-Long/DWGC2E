import {test} from 'node:test';import assert from 'node:assert/strict';import {setup} from './helpers/worker.mjs';import worker from '../src/index.ts';
const request=(ip,spoof)=>new Request('https://test/v1/feedback',{method:'POST',headers:{'content-type':'application/json','cf-connecting-ip':ip,'x-forwarded-for':spoof,'x-real-ip':spoof,'true-client-ip':spoof,'forwarded':'for='+spoof},body:'{}'});
test('feedback rate limit keys only edge identity; forged headers cannot rotate bucket',async t=>{
 const x=setup(t);
 for(let n=0;n<10;n++)assert.equal((await worker.fetch(request('203.0.113.1','198.51.100.'+n),x.env)).status,400);
 assert.equal((await worker.fetch(request('203.0.113.1','198.51.100.99'),x.env)).status,429);
 assert.equal((await worker.fetch(request('203.0.113.2','198.51.100.99'),x.env)).status,400);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM feedback').get().n,0);
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM request_limits WHERE key LIKE 'feedback:ip:%'").get().n,2);
});
test('login IP bucket ignores spoofed headers without weakening account limiter',async t=>{
 const x=setup(t);
 const call=(account,ip,spoof)=>worker.fetch(new Request('https://test/v1/auth/web/login',{method:'POST',headers:{'content-type':'application/json','cf-connecting-ip':ip,'x-forwarded-for':spoof,'x-real-ip':spoof},body:JSON.stringify({account,password:'invalid-test-password'})}),x.env);
 // Account lockout now closes the account before the plain rate bucket is exhausted: the first
 // ten failures still answer 401, the eleventh is refused, and it never reaches the IP counter.
 for(let n=0;n<10;n++)assert.equal((await call('no-such-account','203.0.113.1','198.51.100.'+n)).status,401);
 assert.equal((await call('no-such-account','203.0.113.2','198.51.100.99')).status,429);
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM request_limits WHERE key LIKE 'login-ip:%'").get().n,1);
 assert.equal(x.db.prepare("SELECT count FROM request_limits WHERE key LIKE 'login-ip:%'").get().count,10);
 assert.equal((await call('different-account','203.0.113.2','198.51.100.99')).status,401);
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM request_limits WHERE key LIKE 'login-ip:%'").get().n,2);
});
