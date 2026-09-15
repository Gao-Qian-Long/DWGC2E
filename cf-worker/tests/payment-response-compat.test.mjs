import {test} from 'node:test';
import assert from 'node:assert/strict';
import {EzfpyProvider} from '../src/payments/index.ts';
const env={EZFPY_API_BASE_URL:'https://provider.example/',EZFPY_PID:'6757',EZFPY_KEY:'fake',PUBLIC_API_URL:'https://api.example/',PUBLIC_WEB_URL:'https://web.example/'};
const order={order_no:'DWexample',channel:'alipay',amount_cents:19,plan_name:'Pro',plan_id:'test_pro_019'};
const full={code:200,id:6757,out_trade_no:'DWexample',trade_no:'T1',money:'0.19',type:'alipay'};
for(const [name,change,ok] of [['verified',{},true],['wrong money',{money:'0.29'},false],['wrong merchant',{id:1},false],['wrong order',{out_trade_no:'other'},false],['wrong channel',{type:'wxpay'},false],['wrong trade',{trade_no:'T2'},false],['missing money',{money:undefined},false],['query rejected',{code:201},false]])test(name,async t=>{
 let calls=0;t.mock.method(globalThis,'fetch',async(url,options)=>{calls++;assert.equal(options.redirect,'manual');if(calls===1)return Response.json({code:1,trade_no:'T1',qrcode:'alipay://example'});assert.equal(new URL(url).pathname,'/api/findorder');const params=new URLSearchParams(options.body);assert.equal(params.get('order_no'),order.order_no);assert.equal(params.get('type'),'2');assert.equal(options.method,'POST');return Response.json({code:change.code??200,data:{...full,...change}});});
 const result=new EzfpyProvider().createPayment(env,order);if(ok){assert.equal((await result).qrCode,'alipay://example');}else await assert.rejects(result);assert.equal(calls,2);
});
test('documented complete response does not query',async t=>{let calls=0;t.mock.method(globalThis,'fetch',async()=>{calls++;return Response.json({...full,code:200,qrcode:'alipay://example'});});await new EzfpyProvider().createPayment(env,order);assert.equal(calls,1);});
test('query redirects refused',async t=>{let calls=0;t.mock.method(globalThis,'fetch',async()=>++calls===1?Response.json({code:1,trade_no:'T1',qrcode:'alipay://example'}):new Response('',{status:302,headers:{location:'https://evil.example/'}}));await assert.rejects(new EzfpyProvider().createPayment(env,order));assert.equal(calls,2);});
