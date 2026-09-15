import {createEzfpySign,verifyEzfpySign,moneyStringToCents,centsToMoneyString} from './sign.ts';
export interface PaymentEnv {
 DB:D1Database; EZFPY_API_BASE_URL?:string; EZFPY_PID?:string; EZFPY_KEY?:string;
 PUBLIC_WEB_URL?:string; PUBLIC_API_URL?:string; PAYMENTS_ENABLED?:string;
 EZFPY_QR_IMAGE_ORIGINS?:string; ADMIN_API_KEY?:string;
}
type Order={order_no:string;user_id:string;plan_id:string;plan_name:string;duration_days:number;membership_level:string;amount_cents:number;payable_cents:number;provider:string;channel:string;provider_trade_no:string|null;status:string;create_state:string;last_error_code?:string|null;qr_code:string|null;qr_image_url:string|null;created_at:string;expires_at:string;paid_at:string|null};
type User={user_id:string;email?:string};
type Created={providerTradeNo:string;qrCode:string;qrImageUrl:string;amountCents:number};
export interface PaymentProvider { createPayment(e:PaymentEnv,o:Order,diagnostic?:CreateDiagnostic):Promise<Created>; }
const diagnosticCodes=new Set(['payment_config','provider_http','provider_response','provider_rejected','provider_signature_rejected','provider_channel_unavailable','provider_amount_rejected','provider_merchant_rejected','provider_mismatch','provider_trade_missing','provider_amount_mismatch','invalid_money','invalid_qr','invalid_qr_image','provider_qr_missing','provider_timeout','provider_prepare','provider_network','provider_redirect','provider_read','provider_parse','provider_business','provider_validation','provider_persist','provider_audit','provider_query','provider_query_http','provider_query_redirect','provider_query_response','provider_query_rejected']);
type CreateStage='prepare'|'request'|'read'|'parse'|'business'|'validate'|'persist'|'audit'|'query';
type FieldSummary={type:string;present:boolean;length?:number};
class CreateDiagnostic {
 readonly started=Date.now();stage:CreateStage='prepare';httpStatus?:number;businessCode?:string;providerMessage?:string;
 query?:{httpStatus?:number;businessCode?:string;responseReceived:boolean;bodyRead:boolean;fields?:Record<string,FieldSummary>};
 responseReceived=false;bodyRead=false;saved=false;fields?:Record<string,FieldSummary>;
 summary(code:string){return JSON.stringify({version:1,stage:this.stage,code,elapsedMs:Math.max(0,Date.now()-this.started),httpStatus:this.httpStatus,businessCode:this.businessCode,responseReceived:this.responseReceived,bodyRead:this.bodyRead,saved:this.saved,businessMessage:this.providerMessage,fields:this.fields,query:this.query});}
}
function diagnosticCode(err:unknown,stage:CreateStage){
 if(err instanceof Error&&diagnosticCodes.has(err.message))return err.message;
 if((stage==='request'||stage==='read')&&err instanceof Error&&['TimeoutError','AbortError'].includes(err.name))return 'provider_timeout';
 return ({prepare:'provider_prepare',request:'provider_network',read:'provider_read',parse:'provider_parse',business:'provider_business',validate:'provider_validation',persist:'provider_persist',audit:'provider_audit',query:'provider_query'} as const)[stage];
}
function summarizeFields(d:Record<string,unknown>):Record<string,FieldSummary>{
 const result:Record<string,FieldSummary>={};
 for(const key of ['code','money','type','out_trade_no','trade_no','qrcode','code_url']){
  const value=d[key];result[key]={present:Object.hasOwn(d,key),type:value===null?'null':Array.isArray(value)?'array':typeof value,...(typeof value==='string'?{length:value.length}:{})};
 }
 return result;
}
async function recordCreateDiagnostic(e:PaymentEnv,no:string,event:string,diagnostic:CreateDiagnostic,code:string){
 // Only internally constructed metadata; no error text, payloads, URLs or credentials.
 const reason=diagnostic.summary(code);
 console.info(JSON.stringify({event,orderNo:no,diagnostic:JSON.parse(reason)}));
 try{await e.DB.prepare('INSERT INTO payment_events(order_no,event_type,reason,created_at) VALUES(?,?,?,?)').bind(no,event,reason,stamp()).run();}
 catch{console.error(JSON.stringify({event:'payment_audit_failed',orderNo:no,stage:'audit',code:'provider_audit',saved:diagnostic.saved}));}
}
// Fixed categories only: never persist upstream messages or credentials.
function rejectionCode(value:unknown){const m=typeof value==='string'?value.slice(0,256):'';
 if(/签名|sign/i.test(m))return 'provider_signature_rejected';
 if(/通道|收款账号|未在线|离线/.test(m))return 'provider_channel_unavailable';
 if(/金额|money/i.test(m))return 'provider_amount_rejected';
 if(/商户|PID/i.test(m))return 'provider_merchant_rejected';
 return 'provider_rejected';
}
const stamp=()=>new Date().toISOString();
const fail=(code:string)=>{throw new Error(code);};
function httpsBase(raw:string|undefined):URL {const u=new URL(raw||'');if(u.protocol!=='https:'||u.username||u.password||u.search||u.hash)fail('payment_config');return u;}
function configured(e:PaymentEnv){if(!e.EZFPY_KEY||!e.EZFPY_PID)fail('payment_config');httpsBase(e.EZFPY_API_BASE_URL);httpsBase(e.PUBLIC_WEB_URL);httpsBase(e.PUBLIC_API_URL);}
export function safeImage(e:PaymentEnv,value:unknown):string {
 if(!value)return '';if(typeof value!=='string'||value.length>2048)fail('invalid_qr_image');
 const base=httpsBase(e.EZFPY_API_BASE_URL),u=new URL(value as string,base);
 const origins=[base.origin,...(e.EZFPY_QR_IMAGE_ORIGINS||'').split(',').map(x=>x.trim()).filter(Boolean)];
 if(u.protocol!=='https:'||u.username||u.password||!origins.includes(u.origin))fail('invalid_qr_image');return u.href;
}
/** Read-only lookup verified against an existing unpaid order on 2026-09-15.
 * type=2 selects our order number. This lookup never settles payments.
 */
async function queryCreatedOrder(e:PaymentEnv,o:Order,diagnostic:CreateDiagnostic):Promise<Record<string,unknown>>{
 const query:NonNullable<CreateDiagnostic['query']>={responseReceived:false,bodyRead:false};diagnostic.query=query;
 const url=new URL('api/findorder',httpsBase(e.EZFPY_API_BASE_URL));
 const r=await fetch(url,{method:'POST',headers:{'content-type':'application/x-www-form-urlencoded'},body:new URLSearchParams({order_no:o.order_no,type:'2'}),redirect:'manual',signal:AbortSignal.timeout(10000)});
 query.responseReceived=true;query.httpStatus=r.status;
 if(r.status>=300&&r.status<400){await r.body?.cancel().catch(()=>{});fail('provider_query_redirect');}
 if(!r.ok){await r.body?.cancel().catch(()=>{});fail('provider_query_http');}
 const reader=r.body?.getReader();if(!reader)fail('provider_query_response');
 const decoder=new TextDecoder();let text='',size=0;
 while(true){const c=await reader!.read();if(c.done)break;size+=c.value.byteLength;if(size>65536){await reader!.cancel().catch(()=>{});fail('provider_query_response');}text+=decoder.decode(c.value,{stream:true});}
 text+=decoder.decode();query.bodyRead=true;let raw:unknown;
 try{raw=JSON.parse(text);}catch{fail('provider_query_response');}
 if(!raw||typeof raw!=='object'||Array.isArray(raw))fail('provider_query_response');
 const envelope=raw as Record<string,unknown>,code=String(envelope.code??'');
 if(/^[-0-9]{1,16}$/.test(code))query.businessCode=code;
 if(envelope.code!==200&&envelope.code!=='200')fail('provider_query_rejected');
 if(!envelope.data||typeof envelope.data!=='object'||Array.isArray(envelope.data))fail('provider_query_response');
 const q=envelope.data as Record<string,unknown>;query.fields=summarizeFields(q);
 if(String(q.id)!==e.EZFPY_PID||String(q.out_trade_no)!==o.order_no||String(q.type)!==o.channel)fail('provider_mismatch');
 if(moneyStringToCents(q.money)!==o.amount_cents)fail('provider_amount_mismatch');
 return q;
}
export class EzfpyProvider implements PaymentProvider {
 async createPayment(e:PaymentEnv,o:Order,diagnostic=new CreateDiagnostic()):Promise<Created>{
  diagnostic.stage='prepare';configured(e);
  const params:Record<string,string>={pid:e.EZFPY_PID!,type:o.channel,notify_url:new URL('/v1/billing/notify/ezfpy',e.PUBLIC_API_URL).href,
   return_url:new URL('/billing.html',e.PUBLIC_WEB_URL).href,out_trade_no:o.order_no,name:o.plan_name,
   money:centsToMoneyString(o.amount_cents),param:o.plan_id,sign_type:'MD5'};
  params.sign=createEzfpySign(params,e.EZFPY_KEY!);
  const endpoint=new URL('mapi.php',httpsBase(e.EZFPY_API_BASE_URL));
  diagnostic.stage='request';
  const r=await fetch(endpoint,{method:'POST',redirect:'manual',signal:AbortSignal.timeout(10000),headers:{'content-type':'application/x-www-form-urlencoded'},body:new URLSearchParams(params)});
  diagnostic.responseReceived=true;diagnostic.httpStatus=r.status;
  if(r.status>=300&&r.status<400){await r.body?.cancel().catch(()=>{});fail('provider_redirect');}
  if(!r.ok){await r.body?.cancel().catch(()=>{});fail('provider_http');}
  diagnostic.stage='read';
  const reader=r.body?.getReader();if(!reader)fail('provider_response');
  const decoder=new TextDecoder();let body='',bytes=0;
  while(true){const chunk=await reader!.read();if(chunk.done)break;bytes+=chunk.value.byteLength;if(bytes>65536){await reader!.cancel().catch(()=>{});fail('provider_response');}body+=decoder.decode(chunk.value,{stream:true});}
  body+=decoder.decode();diagnostic.bodyRead=true;
  diagnostic.stage='parse';const raw:unknown=JSON.parse(body);
  if(!raw||typeof raw!=='object'||Array.isArray(raw))fail('provider_response');
  const d=raw as Record<string,unknown>;diagnostic.fields=summarizeFields(d);
  // Numeric business codes only. Arbitrary strings could contain sensitive data.
  const businessCode=String(d.code??'');if(/^[-0-9]{1,16}$/.test(businessCode))diagnostic.businessCode=businessCode;
  if(typeof d.msg==='string')diagnostic.providerMessage=d.msg.replace(/[\r\n\t]+/g,' ').replace(/https?:\/\/[^\s]+/gi,'[url]').replace(/[A-Za-z0-9_-]{24,}/g,'[token]').slice(0,160);
  diagnostic.stage='business';if(d.code!==200&&d.code!=='200'&&d.code!==1&&d.code!=='1')fail(rejectionCode(d.msg));
  diagnostic.stage='validate';
  // Reject supplied contradictions; query missing fields instead of assuming local values.
  if((d.out_trade_no!==undefined&&String(d.out_trade_no)!==o.order_no)||(d.type!==undefined&&String(d.type)!==o.channel))fail('provider_mismatch');
  if(d.money!==undefined&&moneyStringToCents(d.money)!==o.amount_cents)fail('provider_amount_mismatch');
  let verified=d;
  if(d.out_trade_no===undefined||d.type===undefined||d.money===undefined){
   diagnostic.stage='query';verified=await queryCreatedOrder(e,o,diagnostic);diagnostic.stage='validate';
   if(String(verified.trade_no)!==String(d.trade_no)||!d.trade_no)fail('provider_mismatch');
  }
  if(String(verified.out_trade_no)!==o.order_no||String(verified.type)!==o.channel)fail('provider_mismatch');
  const trade=String(d.trade_no||'');if(!/^[a-zA-Z0-9_-]{1,128}$/.test(trade))fail('provider_trade_missing');
  const amount=moneyStringToCents(verified.money);if(amount!==o.amount_cents)fail('provider_amount_mismatch');
  const qr=typeof d.qrcode==='string'?d.qrcode:'';if(qr.length>4096)fail('invalid_qr');
  let image='';try{image=safeImage(e,d.code_url);}catch(err){if(!qr)throw err;}if(!qr&&!image)fail('provider_qr_missing');
  return {providerTradeNo:trade,qrCode:qr,qrImageUrl:image,amountCents:amount};
 }
}
export async function settlePayment(e:PaymentEnv,o:Order,trade:string,amount:number){
 if(o.provider_trade_no!==trade||o.payable_cents!==amount)fail('payment_mismatch');
 if(o.status==='paid'){
  const s=await e.DB.prepare('SELECT provider_trade_no,amount_cents FROM payment_settlements WHERE order_no=?').bind(o.order_no).first<{provider_trade_no:string;amount_cents:number}>();
  if(!s||s.provider_trade_no!==trade||s.amount_cents!==amount)fail('settlement_mismatch');return;
 }
 if(o.status!=='pending'||o.create_state!=='ready')fail('payment_not_ready');
 // The trigger performs order + subscription + quota + audit in the SAME transaction.
 await e.DB.prepare('INSERT INTO payment_settlements(order_no,provider_trade_no,amount_cents,settled_at) VALUES(?,?,?,?) ON CONFLICT(order_no) DO NOTHING').bind(o.order_no,trade,amount,stamp()).run();
}
export async function notifyPayment(r:Request,e:PaymentEnv):Promise<Response>{
 const ack=(ok:boolean,status=200)=>new Response(ok?'success':'error',{status,headers:{'content-type':'text/plain; charset=utf-8','cache-control':'no-store'}});
 let orderNo='';
 try{
  if(r.method!=='GET')return ack(false,405);
  if(!e.EZFPY_KEY||!e.EZFPY_PID)return ack(false,503);
  if(r.url.length>12000)return ack(false,400);
  const p:Record<string,string>={};
  const fields=new Set(['pid','type','out_trade_no','trade_no','name','money','param','trade_status','sign','sign_type']);
  for(const [k,v] of new URL(r.url).searchParams){if(!fields.has(k)||Object.hasOwn(p,k)||v.length>4096)return ack(false,400);p[k]=v;}
  for(const k of ['pid','type','out_trade_no','trade_no','name','money','trade_status','sign','sign_type'])if(!p[k])return ack(false,400);
  if(p.sign_type!=='MD5'||!verifyEzfpySign(p,e.EZFPY_KEY))return ack(false,400);
  if(p.pid!==e.EZFPY_PID||p.trade_status!=='TRADE_SUCCESS')return ack(false,400);
  if(!/^DW[a-f0-9]{32}$/.test(p.out_trade_no))return ack(false,400);
  orderNo=p.out_trade_no;
  const o=await e.DB.prepare('SELECT * FROM orders WHERE order_no=?').bind(orderNo).first<Order>();
  if(!o||o.provider!=='ezfpy'||o.channel!==p.type)return ack(false,400);
  await settlePayment(e,o,p.trade_no,moneyStringToCents(p.money));return ack(true);
 }catch{
  // Never log callback URLs, signatures, raw payloads or upstream exception text.
  console.error(JSON.stringify({event:'payment_notify_failed',orderNo}));return ack(false,503);
 }
}
function publicOrder(o:Order){return {errorCode:o.last_error_code&&diagnosticCodes.has(o.last_error_code)?o.last_error_code:o.create_state==='unknown'?'create_unconfirmed':null,orderNo:o.order_no,planId:o.plan_id,planName:o.plan_name,status:o.status,createState:o.create_state,
 amountCents:o.amount_cents,payableCents:o.payable_cents,channel:o.channel,createdAt:o.created_at,expiresAt:o.expires_at,paidAt:o.paid_at,
 qrCode:o.status==='pending'&&Date.parse(o.expires_at)>Date.now()?o.qr_code:null,
 qrCodeImageUrl:o.status==='pending'&&Date.parse(o.expires_at)>Date.now()?o.qr_image_url:null};}
export async function billingRoute(r:Request,e:PaymentEnv,user:User,origin:string):Promise<Response>{
 const reply=(d:unknown,status=200)=>new Response(JSON.stringify(d),{status,headers:{'content-type':'application/json','access-control-allow-origin':origin,'cache-control':'no-store'}});
 const error=(code:string,message:string,status:number)=>reply({error_code:code,message},status);
 const url=new URL(r.url),path=url.pathname;
 if(path==='/v1/billing/plans'&&r.method==='GET'){
  const available=e.PAYMENTS_ENABLED==='true';
  const rows=available?await e.DB.prepare('SELECT id,name,price_cents,duration_days,membership_level FROM plans WHERE enabled=1 ORDER BY price_cents').all():{results:[]};
  return reply({plans:rows.results,paymentsEnabled:available,message:available?'所有已登录用户均可购买，价格与权益以下方套餐为准':'购买服务暂未开放'});
 }
 if(path==='/v1/billing/orders'&&r.method==='GET'){
  const cursor=url.searchParams.get('before')||'9999';
  const rows=await e.DB.prepare("SELECT * FROM orders WHERE user_id=? AND (status='paid' OR NOT EXISTS(SELECT 1 FROM payment_order_hidden h WHERE h.order_no=orders.order_no)) AND (created_at||order_no)<? ORDER BY created_at DESC,order_no DESC LIMIT 21").bind(user.user_id,cursor).all<Order>();
  const list=rows.results.slice(0,20),last=list.at(-1);return reply({orders:list.map(publicOrder),nextCursor:rows.results.length>20&&last?last.created_at+last.order_no:null});
 }
 // Soft deletion only. Preserve order, idempotency and callback/settlement data.
 if(path.startsWith('/v1/billing/orders/')&&path.endsWith('/hide')&&r.method==='POST'){
  const no=path.slice('/v1/billing/orders/'.length,-'/hide'.length);
  if(!/^DW[a-f0-9]{32}$/.test(no))return error('invalid_order','订单编号无效',400);
  const o=await e.DB.prepare('SELECT * FROM orders WHERE order_no=? AND user_id=?').bind(no,user.user_id).first<Order>();
  if(!o)return error('order_not_found','订单不存在',404);
  if(o.status==='paid'||o.paid_at)return error('paid_order','已付款订单不能删除',409);
  // Atomic predicate also protects against settlement racing with deletion.
  await e.DB.prepare("INSERT INTO payment_order_hidden(order_no,hidden_at) SELECT order_no,? FROM orders WHERE order_no=? AND user_id=? AND status!='paid' AND paid_at IS NULL ON CONFLICT(order_no) DO NOTHING").bind(stamp(),no,user.user_id).run();
  const latest=await e.DB.prepare('SELECT status,paid_at FROM orders WHERE order_no=? AND user_id=?').bind(no,user.user_id).first<{status:string;paid_at:string|null}>();
  if(latest?.status==='paid'||latest?.paid_at)return error('paid_order','订单已付款，不能删除，请刷新会员状态',409);
  return reply({orderNo:no,hidden:true,message:'已从列表删除；未取消平台订单，旧二维码请勿再付款。'});
 }
 if(path.startsWith('/v1/billing/orders/')&&r.method==='GET'){
  const no=path.slice('/v1/billing/orders/'.length);
  const o=await e.DB.prepare('SELECT * FROM orders WHERE order_no=? AND user_id=?').bind(no,user.user_id).first<Order>();
  return o?reply(publicOrder(o)):error('order_not_found','订单不存在',404);
 }
 if(path!=='/v1/billing/checkout'||r.method!=='POST')return error('not_found','接口不存在',404);
 if(e.PAYMENTS_ENABLED!=='true')return error('payments_disabled','购买服务暂未开放',403);
 try{configured(e);}catch{return error('payment_config','支付服务尚未配置完成',503);}
 const key=r.headers.get('Idempotency-Key')||'';if(!/^[a-zA-Z0-9_-]{16,80}$/.test(key))return error('invalid_idempotency_key','缺少有效下单标识',400);
 let body:Record<string,unknown>;try{const raw=await r.text();if(raw.length>2048)throw Error();body=JSON.parse(raw);if(!body||Array.isArray(body)||typeof body!=='object')throw Error();}catch{return error('invalid_body','请求格式错误',400);}
 if(Object.keys(body).some(k=>!['planId','channel'].includes(k))||typeof body.planId!=='string'||!['alipay','wxpay'].includes(String(body.channel)))return error('invalid_checkout','套餐或支付方式无效',400);
 const existing=()=>e.DB.prepare('SELECT * FROM orders WHERE user_id=? AND idempotency_key=?').bind(user.user_id,key).first<Order>();
 const replay=(o:Order)=>o.plan_id!==body.planId||o.channel!==body.channel?error('idempotency_conflict','下单标识已用于其他请求',409):reply(publicOrder(o));
 const previous=await existing();if(previous){
  const hidden=await e.DB.prepare('SELECT order_no FROM payment_order_hidden WHERE order_no=?').bind(previous.order_no).first();
  if(hidden)return error('order_hidden','原订单记录已删除。此操作不取消平台订单；确认未付款后可重新选择套餐。',409);
  return replay(previous);
 }
 const plan=await e.DB.prepare('SELECT * FROM plans WHERE id=? AND enabled=1').bind(body.planId).first<{id:string;name:string;price_cents:number;duration_days:number;membership_level:string}>();
 if(!plan)return error('invalid_plan','套餐不存在或未启用',400);
 // Atomic fixed-window count; do not trust browser-supplied forwarding headers.
 const t=Math.floor(Date.now()/1000);
 const limit=await e.DB.prepare('INSERT INTO request_limits(key,window_start,count) VALUES(?,?,1) ON CONFLICT(key) DO UPDATE SET window_start=CASE WHEN window_start<? THEN excluded.window_start ELSE window_start END,count=CASE WHEN window_start<? THEN 1 ELSE count+1 END RETURNING count').bind('checkout:'+user.user_id,t,t-60,t-60).first<{count:number}>();
 if(!limit||limit.count>5)return error('rate_limited','下单过于频繁，请一分钟后重试',429);
 const no='DW'+crypto.randomUUID().replaceAll('-',''),created=stamp(),expiry=new Date(Date.now()+15*60000).toISOString();
 await e.DB.prepare('INSERT INTO orders(order_no,user_id,plan_id,plan_name,duration_days,membership_level,amount_cents,payable_cents,channel,idempotency_key,created_at,expires_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(user_id,idempotency_key) DO NOTHING').bind(no,user.user_id,plan.id,plan.name,plan.duration_days,plan.membership_level,plan.price_cents,plan.price_cents,body.channel,key,created,expiry).run();
 const order=(await existing())!;if(order.order_no!==no)return replay(order);
 const diagnostic=new CreateDiagnostic();
 try{
  const result=await new EzfpyProvider().createPayment(e,order,diagnostic);
  diagnostic.stage='persist';
  const saved=await e.DB.prepare("UPDATE orders SET provider_trade_no=?,qr_code=?,qr_image_url=?,create_state='ready' WHERE order_no=? AND status='pending' AND create_state='creating'").bind(result.providerTradeNo,result.qrCode,result.qrImageUrl,no).run();
  if(saved.meta.changes!==1)fail('provider_persist');
  diagnostic.saved=true;
 }catch(err){
  const code=diagnosticCode(err,diagnostic.stage);
  await recordCreateDiagnostic(e,no,'payment_create_unknown',diagnostic,code);
  try{await e.DB.prepare("UPDATE orders SET create_state='unknown',last_error_code=? WHERE order_no=? AND create_state='creating'").bind(code,no).run();}
  catch{console.error(JSON.stringify({event:'payment_error_state_failed',orderNo:no,stage:'persist',code:'provider_persist'}));}
 }
 // Audit cannot turn a successfully persisted ready order into an unknown order.
 if(diagnostic.saved){diagnostic.stage='audit';await recordCreateDiagnostic(e,no,'payment_created',diagnostic,'ok');}

 return reply(publicOrder((await existing())!));
}

/** Read-only diagnostic query. No settlement until this platform's real response contract is verified. */
export async function inspectPayment(r:Request,e:PaymentEnv):Promise<Response>{
 const reply=(value:unknown,status=200)=>new Response(JSON.stringify(value),{status,headers:{'content-type':'application/json','cache-control':'no-store'}});
 if(r.method!=='POST')return reply({error_code:'method_not_allowed'},405);
 const given=r.headers.get('authorization')||'',expected='Bearer '+(e.ADMIN_API_KEY||'');
 if(!e.ADMIN_API_KEY||e.ADMIN_API_KEY.length<32||given.length!==expected.length)return reply({error_code:'unauthorized'},401);
 let diff=0;for(let i=0;i<given.length;i++)diff|=given.charCodeAt(i)^expected.charCodeAt(i);
 if(diff)return reply({error_code:'unauthorized'},401);
 const no=new URL(r.url).pathname.split('/').at(-2)||'';
 if(!/^DW[a-f0-9]{32}$/.test(no))return reply({error_code:'invalid_order'},400);
 const o=await e.DB.prepare('SELECT * FROM orders WHERE order_no=?').bind(no).first<Order>();
 if(!o)return reply({error_code:'order_not_found'},404);
 const t=Math.floor(Date.now()/1000);
 const n=await e.DB.prepare('INSERT INTO request_limits(key,window_start,count) VALUES(?,?,1) ON CONFLICT(key) DO UPDATE SET window_start=CASE WHEN window_start<? THEN excluded.window_start ELSE window_start END,count=CASE WHEN window_start<? THEN 1 ELSE count+1 END RETURNING count').bind('payment-inspect',t,t-60,t-60).first<{count:number}>();
 if(!n||n.count>5)return reply({error_code:'rate_limited'},429);
 try{
  configured(e);
  const data=await queryCreatedOrder(e,o,new CreateDiagnostic());
  // Read-only, ownership-validated and size-bounded. Never grant membership here.
  const selected:Record<string,string>={};
  for(const k of ['code','pid','out_trade_no','trade_no','status','trade_status','money','type','addtime','endtime']){
   const value=data?.[k];if(['string','number'].includes(typeof value))selected[k]=String(value).slice(0,128).replaceAll(e.EZFPY_KEY!,'[redacted]');
  }
  await e.DB.prepare("INSERT INTO payment_events(order_no,event_type,reason,created_at) VALUES(?,'query_requested','read_only_identity_verified',?)").bind(no,stamp()).run();
  return reply({orderNo:no,settled:false,reviewRequired:true,observed:selected,message:'查单仅供核查，已校验订单归属和金额，不会自动开通会员。'});
 }catch{return reply({error_code:'query_unconfirmed',message:'平台查询未确认；没有修改订单或会员。'},502);}
}

