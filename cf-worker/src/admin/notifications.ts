/** Administrator-authored directed notifications. Kept out of `operation_settings`: the `content`
 *  section is a strict equality whitelist (operations.ts:17) and a per-user message must not be
 *  confused with the fleet-wide announcement. Authentication reuses the shared admin gate. */
import {adminRequestAuthorized,auditActor} from './session.ts';
type Env={DB:D1Database;ADMIN_API_KEY?:string;CORS_ORIGINS?:string;WEB_PROXY_IDENTITY_KEY?:string};
type Row=Record<string,any>;
const PAGE_SIZE=25;
const UUID=/^[-a-f0-9]{36}$/;
const ISO=/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/;
const MAX_BODY=2000,MAX_RECIPIENTS=500;
const present=(row:Row)=>({id:row.id,title:row.title,body:row.body,actor:row.actor,reason:row.reason,created_at:row.created_at,updated_at:row.updated_at,expires_at:row.expires_at??null,withdrawn_at:row.withdrawn_at??null,revision:row.revision,recipient_count:Number(row.recipient_count||0),read_count:Number(row.read_count||0)});
const projection=`n.*,n.id AS id,(SELECT COUNT(*) FROM notification_recipients r WHERE r.notification_id=n.id) recipient_count,(SELECT COUNT(*) FROM notification_recipients r WHERE r.notification_id=n.id AND r.read_at IS NOT NULL) read_count`;
export async function adminNotificationsRoute(r:Request,e:Env,readBody:(r:Request)=>Promise<any>):Promise<Response>{
 const reply=(data:unknown,status=200)=>new Response(JSON.stringify(data),{status,headers:{'content-type':'application/json; charset=utf-8','cache-control':'no-store','access-control-allow-origin':e.CORS_ORIGINS||'https://cad.pocketter.dpdns.org'}});
 if(!await adminRequestAuthorized(r,e))return reply({message:'管理员验证失败'},401);
 const url=new URL(r.url),path=url.pathname,at=new Date().toISOString();
 try{
  // ---- create ------------------------------------------------------------------------------
  if(path==='/v1/admin/notifications'&&r.method==='POST'){
   const d=await readBody(r);
   const keys=d&&typeof d==='object'&&!Array.isArray(d)?Object.keys(d):null;
   if(!keys||keys.some(k=>!['title','body','user_ids','expires_at','reason','request_id'].includes(k)))return reply({message:'请求字段无效'},400);
   const title=typeof d.title==='string'?d.title.trim():'',body=typeof d.body==='string'?d.body.trim():'';
   if(!title||title.length>120||!body||body.length>MAX_BODY||typeof d.reason!=='string'||d.reason.trim().length<1||d.reason.trim().length>500||!UUID.test(d.request_id||''))return reply({message:`请填写 1–120 字标题、1–${MAX_BODY} 字正文和 1–500 字操作原因`},400);
   if(!Array.isArray(d.user_ids)||d.user_ids.length<1||d.user_ids.length>MAX_RECIPIENTS||d.user_ids.some((x:unknown)=>typeof x!=='string'||!UUID.test(x)))return reply({message:`收件人须为 1–${MAX_RECIPIENTS} 个有效的用户编号`},400);
   const recipients=[...new Set<string>(d.user_ids)];
   if(recipients.length!==d.user_ids.length)return reply({message:'收件人存在重复，请重新选择'},400);
   let expires:string|null=null;
   if(d.expires_at!==null&&d.expires_at!==undefined){
    if(typeof d.expires_at!=='string'||!ISO.test(d.expires_at)||!Number.isFinite(Date.parse(d.expires_at)))return reply({message:'有效期须为 ISO8601 时间或留空'},400);
    expires=new Date(d.expires_at).toISOString();
    if(expires!==d.expires_at)return reply({message:'有效期格式无效'},400);
    if(expires<=at)return reply({message:'有效期必须晚于当前时间'},400);
   }
   const actor=await auditActor(r,e);
   const previous=await e.DB.prepare(`SELECT ${projection} FROM notifications n WHERE n.request_id=?`).bind(d.request_id).first<Row>();
   if(previous){const same=previous.actor===actor&&previous.title===title&&previous.body===body&&(previous.expires_at??null)===expires&&previous.reason===d.reason.trim()
     &&JSON.stringify(await recipientsOf(e,previous.id))===JSON.stringify([...recipients].sort());
    return same?reply({success:true,replayed:true,notification:present(previous)}):reply({message:'重复操作编号不匹配',error_code:'request_conflict'},409);}
   const existing=await e.DB.prepare(`SELECT id FROM users WHERE id IN (${recipients.map(()=>'?').join(',')})`).bind(...recipients).all<Row>();
   if(existing.results.length!==recipients.length)return reply({message:'收件人中有不存在的用户，请重新选择',error_code:'unknown_recipient'},400);
   try{await e.DB.batch([
    e.DB.prepare('INSERT INTO notifications(id,request_id,title,body,actor,reason,created_at,updated_at,expires_at,withdrawn_at,withdrawn_actor,revision) VALUES(?,?,?,?,?,?,?,?,?,NULL,NULL,1)').bind(d.request_id,d.request_id,title,body,actor,d.reason.trim(),at,at,expires),
    ...recipients.map(id=>e.DB.prepare('INSERT INTO notification_recipients(notification_id,user_id,read_at,created_at) VALUES(?,?,NULL,?)').bind(d.request_id,id,at))
   ]);}catch(error){if(/UNIQUE constraint/.test(String(error)))return reply({message:'重复操作编号不匹配',error_code:'request_conflict'},409);throw error;}
   const created=await e.DB.prepare(`SELECT ${projection} FROM notifications n WHERE n.id=?`).bind(d.request_id).first<Row>();
   return reply({success:true,notification:present(created!)},201);
  }
  // ---- list --------------------------------------------------------------------------------
  if(path==='/v1/admin/notifications'&&r.method==='GET'){
   const page=Number(url.searchParams.get('page')||1);
   if(!Number.isSafeInteger(page)||page<1||page>100000)return reply({message:'页码无效'},400);
   const rows=await e.DB.prepare(`SELECT ${projection} FROM notifications n ORDER BY n.created_at DESC,n.id DESC LIMIT ? OFFSET ?`).bind(PAGE_SIZE+1,(page-1)*PAGE_SIZE).all<Row>();
   const total=await e.DB.prepare('SELECT COUNT(*) n FROM notifications').first<Row>();
   return reply({items:rows.results.slice(0,PAGE_SIZE).map(present),page,pageSize:PAGE_SIZE,hasMore:rows.results.length>PAGE_SIZE,total:Number(total?.n||0),asOf:at});
  }
  const match=path.match(/^\/v1\/admin\/notifications\/([-a-f0-9]{36})(\/recipients|\/withdraw)?$/);
  if(!match)return reply({message:'接口不存在'},404);
  const id=match[1],sub=match[2];
  // ---- recipient read/unread roster ---------------------------------------------------------
  if(!sub&&r.method==='GET'){
   const row=await e.DB.prepare(`SELECT ${projection} FROM notifications n WHERE n.id=?`).bind(id).first<Row>();
   if(!row)return reply({message:'通知不存在'},404);
   return reply({notification:present(row)});
  }
  if(sub==='/recipients'&&r.method==='GET'){
   const row=await e.DB.prepare('SELECT id FROM notifications WHERE id=?').bind(id).first<Row>();
   if(!row)return reply({message:'通知不存在'},404);
   const filter=url.searchParams.get('filter')||'all',page=Number(url.searchParams.get('page')||1);
   if(!['all','read','unread'].includes(filter)||!Number.isSafeInteger(page)||page<1||page>100000)return reply({message:'筛选条件无效'},400);
   const where=filter==='read'?'AND r.read_at IS NOT NULL':filter==='unread'?'AND r.read_at IS NULL':'';
   const rows=await e.DB.prepare(`SELECT r.user_id,r.read_at,r.created_at,u.account,u.email,u.display_name FROM notification_recipients r JOIN users u ON u.id=r.user_id WHERE r.notification_id=? ${where} ORDER BY r.read_at IS NOT NULL,r.read_at,u.account LIMIT ? OFFSET ?`).bind(id,PAGE_SIZE+1,(page-1)*PAGE_SIZE).all<Row>();
   const counts=await e.DB.prepare('SELECT COUNT(*) total,SUM(CASE WHEN read_at IS NOT NULL THEN 1 ELSE 0 END) read_count FROM notification_recipients WHERE notification_id=?').bind(id).first<Row>();
   const items=rows.results.slice(0,PAGE_SIZE).map(x=>({user_id:x.user_id,account:x.account,email:x.email,display_name:x.display_name,read_at:x.read_at??null,created_at:x.created_at}));
   return reply({items,filter,page,pageSize:PAGE_SIZE,hasMore:rows.results.length>PAGE_SIZE,total:Number(counts?.total||0),read_count:Number(counts?.read_count||0),unread_count:Number(counts?.total||0)-Number(counts?.read_count||0)});
  }
  // ---- edit (optimistic lock) ----------------------------------------------------------------
  if(!sub&&r.method==='POST'){
   const d=await readBody(r);
   const keys=d&&typeof d==='object'&&!Array.isArray(d)?Object.keys(d):null;
   if(!keys||keys.some(k=>!['title','body','expires_at','revision','reason','request_id'].includes(k)))return reply({message:'请求字段无效'},400);
   const title=typeof d.title==='string'?d.title.trim():'',body=typeof d.body==='string'?d.body.trim():'';
   if(!title||title.length>120||!body||body.length>MAX_BODY||typeof d.reason!=='string'||d.reason.trim().length<1||d.reason.trim().length>500||!Number.isSafeInteger(d.revision)||d.revision<1||(d.request_id!==undefined&&!UUID.test(d.request_id||'')))return reply({message:`请填写 1–120 字标题、1–${MAX_BODY} 字正文、修改原因和版本号`},400);
   let expires:string|null=null;
   if(d.expires_at!==null&&d.expires_at!==undefined){
    if(typeof d.expires_at!=='string'||!ISO.test(d.expires_at)||!Number.isFinite(Date.parse(d.expires_at)))return reply({message:'有效期须为 ISO8601 时间或留空'},400);
    expires=new Date(d.expires_at).toISOString();if(expires!==d.expires_at)return reply({message:'有效期格式无效'},400);
   }
   const actor=await auditActor(r,e);
   const requestId=d.request_id||crypto.randomUUID();
   const replayed=await e.DB.prepare('SELECT * FROM notification_changes WHERE id=?').bind(requestId).first<Row>();
   if(replayed){const same=replayed.notification_id===id&&replayed.actor===actor&&replayed.action==='edit'
     &&replayed.before_revision===d.revision&&replayed.reason===d.reason.trim()&&jsonField(replayed.after_value,'title')===title&&jsonField(replayed.after_value,'body')===body&&(jsonField(replayed.after_value,'expires_at')??null)===expires;
    return same?reply({success:true,replayed:true,notification:present((await e.DB.prepare(`SELECT ${projection} FROM notifications n WHERE n.id=?`).bind(id).first<Row>())!)}):reply({message:'重复操作编号不匹配',error_code:'request_conflict'},409);}
   const before=await e.DB.prepare('SELECT * FROM notifications WHERE id=?').bind(id).first<Row>();
   if(!before)return reply({message:'通知不存在',error_code:'not_found'},404);
   if(before.withdrawn_at)return reply({message:'通知已撤回，不能再修改',error_code:'notification_withdrawn'},409);
   if(before.revision!==d.revision)return reply({message:'通知已被其他人修改，请重新加载后核对',error_code:'notification_conflict'},409);
   try{await e.DB.prepare('INSERT INTO notification_changes(id,notification_id,actor,action,before_revision,before_snapshot,after_value,reason,created_at) VALUES(?,?,?,?,?,?,?,?,?)')
     .bind(requestId,id,actor,'edit',d.revision,snapshot(before),JSON.stringify({title,body,expires_at:expires,withdrawn_at:before.withdrawn_at??null}),d.reason.trim(),at).run();}
   catch(error){if(/notification_conflict|UNIQUE constraint/.test(String(error)))return reply({message:'通知已被修改，请重新加载后核对；未覆盖新内容。',error_code:'notification_conflict'},409);throw error;}
   return reply({success:true,notification:present((await e.DB.prepare(`SELECT ${projection} FROM notifications n WHERE n.id=?`).bind(id).first<Row>())!)});
  }
  // ---- withdraw ------------------------------------------------------------------------------
  if(sub==='/withdraw'&&r.method==='POST'){
   const d=await readBody(r);
   const keys=d&&typeof d==='object'&&!Array.isArray(d)?Object.keys(d):null;
   if(!keys||keys.some(k=>!['reason','request_id'].includes(k))||typeof d.reason!=='string'||d.reason.trim().length<1||d.reason.trim().length>500||(d.request_id!==undefined&&!UUID.test(d.request_id||'')))return reply({message:'请填写 1–500 字撤回原因'},400);
   const actor=await auditActor(r,e);
   const requestId=d.request_id||crypto.randomUUID();
   const replayed=await e.DB.prepare('SELECT * FROM notification_changes WHERE id=?').bind(requestId).first<Row>();
   const current=async()=>present((await e.DB.prepare(`SELECT ${projection} FROM notifications n WHERE n.id=?`).bind(id).first<Row>())!);
   if(replayed){const same=replayed.notification_id===id&&replayed.actor===actor&&replayed.action==='withdraw'&&replayed.reason===d.reason.trim();
    return same?reply({success:true,replayed:true,notification:await current()}):reply({message:'重复操作编号不匹配',error_code:'request_conflict'},409);}
   const before=await e.DB.prepare('SELECT * FROM notifications WHERE id=?').bind(id).first<Row>();
   if(!before)return reply({message:'通知不存在',error_code:'not_found'},404);
   // Recall is a one-way state: repeating it reports the existing state instead of moving the
   // timestamp, so the audit trail keeps the moment the notification actually became invisible.
   if(before.withdrawn_at)return reply({success:true,already_withdrawn:true,notification:await current()});
   try{await e.DB.prepare('INSERT INTO notification_changes(id,notification_id,actor,action,before_revision,before_snapshot,after_value,reason,created_at) VALUES(?,?,?,?,?,?,?,?,?)')
     .bind(requestId,id,actor,'withdraw',before.revision,snapshot(before),JSON.stringify({title:before.title,body:before.body,expires_at:before.expires_at??null,withdrawn_at:at}),d.reason.trim(),at).run();}
   catch(error){if(/notification_conflict|UNIQUE constraint/.test(String(error)))return reply({message:'通知已被修改，请重新加载后核对',error_code:'notification_conflict'},409);throw error;}
   return reply({success:true,notification:await current()});
  }
  return reply({message:'不支持的请求方式'},405);
 }catch{return reply({message:'通知服务暂不可用，请重新读取确认操作结果'},503);}
}
const snapshot=(row:Row)=>JSON.stringify({title:row.title,body:row.body,expires_at:row.expires_at??null,withdrawn_at:row.withdrawn_at??null});
const jsonField=(value:string,key:string):any=>{const v=JSON.parse(value)[key];return v===undefined?null:v;};
async function recipientsOf(e:Env,id:string){const rows=await e.DB.prepare('SELECT user_id FROM notification_recipients WHERE notification_id=? ORDER BY user_id').bind(id).all<Row>();return rows.results.map(x=>x.user_id);}
