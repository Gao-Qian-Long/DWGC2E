import type {PaymentEnv} from './index.ts';
// No trusted provider status API has been verified. Never settle from diagnostic queries.
export async function runPaymentRecovery(e:PaymentEnv){
 const now=new Date().toISOString(),lease=new Date(Date.now()+60000).toISOString();
 const rows=await e.DB.prepare("SELECT r.order_no FROM payment_recovery r JOIN orders o ON o.order_no=r.order_no WHERE r.state IN ('queued','waiting_callback') AND r.next_attempt_at<=? AND (r.lease_until IS NULL OR r.lease_until<=?) ORDER BY r.next_attempt_at LIMIT 50").bind(now,now).all<{order_no:string}>();
 for(const row of rows.results){
  const token=crypto.randomUUID();
  const lock=await e.DB.prepare("UPDATE payment_recovery SET lease_until=?,lease_token=? WHERE order_no=? AND state IN ('queued','waiting_callback') AND next_attempt_at<=? AND (lease_until IS NULL OR lease_until<=?)").bind(lease,token,row.order_no,now,now).run();
  if(!lock.meta.changes)continue;
  const o=await e.DB.prepare('SELECT o.status,o.created_at,r.attempts FROM orders o JOIN payment_recovery r ON r.order_no=o.order_no WHERE o.order_no=?').bind(row.order_no).first<{status:string;created_at:string;attempts:number}>();
  if(!o){
   // The order row is gone: hand the task to a human and release the lease instead of leaving it
   // locked until the lease expires.
   await e.DB.prepare("UPDATE payment_recovery SET state='manual',reason='order_missing',attempts=attempts+1,lease_until=NULL,lease_token=NULL,updated_at=? WHERE order_no=? AND lease_token=?").bind(now,row.order_no,token).run();
   continue;
  }
  const state=o.status==='paid'?'resolved':Date.now()-Date.parse(o.created_at)>=86400000?'manual':'waiting_callback';
  const next=new Date(Date.now()+Math.min(3600000,60000*2**Math.min(o.attempts,6))).toISOString();
  const written=await e.DB.prepare("UPDATE payment_recovery SET state=?,reason=?,attempts=attempts+1,next_attempt_at=?,lease_until=NULL,lease_token=NULL,updated_at=? WHERE order_no=? AND lease_token=? AND state IN ('queued','waiting_callback')").bind(state,state==='resolved'?'trusted_callback':'trusted_query_unavailable',next,now,row.order_no,token).run();
  // The task changed underneath us (a callback settled it, an operator resolved it): the lease
  // still has to be released, and only the holder of this token may do it.
  if(!written.meta.changes)await e.DB.prepare('UPDATE payment_recovery SET lease_until=NULL,lease_token=NULL WHERE order_no=? AND lease_token=?').bind(row.order_no,token).run();
 }
}
export async function recoveryAdmin(r:Request,e:PaymentEnv){
 const reply=(data:unknown,status=200)=>new Response(JSON.stringify(data),{status,headers:{'content-type':'application/json','cache-control':'no-store'}});
 const expected='Bearer '+(e.ADMIN_API_KEY||''),actual=r.headers.get('authorization')||'';
 if(!e.ADMIN_API_KEY||e.ADMIN_API_KEY.length<32||actual.length!==expected.length)return reply({error_code:'unauthorized'},401);
 let diff=0;for(let i=0;i<actual.length;i++)diff|=actual.charCodeAt(i)^expected.charCodeAt(i);if(diff)return reply({error_code:'unauthorized'},401);
 const path=new URL(r.url).pathname;
 if(path==='/v1/admin/billing/recovery'&&r.method==='GET'){
 const rows=await e.DB.prepare("SELECT r.*,o.status,o.create_state,o.amount_cents,o.channel,o.created_at FROM payment_recovery r JOIN orders o ON o.order_no=r.order_no WHERE r.state<>'resolved' AND o.created_at<=? ORDER BY o.created_at LIMIT 100").bind(new Date(Date.now()-300000).toISOString()).all();const conflicts=await e.DB.prepare("SELECT id,order_no,event_type,reason,created_at FROM payment_events WHERE event_type='callback_rejected' ORDER BY id DESC LIMIT 100").all();return reply({items:rows.results,conflicts:conflicts.results,trustedQueryAvailable:false});
 }
 if(/^\/v1\/admin\/billing\/orders\/DW[a-f0-9]{32}\/review$/.test(path)&&r.method==='GET'){
 const no=path.split('/').at(-2)!;const order=await e.DB.prepare('SELECT order_no,status,create_state,amount_cents,payable_cents,channel,created_at,paid_at FROM orders WHERE order_no=?').bind(no).first();if(!order)return reply({error_code:'order_not_found'},404);
 const cursor=new URL(r.url).searchParams.get('before')||'9223372036854775807';if(!/^[0-9]{1,19}$/.test(cursor))return reply({error_code:'invalid_cursor'},400);
 const events=await e.DB.prepare('SELECT id,event_type,reason,created_at FROM payment_events WHERE order_no=? AND id<CAST(? AS INTEGER) ORDER BY id DESC LIMIT 101').bind(no,cursor).all<{id:number}>();return reply({order,events:events.results.slice(0,100),nextCursor:events.results.length>100?String(events.results[99].id):null});
 }
 if(/^\/v1\/admin\/billing\/orders\/DW[a-f0-9]{32}\/review$/.test(path)&&r.method==='POST'){
 const no=path.split('/').at(-2)!;let b:any;try{const raw=await r.text();if(raw.length>2048)throw Error();b=JSON.parse(raw);}catch{return reply({error_code:'invalid_body'},400);}
 if(!b||Object.keys(b).some(k=>!['operator','evidenceRef','outcome'].includes(k))||!['awaiting_provider','provider_replay_requested','escalated'].includes(b.outcome)||![b.operator,b.evidenceRef].every(x=>typeof x==='string'&&/^[a-zA-Z0-9_.:@/-]{1,160}$/.test(x)))return reply({error_code:'invalid_review'},400);
 const result=await e.DB.prepare("INSERT INTO payment_events(order_no,event_type,reason,created_at) SELECT ?,'manual_review',?,? WHERE EXISTS(SELECT 1 FROM orders WHERE order_no=?)").bind(no,JSON.stringify(b),new Date().toISOString(),no).run();return result.meta.changes?reply({recorded:true,settled:false}):reply({error_code:'order_not_found'},404);
 }
 return reply({error_code:'not_found'},404);
}
