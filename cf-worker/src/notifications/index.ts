/** Directed per-user notifications. Every statement here is scoped by the authenticated user_id:
 *  a recipient can only ever read their own rows, and the read receipt is written once. */
import type {SessionUser} from '../auth/sessions.ts';
type Env={DB:D1Database};
type Row=Record<string,any>;
const PAGE_SIZE=25;
const UUID=/^[-a-f0-9]{36}$/;
export async function notificationsRoute(r:Request,e:Env,user:SessionUser,origin:string,readBody:(r:Request)=>Promise<any>):Promise<Response>{
 const reply=(data:unknown,status=200)=>new Response(JSON.stringify(data),{status,headers:{'content-type':'application/json; charset=utf-8','cache-control':'no-store','access-control-allow-origin':origin}});
 const url=new URL(r.url),path=url.pathname,at=new Date().toISOString();
 if(path==='/v1/notifications'&&r.method==='GET'){
  const page=Number(url.searchParams.get('page')||1);
  if(!Number.isSafeInteger(page)||page<1||page>100000)return reply({error_code:'invalid_request',message:'页码无效'},400);
  // Visibility: a recalled notification is never returned again, and a notification outside its
  // validity window is withheld exactly like the fleet-wide announcement (admin/operations.ts:26).
  // The `r.user_id=?` term is the authorisation boundary for this endpoint.
  const rows=await e.DB.prepare(`SELECT n.id,n.title,n.body,n.created_at,n.expires_at,r.read_at
   FROM notification_recipients r JOIN notifications n ON n.id=r.notification_id
   WHERE r.user_id=? AND n.withdrawn_at IS NULL AND (n.expires_at IS NULL OR n.expires_at>?)
   ORDER BY n.created_at DESC,n.id DESC LIMIT ? OFFSET ?`).bind(user.user_id,at,PAGE_SIZE+1,(page-1)*PAGE_SIZE).all<Row>();
  const unread=await e.DB.prepare(`SELECT COUNT(*) n FROM notification_recipients r JOIN notifications n ON n.id=r.notification_id
   WHERE r.user_id=? AND r.read_at IS NULL AND n.withdrawn_at IS NULL AND (n.expires_at IS NULL OR n.expires_at>?)`).bind(user.user_id,at).first<Row>();
  const items=rows.results.slice(0,PAGE_SIZE);
  return reply({items,unread_count:Number(unread?.n||0),page,pageSize:PAGE_SIZE,total:items.length,hasMore:rows.results.length>PAGE_SIZE});
 }
 if(path==='/v1/notifications/read'&&r.method==='POST'){
  const body=await readBody(r);
  const raw=Array.isArray(body?.ids)?body.ids:null;
  const ids=raw?raw.filter((x:unknown):x is string=>typeof x==='string'&&UUID.test(x)):null;
  if(!raw||!ids||raw.length<1||raw.length>100||ids.length!==raw.length)return reply({error_code:'invalid_request',message:'请提供 1–100 个有效的通知编号'},400);
  // `read_at IS NULL` is what makes the receipt idempotent: the first read time is authoritative
  // and a repeated report never overwrites it, so `marked` is 0 on a replay. The visibility window
  // is repeated here so an expired or recalled notification can never acquire a fresh receipt.
  const results=await e.DB.batch([...new Set(ids)].map(id=>e.DB.prepare(`UPDATE notification_recipients SET read_at=?
   WHERE notification_id=? AND user_id=? AND read_at IS NULL
   AND EXISTS(SELECT 1 FROM notifications n WHERE n.id=notification_id AND n.withdrawn_at IS NULL AND (n.expires_at IS NULL OR n.expires_at>?))`)
   .bind(at,id,user.user_id,at)));
  return reply({success:true,read_at:at,marked:results.reduce((n:number,x:any)=>n+Number(x.meta?.changes||0),0),ids});
 }
 return reply({error_code:'not_found',message:'接口不存在'},404);
}
