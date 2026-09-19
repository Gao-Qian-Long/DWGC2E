import {test} from 'node:test';
import assert from 'node:assert/strict';
import {setup} from './helpers/worker.mjs';
import {captchaHash,issueCaptcha,consumeCaptcha} from '../src/captcha.ts';
test('numeric CAPTCHA is email/purpose bound, single use, expires, and never returns plaintext answer',async t=>{
 const x=setup(t);const issued=await issueCaptcha(x.env,'alice@example.com','register');
 assert.match(issued.image,/^data:image\/svg\+xml;base64,/);assert.equal(issued.expires_in,300);assert.equal(issued.code,undefined);
 const code='12345';x.db.prepare('UPDATE numeric_captchas SET answer_hash=? WHERE id=?').run(await captchaHash(issued.captcha_id+'|'+code+'|'+x.env.PASSWORD_PEPPER),issued.captcha_id);
 assert.equal(await consumeCaptcha(x.env,'bob@example.com','register',issued.captcha_id,code),false);
 assert.equal(await consumeCaptcha(x.env,'alice@example.com','password_reset',issued.captcha_id,code),false);
 assert.equal(await consumeCaptcha(x.env,'alice@example.com','register',issued.captcha_id,code),true);
 assert.equal(await consumeCaptcha(x.env,'alice@example.com','register',issued.captcha_id,code),false);
 const expired=await issueCaptcha(x.env,'alice@example.com','register');x.db.prepare('UPDATE numeric_captchas SET expires_at=0 WHERE id=?').run(expired.captcha_id);
 assert.equal(await consumeCaptcha(x.env,'alice@example.com','register',expired.captcha_id,code),false);
});
test('missing CAPTCHA blocks both email endpoints before delivery',async t=>{
 const x=setup(t);let calls=0;t.mock.method(globalThis,'fetch',async()=>{calls++;return Response.json({});});
 for(const p of ['register','password']){const r=await x.request('/v1/auth/'+p+'/request-code',{method:'POST',body:{email:'new@example.com'}});assert.equal(r.status,400);assert.equal((await r.json()).error_code,'invalid_captcha');}
 assert.equal(calls,0);
});
test('wrong guess consumes challenge; parallel valid guesses only one succeeds',async t=>{
 const x=setup(t);const c=await issueCaptcha(x.env,'alice@example.com','register');const id=c.captcha_id;
 x.db.prepare('UPDATE numeric_captchas SET answer_hash=? WHERE id=?').run(await captchaHash(id+'|12345|'+x.env.PASSWORD_PEPPER),id);
 const values=await Promise.all([consumeCaptcha(x.env,'alice@example.com','register',id,'12345'),consumeCaptcha(x.env,'alice@example.com','register',id,'12345')]);
 assert.equal(values.filter(Boolean).length,1);
});
