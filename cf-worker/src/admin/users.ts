/** Private user directory and audited membership administration. Never returns auth material. */
import {adminRequestAuthorized,auditActor} from './session.ts';
type Env = { DB: D1Database; ADMIN_API_KEY?: string; CORS_ORIGINS?: string; WEB_PROXY_IDENTITY_KEY?: string };
type Row = Record<string, any>;
const join = `FROM users a LEFT JOIN subscriptions s ON s.user_id=a.id LEFT JOIN usage_monthly u ON u.user_id=a.id AND u.year_month=?`;
const columns = `COALESCE((SELECT is_super FROM user_admin_state WHERE user_id=a.id),0) is_super,(SELECT deleted_at FROM user_admin_state WHERE user_id=a.id) deleted_at,(SELECT version FROM admin_account_snapshot WHERE id=a.id) account_version,COALESCE((SELECT quota FROM plan_catalog WHERE id='max'),0) max_quota,COALESCE((SELECT SUM(amount) FROM quota_compensations_v3 WHERE user_id=a.id AND year_month=strftime('%Y-%m','now')),0) compensation_quota,COALESCE((SELECT quota FROM plan_catalog WHERE id='free'),100000) free_quota,COALESCE((SELECT quota FROM subscription_quotas WHERE user_id=a.id),(SELECT quota FROM plan_catalog WHERE id=s.plan_name),1000000) paid_quota,
 COALESCE((SELECT SUM(remaining) FROM addon_balances WHERE user_id=a.id AND expires_at>strftime('%Y-%m-%dT%H:%M:%fZ','now')),0) addon_remaining,
 COALESCE((SELECT SUM(x.amount) FROM addon_allocations x JOIN translation_requests r ON r.user_id=x.user_id AND r.request_id=x.request_id WHERE x.user_id=a.id AND r.year_month=u.year_month),0) addon_used,
a.id,a.account,a.email,a.display_name,a.created_at,a.is_active,s.plan_name,s.starts_at,s.expires_at,COALESCE(u.chars_used,0) used,COALESCE(u.task_count,0) task_count,COALESCE((SELECT json_array(plan_name,starts_at,expires_at,auto_renew,updated_at) FROM subscriptions WHERE user_id=a.id),'null') version`;
const superActive = `EXISTS(SELECT 1 FROM user_admin_state WHERE user_id=a.id AND is_super=1)`;
const active = `(${superActive} OR (s.plan_name IN ('pro','max') AND (s.expires_at IS NULL OR s.expires_at>?)))`;
function present(row:Row, at:string) {
 const underlying_plan_name=row.plan_name||'free',underlying_expires_at=row.expires_at;if(row.is_super){row.plan_name='max';row.expires_at=null;row.paid_quota=row.max_quota;}
 const member=['pro','max'].includes(row.plan_name)&&(!row.expires_at||Date.parse(row.expires_at)>Date.parse(at));
 const base=Number(member?row.paid_quota:row.free_quota)+Number(row.compensation_quota||0),remaining=Math.max(0,base-(Number(row.used)-Number(row.addon_used)))+Number(row.addon_remaining);
 return {...row,underlying_plan_name,underlying_expires_at,account_status:row.deleted_at?'deleted':row.is_active?'enabled':'disabled',plan_name:row.plan_name||'free',membership:member?'active':['pro','max'].includes(row.plan_name)?'expired':'free',monthly_quota:Number(row.used)+remaining,remaining};
}
async function readBody(r:Request){const reader=r.body?.getReader();if(!reader)throw Error();let bytes=0,text='';const decoder=new TextDecoder();while(true){const x=await reader.read();if(x.done)break;bytes+=x.value.length;if(bytes>4096){await reader.cancel();throw Error();}text+=decoder.decode(x.value,{stream:true});}return JSON.parse(text+decoder.decode());}
export async function adminUsersRoute(r:Request,e:Env):Promise<Response> {
 const reply=(data:unknown,status=200)=>new Response(JSON.stringify(data),{status,headers:{'content-type':'application/json; charset=utf-8','cache-control':'no-store','access-control-allow-origin':e.CORS_ORIGINS||'https://cad.pocketter.dpdns.org'}});
 if(!await adminRequestAuthorized(r,e))return reply({message:'管理员验证失败'},401);
 const url=new URL(r.url),path=url.pathname,at=new Date().toISOString(),ym=at.slice(0,7);
 try {
 if(path==='/v1/admin/users'&&r.method==='GET'){
  const q=(url.searchParams.get('q')||'').trim();const status=url.searchParams.get('status')||'all',page=Number(url.searchParams.get('page')||1);
  if(q.length>100||!['all','active','expired','free','disabled','deleted','super'].includes(status)||!Number.isSafeInteger(page)||page<1||page>100000)return reply({message:'筛选条件无效'},400);
  const pattern='%'+q.replace(/[\\%_]/g,'\\$&')+'%';let where=`WHERE (a.account LIKE ? ESCAPE '\\' OR a.email LIKE ? ESCAPE '\\' OR a.display_name LIKE ? ESCAPE '\\' OR a.id=?)`;
  const args:any[]=[ym,pattern,pattern,pattern,q];
  if(status==='disabled')where+=' AND a.is_active=0 AND NOT EXISTS(SELECT 1 FROM user_admin_state WHERE user_id=a.id AND deleted_at IS NOT NULL)';
  if(status==='deleted')where+=' AND EXISTS(SELECT 1 FROM user_admin_state WHERE user_id=a.id AND deleted_at IS NOT NULL)';
  if(status==='super')where+=' AND EXISTS(SELECT 1 FROM user_admin_state WHERE user_id=a.id AND is_super=1)';
  if(status==='active'){where+=' AND '+active;args.push(at);}
  if(status==='expired'){where+=" AND s.plan_name IN ('pro','max') AND s.expires_at<=? AND NOT "+superActive;args.push(at);}
  if(status==='free')where+=" AND COALESCE(s.plan_name,'free') NOT IN ('pro','max') AND NOT "+superActive;
  const results=await e.DB.batch([
   e.DB.prepare(`SELECT COUNT(*) total,COALESCE(SUM(CASE WHEN ${active} THEN 1 ELSE 0 END),0) members,COALESCE(SUM(u.chars_used),0) used,COALESCE(SUM(u.task_count),0) tasks ${join}`).bind(at,ym),
   e.DB.prepare(`SELECT COUNT(*) total ${join} ${where}`).bind(...args),
   e.DB.prepare(`SELECT ${columns} ${join} ${where} ORDER BY a.created_at DESC,a.id DESC LIMIT 25 OFFSET ?`).bind(...args,(page-1)*25)
  ]);
  return reply({summary:(results[0].results as Row[])[0],total:(results[1].results as Row[])[0].total,items:(results[2].results as Row[]).map(row=>present(row,at)),page,pageSize:25,month:ym,asOf:at});
 }
 const match=path.match(/^\/v1\/admin\/users\/([^/]+)(\/(?:membership|account))?$/);
 if(!match)return reply({message:'接口不存在'},404);
 const id=decodeURIComponent(match[1]);if(id.length>100)return reply({message:'用户编号无效'},400);
 if(r.method==='GET'&&!match[2]){
  const row=await e.DB.prepare(`SELECT ${columns} ${join} WHERE a.id=?`).bind(ym,id).first<Row>();if(!row)return reply({message:'用户不存在'},404);
  const history=await e.DB.prepare('SELECT id,actor,before_snapshot,plan_name,expires_at,reason,created_at FROM admin_membership_changes_v3 WHERE user_id=? ORDER BY created_at DESC,id DESC LIMIT 30').bind(id).all();
  const usage=await e.DB.prepare('SELECT year_month,chars_used,task_count FROM usage_monthly WHERE user_id=? ORDER BY year_month DESC LIMIT 12').bind(id).all();
  const accountHistory=await e.DB.prepare('SELECT id,actor,action,reason,created_at FROM admin_account_changes WHERE user_id=? ORDER BY created_at DESC,id DESC LIMIT 30').bind(id).all();
  return reply({accountHistory:accountHistory.results,user:present(row,at),history:history.results,usage:usage.results,month:ym});
 }
 if(r.method==='POST'&&match[2]==='/account'){
  let d;try{d=await readBody(r);}catch{return reply({message:'请求格式无效'},400);}
  if(!d||!['disable','enable','delete','restore','grant_super','revoke_super','revoke_sessions'].includes(d.action)||typeof d.version!=='string'||d.version.length>1000||typeof d.reason!=='string'||d.reason.trim().length<1||d.reason.trim().length>500||!/^[-a-f0-9]{36}$/.test(d.request_id||''))return reply({message:'请填写操作、原因（1–500 字）和版本信息'},400);
  const actor=await auditActor(r,e);
  const previous=await e.DB.prepare('SELECT * FROM admin_account_changes WHERE id=?').bind(d.request_id).first<Row>();
  if(previous){if(previous.user_id!==id||previous.actor!==actor||previous.action!==d.action||previous.before_snapshot!==d.version||previous.reason!==d.reason.trim())return reply({message:'重复操作编号不匹配'},409);return reply({success:true,replayed:true});}
  if(!await e.DB.prepare('SELECT id FROM users WHERE id=?').bind(id).first())return reply({message:'用户不存在'},404);
  try{await e.DB.prepare('INSERT INTO admin_account_changes(id,user_id,actor,before_snapshot,action,reason,created_at) VALUES(?,?,?,?,?,?,?)').bind(d.request_id,id,actor,d.version,d.action,d.reason.trim(),at).run();}
  catch(error){if(/account_conflict|account_state_invalid|UNIQUE constraint/.test(String(error)))return reply({message:'账户状态已变化，请重新读取后核对。已删除账户须先恢复，恢复后仍需启用。'},409);if(String(error).includes('max_quota_missing'))return reply({message:'请先配置 Max 额度'},400);throw error;}
  return reply({success:true});
 }
 if(r.method==='POST'&&match[2]==='/membership'){
  if(await e.DB.prepare('SELECT 1 FROM user_admin_state WHERE user_id=? AND deleted_at IS NOT NULL').bind(id).first())return reply({message:'请先恢复已删除账户'},409);
  let d;try{d=await readBody(r);}catch{return reply({message:'请求格式无效或内容过长'},400);}
  if(!d||!['free','pro','max'].includes(d.plan_name)||typeof d.version!=='string'||d.version.length>1000||typeof d.reason!=='string'||d.reason.trim().length<1||d.reason.trim().length>500||!/^[-a-f0-9]{36}$/.test(d.request_id||''))return reply({message:'请填写有效的会员状态、修改原因（1–500 字）和版本信息'},400);
  let expiry:string|null=null;
  if(d.plan_name!=='free'){
   if(typeof d.expires_at!=='string'||!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(d.expires_at)||!Number.isFinite(Date.parse(d.expires_at)))return reply({message:'请填写有效的到期时间'},400);
   expiry=new Date(d.expires_at).toISOString();if(expiry!==d.expires_at||expiry.slice(0,4)<'2000'||expiry.slice(0,4)>'2100')return reply({message:'到期时间须在 2000–2100 年之间'},400);
  }else if(d.expires_at!==null)return reply({message:'免费账户不能设置会员到期时间'},400);
  if(d.plan_name==='max'&&!await e.DB.prepare("SELECT 1 FROM plan_catalog WHERE id='max' AND quota>0").first())return reply({message:'请先配置 Max 额度'},400);
  const actor=await auditActor(r,e);
  const previous=await e.DB.prepare('SELECT * FROM admin_membership_changes_v3 WHERE id=?').bind(d.request_id).first<Row>();
  if(previous){if(previous.user_id!==id||previous.actor!==actor||previous.before_snapshot!==d.version||previous.plan_name!==d.plan_name||previous.expires_at!==expiry||previous.reason!==d.reason.trim())return reply({message:'重复操作编号不匹配，请重新加载'},409);return reply({success:true,replayed:true});}
  if(!await e.DB.prepare('SELECT id FROM users WHERE id=?').bind(id).first())return reply({message:'用户不存在'},404);
  try{await e.DB.prepare('INSERT INTO admin_membership_changes_v3(id,user_id,actor,before_snapshot,plan_name,expires_at,reason,created_at) VALUES(?,?,?,?,?,?,?,?)').bind(d.request_id,id,actor,d.version,d.plan_name,expiry,d.reason.trim(),at).run();}
  catch(error){if(String(error).includes('membership_conflict')||String(error).includes('UNIQUE constraint'))return reply({message:'会员数据已变化，可能刚完成续费或已提交修改。请重新加载用户详情后核对，未覆盖新数据。'},409);throw error;}
  return reply({success:true});
 }
 return reply({message:'不支持的请求方式'},405);
 }catch{return reply({message:'用户管理服务暂不可用，请稍后重试；请重新读取数据确认操作结果'},503);}
}

