/** Admin-only pricing, immutable audit and optimistic concurrency. */
import {adminRequestAuthorized,auditActor} from './session.ts';
type Env={DB:D1Database;ADMIN_API_KEY?:string;CORS_ORIGINS?:string;WEB_PROXY_IDENTITY_KEY?:string};
export async function adminPlansRoute(r:Request,e:Env):Promise<Response> {
 const reply=(data:unknown,status=200)=>new Response(JSON.stringify(data),{status,headers:{'content-type':'application/json','cache-control':'no-store','access-control-allow-origin':e.CORS_ORIGINS||'https://cad.pocketter.dpdns.org'}});
 if(!await adminRequestAuthorized(r,e))return reply({message:'管理员验证失败'},401);
 const path=new URL(r.url).pathname;
 try {
 if(path==='/v1/admin/plans'&&r.method==='GET') {
  const plans=await e.DB.prepare("SELECT * FROM plan_catalog ORDER BY CASE id WHEN 'free' THEN 0 WHEN 'pro' THEN 1 WHEN 'max' THEN 2 ELSE 3 END").all();
  const history=await e.DB.prepare('SELECT * FROM plan_changes_v3 ORDER BY created_at DESC,id DESC LIMIT 30').all();return reply({plans:plans.results,history:history.results});
 }
 const match=path.match(/^\/v1\/admin\/plans\/(free|pro|max|go)$/);
 if(!match||r.method!=='POST')return reply({message:'接口不存在'},404);
 let body;try{const reader=r.body?.getReader();if(!reader)throw Error();let data='',size=0;const decoder=new TextDecoder();while(true){const chunk=await reader.read();if(chunk.done)break;size+=chunk.value.length;if(size>4096){await reader.cancel();throw Error();}data+=decoder.decode(chunk.value,{stream:true});}body=JSON.parse(data+decoder.decode());}catch{return reply({message:'请求格式无效或过长'},400);}
 const id=match[1];
 if(body?.name!==undefined&&(typeof body.name!=='string'||body.name.trim().length<1||body.name.trim().length>50)||body?.description!==undefined&&(typeof body.description!=='string'||body.description.length>1000))return reply({message:'套餐名称限 1–50 字，权益说明限 1000 字'},400);
 // A non-free tier below 1 yuan is a configuration error, not a promotion: it would be sold at
 // face value by the checkout path and only discovered when the first customer pays.
 if(!body||Object.keys(body).some(k=>!['price_cents','quota','duration_days','enabled','revision','reason','request_id','name','description'].includes(k))||![body.price_cents,body.quota,body.duration_days,body.revision].every(Number.isSafeInteger)||body.price_cents<0||body.price_cents>10000000||body.quota<0||body.quota>1000000000||body.duration_days<1||body.duration_days>366||body.revision<1||![0,1].includes(body.enabled)||typeof body.reason!=='string'||body.reason.trim().length<1||body.reason.trim().length>500||!/^[-a-f0-9]{36}$/.test(body.request_id||'')||(id==='free'?(body.price_cents!==0||body.enabled!==1):body.price_cents<100)||(body.enabled===1&&body.quota===0))return reply({message:'请填写有效价格、字符额度、1–366 天有效期和修改原因；Free 必须免费且启用；付费套餐价格不得低于 100 分（1 元），开放购买前须配置正额度。'},400);
 const actor=await auditActor(r,e);
 const previous=await e.DB.prepare('SELECT * FROM plan_changes_v3 WHERE id=?').bind(body.request_id).first<Record<string,any>>();
 if(previous){const same=previous.actor===actor&&previous.plan_id===id&&previous.before_revision===body.revision&&['price_cents','quota','duration_days','enabled'].every(k=>previous[k]===body[k])&&previous.reason===body.reason.trim()&&(previous.display_name??null)===(body.name?.trim()??null)&&(previous.description??null)===(body.description?.trim()??null);return same?reply({success:true,replayed:true}):reply({message:'操作编号已用于其他修改，请重新读取'},409);}
 const before=await e.DB.prepare('SELECT * FROM plan_catalog WHERE id=?').bind(id).first();
 try{await e.DB.prepare('INSERT INTO plan_changes_v3(id,actor,plan_id,before_revision,price_cents,quota,duration_days,enabled,reason,before_snapshot,created_at,display_name,description) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)').bind(body.request_id,actor,id,body.revision,body.price_cents,body.quota,body.duration_days,body.enabled,body.reason.trim(),JSON.stringify(before),new Date().toISOString(),body.name?.trim()??null,body.description?.trim()??null).run();}
 catch(error){if(/plan_conflict|UNIQUE constraint/.test(String(error)))return reply({message:'套餐已被修改，请重新读取后再保存；未覆盖新配置。'},409);throw error;}
 return reply({success:true});
 }catch{return reply({message:'套餐管理暂不可用，请重新读取确认结果'},503);}
}
