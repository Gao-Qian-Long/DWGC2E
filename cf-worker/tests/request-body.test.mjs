import {test} from 'node:test';
import assert from 'node:assert/strict';
import {guardRequestBody, requestBodyLimit} from '../src/request-body.ts';
import worker from '../src/index.ts';
const req=(body, path='/v1/auth/login', headers={})=>new Request('https://test'+path,{method:'POST',headers:{'content-type':'application/json',...headers},body,duplex:'half'});
const rejects=(r,status,code)=>assert.rejects(guardRequestBody(r),e=>e.status===status&&e.code===code);
test('body guard preserves exact UTF8 bytes and authentication',async()=>{
 const raw=' {"note":"法兰 😀", "value": 1} \n';
 const r=await guardRequestBody(req(raw,'/v1/glossary?x=1',{authorization:'Bearer preserved'}));
 assert.equal(await r.text(),raw);assert.equal(r.headers.get('authorization'),'Bearer preserved');assert.equal(r.method,'POST');assert.equal(new URL(r.url).search,'?x=1');
});
test('size boundary accepted, plus one rejected',async()=>{
 const n=requestBodyLimit('/v1/auth/login');
 assert.equal((await(await guardRequestBody(req('"'+'a'.repeat(n-2)+'"'))).text()).length,n);
 await rejects(req('"'+'a'.repeat(n-1)+'"'),413,'payload_too_large');
});
test('UTF8 bytes counted instead of characters',async()=>{await rejects(req(JSON.stringify('中'.repeat(23000))),413,'payload_too_large');});
test('declared oversize cancels stream',async()=>{
 let cancelled=false;const stream=new ReadableStream({cancel(){cancelled=true;}});
 await rejects(req(stream,'/v1/auth/login',{'content-length':'999999'}),413,'payload_too_large');assert.equal(cancelled,true);
});
test('absent or forged small content length cannot bypass streaming bound',async()=>{
 for(const headers of [{},{'content-length':'2'}]) {
 let cancelled=false;const stream=new ReadableStream({pull(c){c.enqueue(new Uint8Array(8192).fill(32));},cancel(){cancelled=true;}});
 await rejects(req(stream,'/v1/auth/login',headers),413,'payload_too_large');assert.equal(cancelled,true);
 }
});
test('malformed JSON and invalid UTF8 return controlled errors',async()=>{
 await rejects(req('{'),400,'invalid_json');await rejects(req(new Uint8Array([34,255,34])),400,'invalid_json');
});
test('reader failure maps to client error',async()=>{await rejects(req(new ReadableStream({pull(c){c.error(Error('broken'));}})),400,'invalid_request');});
test('callback form and JSON bytes remain unmodified',async()=>{
 for(const [raw,type] of [['sign=abc&name=a%20b','application/x-www-form-urlencoded'],['{gateway payload','application/json']]) {
 const r=await guardRequestBody(req(raw,'/v1/billing/notify/ezfpy',{'content-type':type}));assert.equal(await r.text(),raw);
 }
});
test('bodyless GET and preflight unchanged',async()=>{
 for(const method of ['GET','OPTIONS']) {const r=new Request('https://test/v1/health',{method});assert.equal(await guardRequestBody(r),r);}
});
test('1000 maximum-sized glossary entries fit including escaped Unicode',async()=>{
 const entry={source:'中'.repeat(500),target:'文'.repeat(500),category:'类'.repeat(128),folder:'夹'.repeat(128),note:'注'.repeat(1000),enabled:true};
 const raw=JSON.stringify({entries:Array.from({length:1000},()=>entry)}).replace(/[\u0080-\uffff]/g,c=>'\\u'+c.charCodeAt(0).toString(16).padStart(4,'0'));
 assert.ok(raw.length<requestBodyLimit('/v1/glossary'));assert.equal((await(await guardRequestBody(req(raw,'/v1/glossary'))).text()).length,raw.length);
});
test('ingress rejection retains CORS and avoids database side effects',async()=>{
 const env={CORS_ORIGINS:'https://site.test',DB:{prepare(){throw Error('must not access DB');}}};
 for(const [body,status,code] of [['{',400,'invalid_json'],['x'.repeat(65537),413,'payload_too_large']]) {
 const r=await worker.fetch(req(body),env);assert.equal(r.status,status);assert.equal((await r.json()).error_code,code);assert.equal(r.headers.get('access-control-allow-origin'),env.CORS_ORIGINS);assert.equal(r.headers.get('cache-control'),'no-store');
 }
});
