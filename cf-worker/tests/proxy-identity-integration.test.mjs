import {test} from 'node:test';import assert from 'node:assert/strict';import vm from 'node:vm';import {readFileSync,existsSync} from 'node:fs';import {clientAddress} from '../src/client-address.ts';
const proxyFile = new URL('../../../DWGC2E_Website/functions/api/[[path]].js',import.meta.url);
test('actual Pages signer and Worker verifier agree and strip supplied attestations',{skip:!existsSync(proxyFile)},async()=>{
 const env={WEB_PROXY_IDENTITY_KEY:'test-cross-end-identity-key-'.repeat(3)};
 const code=readFileSync(proxyFile,'utf8').replace('export async function','async function');
 for(const ip of ['203.0.113.15','2001:db8::15']){
 const context={URL,Headers,Response,AbortSignal,crypto,TextEncoder,fetch:async(url,options)=>{
 const headers=new Headers(options.headers);headers.set('cf-connecting-ip','2a06:98c0:3600::103');
 assert.equal(await clientAddress(new Request(url,{method:options.method,headers}),env),ip);return Response.json({ok:true});}};
 vm.runInNewContext(code+';globalThis.proxy=onRequest;',context);
 const r=await context.proxy({env,request:new Request('https://site.test/api/v1/feedback?x=1',{method:'POST',headers:{authorization:'Bearer test','cf-connecting-ip':ip,'x-dwgc-client-ip':'attacker','x-dwgc-client-signature':'attacker'}})});assert.equal(r.status,200);
 }
});
