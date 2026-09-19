import {test} from 'node:test';import assert from 'node:assert/strict';import {clientAddress} from '../src/client-address.ts';
const env={WEB_PROXY_IDENTITY_KEY:'test-only-proxy-secret-'.repeat(3)};
async function signed(overrides={}){
 const time=String(Math.floor(Date.now()/1000)),ip='203.0.113.7';
 const key=await crypto.subtle.importKey('raw',new TextEncoder().encode(env.WEB_PROXY_IDENTITY_KEY),{name:'HMAC',hash:'SHA-256'},false,['sign']);
 const sig=Buffer.from(await crypto.subtle.sign('HMAC',key,new TextEncoder().encode([time,ip,'POST','/v1/feedback',''].join('\n')))).toString('hex');
 return new Request('https://test/v1/feedback',{method:'POST',headers:{'cf-connecting-ip':'2a06:98c0:3600::103','x-dwgc-client-ip':ip,'x-dwgc-client-time':time,'x-dwgc-client-signature':sig,...overrides}});
}
test('verified proxy identity resolves original client across zone boundary',async()=>{assert.equal(await clientAddress(await signed(),env),'203.0.113.7');});
test('forged address, timestamp, signature and authorization cannot override edge',async()=>{
 for(const h of [{'x-dwgc-client-ip':'203.0.113.8'},{'x-dwgc-client-time':'1000000000'},{'x-dwgc-client-signature':'0'.repeat(64)},{authorization:'Bearer other'},{'x-dwgc-client-ip':'garbage'}])assert.equal(await clientAddress(await signed(h),env),'2a06:98c0:3600::103');
});
test('attestation cannot be reused for another path or method',async()=>{
 const original=await signed();for(const r of [new Request('https://test/v1/auth/login',{method:'POST',headers:original.headers}),new Request(original,{method:'GET'})])assert.equal(await clientAddress(r,env),'2a06:98c0:3600::103');
});
test('unconfigured proxy and unsigned direct clients retain edge identity',async()=>{assert.equal(await clientAddress(await signed(),{}),'2a06:98c0:3600::103');assert.equal(await clientAddress(new Request('https://test',{headers:{'cf-connecting-ip':'203.0.113.9','x-forwarded-for':'attacker'}}),env),'203.0.113.9');});
