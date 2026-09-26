import {adminRequestAuthorized,auditActor} from './session.ts';
type Env = {DB:D1Database; ADMIN_API_KEY?:string; CORS_ORIGINS?:string; WEB_PROXY_IDENTITY_KEY?:string; LATEST_VERSION?:string; DOWNLOAD_URL?:string; BACKUP_DOWNLOAD_URL?:string; RELEASE_NOTES?:string};
type Row = Record<string,any>;
export const controlsDefaults={registration_open:true,purchases_open:true,maintenance:false,device_wait_days:20};
export async function settings(e:Env,section:string):Promise<Row>{
 const row=await e.DB.prepare('SELECT value_json FROM operation_settings WHERE section=?').bind(section).first<{value_json:string}>();
 const defaults=section==='controls'?controlsDefaults:section==='release'?{latest_version:e.LATEST_VERSION||'2.1.1',download_url:e.DOWNLOAD_URL||'https://maplehouse.lanzoum.com/ilNZe3nhk7ne',backup_download_url:e.BACKUP_DOWNLOAD_URL||'',release_notes:e.RELEASE_NOTES||'',package_size:0,package_sha256:'',package_signature:'',package_type:'',signing_key_id:''}:{headline:'',description:'',announcement:'',announcement_start:'',announcement_end:'',help_text:'',contact_email:'',tutorial_url:''};
 return {...defaults,...JSON.parse(row?.value_json||'{}')};
}
export function safeUrl(value:unknown,optional=false){if(value===''&&optional)return true;if(typeof value!=='string'||value.length>2048)return false;try{const u=new URL(value);return u.protocol==='https:'&&!u.username&&!u.password&&!!u.hostname&&!/[\s\\]/.test(value);}catch{return false;}}
export function validSetting(section:string,d:Row){
 if(!d||typeof d!=='object'||Array.isArray(d))return false;
 const exact=(keys:string[])=>Object.keys(d).length===keys.length&&keys.every(k=>Object.hasOwn(d,k));
 const str=(k:string,n:number)=>typeof d[k]==='string'&&d[k].length<=n;
 if(section==='release'){const base=['latest_version','download_url','backup_download_url','release_notes'],secure=[...base,'package_size','package_sha256','package_signature','package_type','signing_key_id'];const shape=exact(base)||exact(secure);const metadata=!Object.hasOwn(d,'package_type')||(d.package_size===0&&d.package_sha256===''&&d.package_signature===''&&d.package_type===''&&d.signing_key_id==='')||(Number.isSafeInteger(d.package_size)&&d.package_size>0&&d.package_size<=2147483648&&/^[0-9a-fA-F]{64}$/.test(d.package_sha256)&&typeof d.package_signature==='string'&&d.package_signature.length>=40&&d.package_signature.length<=4096&&['zip','setup-exe'].includes(d.package_type)&&str('signing_key_id',100));return shape&&str('latest_version',80)&&/^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)*$/.test(d.latest_version)&&safeUrl(d.download_url)&&safeUrl(d.backup_download_url,true)&&str('release_notes',5000)&&metadata;}
 if(section==='controls')return exact(Object.keys(controlsDefaults))&&['registration_open','purchases_open','maintenance'].every(k=>typeof d[k]==='boolean')&&Number.isSafeInteger(d.device_wait_days)&&d.device_wait_days>=0&&d.device_wait_days<=365;
 if(section==='content')return exact(['headline','description','announcement','announcement_start','announcement_end','help_text','contact_email','tutorial_url'])&&str('headline',100)&&str('description',500)&&str('announcement',2000)&&str('help_text',10000)&&str('contact_email',254)&&(!d.contact_email||/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(d.contact_email))&&safeUrl(d.tutorial_url,true)&&['announcement_start','announcement_end'].every(k=>d[k]===''||typeof d[k]==='string'&&/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(d[k])&&Number.isFinite(Date.parse(d[k])))&&(!d.announcement_start||!d.announcement_end||d.announcement_start<d.announcement_end);
 return false;
}
async function read(r:Request){const reader=r.body?.getReader();if(!reader)throw Error();let n=0,text='';const decoder=new TextDecoder();while(true){const c=await reader.read();if(c.done)break;n+=c.value.length;if(n>40000){await reader.cancel();throw Error();}text+=decoder.decode(c.value,{stream:true});}return JSON.parse(text+decoder.decode());}
export async function operationsRoute(r:Request,e:Env):Promise<Response>{
 const reply=(d:unknown,status=200)=>new Response(JSON.stringify(d),{status,headers:{'content-type':'application/json','cache-control':'no-store','access-control-allow-origin':e.CORS_ORIGINS||'https://cad.pocketter.dpdns.org'}});
 const u=new URL(r.url),path=u.pathname;
 if(path==='/v1/site'&&r.method==='GET'){
  const [content,controls,release]=await Promise.all(['content','controls','release'].map(s=>settings(e,s)));
  const at=new Date().toISOString();if(content.announcement_start&&at<content.announcement_start||content.announcement_end&&at>=content.announcement_end)content.announcement='';
  return reply({content,controls,release});
 }
 if(!await adminRequestAuthorized(r,e))return reply({message:'管理员验证失败'},401);
 const actor=await auditActor(r,e);
 try{
 const changeId=path.match(/^\/v1\/admin\/operations\/changes\/([-a-f0-9]{36})$/)?.[1];
 if(changeId&&r.method==='GET'){const change=await e.DB.prepare('SELECT id,section,before_snapshot,value_json,created_at FROM operation_changes WHERE id=?').bind(changeId).first();return change?reply(change):reply({message:'记录不存在'},404);}
 if(path==='/v1/admin/operations/settings'&&r.method==='GET'){
  const rows=await e.DB.prepare('SELECT section,revision FROM operation_settings ORDER BY section').all<Row>();
  return reply({items:await Promise.all(rows.results.map(async row=>({...row,value:await settings(e,row.section)})))});
 }
 const section=path.match(/^\/v1\/admin\/operations\/settings\/(release|content|controls)$/)?.[1];
 if(section&&r.method==='POST'){
  let d;try{d=await read(r);}catch{return reply({message:'请求内容无效或过长'},400);}
  if(!d||Object.keys(d).some(k=>!['value','revision','request_id','reason'].includes(k))||!validSetting(section,d.value)||!Number.isSafeInteger(d.revision)||d.revision<1||!/^[-a-f0-9]{36}$/.test(d.request_id||'')||typeof d.reason!=='string'||d.reason.trim().length<1||d.reason.trim().length>500)return reply({message:'配置无效。请核对版本、HTTPS 链接、时间范围和修改原因；自助解绑等待天数须为 0–365 的整数。'},400);
  const value=JSON.stringify(Object.fromEntries(Object.keys(d.value).sort().map(k=>[k,d.value[k]])));
  const old=await e.DB.prepare('SELECT * FROM operation_changes WHERE id=?').bind(d.request_id).first<Row>();
  if(old)return old.actor===actor&&old.section===section&&old.before_revision===d.revision&&old.value_json===value&&old.reason===d.reason.trim()?reply({success:true,replayed:true}):reply({message:'重复操作编号不匹配'},409);
  const before=await e.DB.prepare('SELECT value_json FROM operation_settings WHERE section=?').bind(section).first<Row>();
  try{await e.DB.prepare('INSERT INTO operation_changes VALUES(?,?,?,?,?,?,?,?)').bind(d.request_id,section,actor,d.revision,before?.value_json||'{}',value,d.reason.trim(),new Date().toISOString()).run();}catch(error){if(/operation_conflict|UNIQUE constraint/.test(String(error)))return reply({message:'配置已经变化，请重新读取后核对；没有覆盖新配置。'},409);throw error;}
  return reply({success:true});
 }
 const term=(u.searchParams.get('q')||'').trim().slice(0,100),page=Math.max(1,Math.min(100000,Number(u.searchParams.get('page'))||1)),offset=(Math.floor(page)-1)*25;
 if(path==='/v1/admin/operations/orders'&&r.method==='GET'){
  const rows=await e.DB.prepare(`SELECT o.order_no,o.user_id,a.account,o.plan_name,o.status,o.create_state,o.amount_cents,o.payable_cents,o.created_at,o.paid_at,EXISTS(SELECT 1 FROM payment_settlements s WHERE s.order_no=o.order_no) AS settled,(SELECT note FROM operation_notes n WHERE n.kind='order' AND n.target_id=o.order_no ORDER BY created_at DESC,id DESC LIMIT 1) AS latest_note FROM orders o JOIN users a ON a.id=o.user_id WHERE (?='' OR o.order_no=? OR o.user_id=? OR instr(lower(a.account),lower(?))>0) ORDER BY o.created_at DESC,o.order_no DESC LIMIT 26 OFFSET ?`).bind(term,term,term,term,offset).all();return reply({items:rows.results.slice(0,25),hasMore:rows.results.length>25,page:Math.floor(page)});
 }
 if(path==='/v1/admin/operations/devices/revoke'&&r.method==='POST'){
  let d;try{d=await read(r);}catch{return reply({message:'无效请求'},400);}
  if(!d||Object.keys(d).some(k=>!['user_id','device_id','first_seen','reason','request_id'].includes(k))||!['user_id','device_id','first_seen'].every(k=>typeof d[k]==='string'&&d[k].length>0&&d[k].length<=128)||!Number.isFinite(Date.parse(d.first_seen))||typeof d.reason!=='string'||!d.reason.trim()||d.reason.trim().length>500||!/^[-a-f0-9]{36}$/.test(d.request_id||''))return reply({message:'请提供设备绑定版本、操作编号和解绑原因（1–500 字）'},400);
  const old=await e.DB.prepare('SELECT * FROM admin_device_revocations WHERE id=?').bind(d.request_id).first<Row>();
  if(old)return ['user_id','device_id','first_seen'].every(k=>old[k]===d[k])&&old.actor===actor&&old.reason===d.reason.trim()?reply({success:true,replayed:true}):reply({message:'操作编号已被使用，请重新核对'},409);
  try{await e.DB.prepare('INSERT INTO admin_device_revocations(id,user_id,device_id,first_seen,actor,reason,created_at) VALUES(?,?,?,?,?,?,?)').bind(d.request_id,d.user_id,d.device_id,d.first_seen,actor,d.reason.trim(),new Date().toISOString()).run();}
  catch(error){if(String(error).includes('device_binding_changed'))return reply({message:'设备绑定已变化或已解绑，请刷新后重新核对'},409);throw error;}
  return reply({success:true,device_id:d.device_id});
 }
 if(path==='/v1/admin/operations/devices'&&r.method==='GET'){
  const rows=await e.DB.prepare(`SELECT b.*,a.account FROM app_device_bindings b JOIN users a ON a.id=b.user_id WHERE (?='' OR b.user_id=? OR b.device_id=? OR instr(lower(a.account),lower(?))>0) ORDER BY b.first_seen DESC,b.device_id LIMIT 26 OFFSET ?`).bind(term,term,term,term,offset).all<Row>();const c=await settings(e,'controls');return reply({items:rows.results.slice(0,25).map(d=>({...d,unbind_available_at:Number.isFinite(Date.parse(d.first_seen))?new Date(Date.parse(d.first_seen)+c.device_wait_days*86400000).toISOString():null})),hasMore:rows.results.length>25,page:Math.floor(page)});
 }
 if(path==='/v1/admin/operations/usage'&&r.method==='GET'){
  if(!term)return reply({message:'请先填写完整用户编号'},400);
  const logs=await e.DB.prepare('SELECT request_id,year_month,reserved,billed,state,started_at,completed_at FROM translation_requests WHERE user_id=? ORDER BY started_at DESC,request_id DESC LIMIT 26 OFFSET ?').bind(term,offset).all();
  const addons=await e.DB.prepare('SELECT order_no,quota,remaining,starts_at,expires_at FROM addon_balances WHERE user_id=? ORDER BY expires_at DESC LIMIT 100').bind(term).all();const compensation=await e.DB.prepare('SELECT id,year_month,amount,reason,created_at FROM quota_compensations_v3 WHERE user_id=? ORDER BY created_at DESC LIMIT 100').bind(term).all();return reply({items:logs.results.slice(0,25),addons:addons.results,compensations:compensation.results,hasMore:logs.results.length>25,page:Math.floor(page)});
 }
 if(path==='/v1/admin/operations/feedback'&&r.method==='GET'){
  const rows=await e.DB.prepare(`SELECT f.*,COALESCE((SELECT status FROM operation_notes n WHERE n.kind='feedback' AND n.target_id=f.id ORDER BY created_at DESC,id DESC LIMIT 1),f.status) AS workflow_status,(SELECT note FROM operation_notes n WHERE n.kind='feedback' AND n.target_id=f.id ORDER BY created_at DESC,id DESC LIMIT 1) AS latest_note FROM feedback f WHERE (?='' OR instr(lower(email),lower(?))>0 OR id=?) ORDER BY created_at DESC,id DESC LIMIT 26 OFFSET ?`).bind(term,term,term,offset).all();return reply({items:rows.results.slice(0,25),hasMore:rows.results.length>25,page:Math.floor(page)});
 }
 if(path==='/v1/admin/operations/compensations'&&r.method==='POST'){
  let d;try{d=await read(r);}catch{return reply({message:'无效请求'},400);}
  const month=new Date().toISOString().slice(0,7);
  if(!d||typeof d.user_id!=='string'||d.user_id.length>100||!/^[-a-f0-9]{36}$/.test(d.request_id||'')||!Number.isSafeInteger(d.amount)||d.amount<1||d.amount>1000000000||typeof d.reason!=='string'||d.reason.trim().length<1||d.reason.length>500||typeof d.year_month!=='string')return reply({message:'请核对用户编号、正整数字符数和修改原因'},400);
  const old=await e.DB.prepare('SELECT * FROM quota_compensations_v3 WHERE id=?').bind(d.request_id).first<Row>();if(old)return old.actor===actor&&old.user_id===d.user_id&&old.amount===d.amount&&old.reason===d.reason.trim()&&old.year_month===d.year_month?reply({success:true,replayed:true}):reply({message:'操作编号已用于其他补偿'},409);
  if(d.year_month!==month)return reply({message:'月份已经变化，请重新读取后核对'},409);
  if(!await e.DB.prepare('SELECT 1 FROM users WHERE id=?').bind(d.user_id).first())return reply({message:'用户不存在'},404);
  await e.DB.prepare('INSERT INTO quota_compensations_v3 VALUES(?,?,?,?,?,?,?)').bind(d.request_id,d.user_id,month,d.amount,actor,d.reason.trim(),new Date().toISOString()).run();return reply({success:true});
 }
 if(path==='/v1/admin/operations/notes'&&r.method==='POST'){
  let d;try{d=await read(r);}catch{return reply({message:'无效请求'},400);}
  if(!d||!['order','feedback'].includes(d.kind)||typeof d.target_id!=='string'||d.target_id.length>100||typeof d.note!=='string'||d.note.trim().length<1||d.note.length>2000||!/^[-a-f0-9]{36}$/.test(d.request_id||'')||!(d.kind==='order'?['awaiting_provider','provider_replay_requested','escalated']:['new','processing','resolved']).includes(d.status))return reply({message:'请选择处理状态并填写备注'},400);
  const old=await e.DB.prepare('SELECT * FROM operation_notes WHERE id=?').bind(d.request_id).first<Row>();if(old)return ['kind','target_id','status'].every(k=>old[k]===d[k])&&old.note===d.note.trim()&&old.actor===actor?reply({success:true,replayed:true}):reply({message:'重复操作编号不匹配'},409);
  const table=d.kind==='order'?'orders':'feedback',column=d.kind==='order'?'order_no':'id';if(!await e.DB.prepare(`SELECT 1 FROM ${table} WHERE ${column}=?`).bind(d.target_id).first())return reply({message:'记录不存在'},404);
  const statements=[e.DB.prepare('INSERT INTO operation_notes VALUES(?,?,?,?,?,?,?)').bind(d.request_id,d.kind,d.target_id,actor,d.status,d.note.trim(),new Date().toISOString())];
  if(d.kind==='feedback')statements.push(e.DB.prepare('UPDATE feedback SET status=?,updated_at=? WHERE id=?').bind(d.status==='resolved'?'resolved':'new',new Date().toISOString(),d.target_id));
  await e.DB.batch(statements);return reply({success:true,settled:false});
 }
 if(path==='/v1/admin/operations/audit'&&r.method==='GET'){
  const rows=await e.DB.prepare(`WITH audit_primary AS MATERIALIZED (SELECT id,'配置 / '||section AS kind,actor,reason,created_at,before_snapshot,value_json AS after_snapshot FROM operation_changes UNION ALL SELECT id,'套餐 / '||plan_id,actor,reason,created_at,before_snapshot,json_object('price_cents',price_cents,'quota',quota,'duration_days',duration_days,'enabled',enabled) FROM plan_changes_v3 UNION ALL SELECT id,'会员 / '||user_id,actor,reason,created_at,before_snapshot,json_object('plan_name',plan_name,'expires_at',expires_at) FROM admin_membership_changes_v3), audit_secondary AS MATERIALIZED (SELECT id,'额度补偿 / '||user_id,actor,reason,created_at,'',json_object('year_month',year_month,'amount',amount) FROM quota_compensations_v3 UNION ALL SELECT id,'设备强制解绑 / '||user_id,actor,reason,created_at,json_object('device_id',device_id,'first_seen',first_seen),'设备已解绑，APP 会话已撤销' FROM admin_device_revocations UNION ALL SELECT id,kind||' / '||target_id,actor,note,created_at,'',status FROM operation_notes) SELECT * FROM (SELECT * FROM audit_primary UNION ALL SELECT * FROM audit_secondary UNION ALL SELECT id,'账户 / '||user_id,actor,reason,created_at,before_snapshot,action FROM admin_account_changes) ORDER BY created_at DESC,id DESC LIMIT 26 OFFSET ?`).bind(offset).all();return reply({items:rows.results.slice(0,25),hasMore:rows.results.length>25,page:Math.floor(page)});
 }
 return reply({message:'接口不存在'},404);
 }catch{return reply({message:'运营后台暂不可用，请重新读取确认操作结果'},503);}
}

