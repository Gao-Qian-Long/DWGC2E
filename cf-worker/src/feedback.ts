import { clientAddress } from './client-address.ts';
type Env={WEB_PROXY_IDENTITY_KEY?:string;DB:D1Database;ADMIN_API_KEY?:string;CORS_ORIGINS?:string};
const reply=(e:Env,x:unknown,status=200)=>new Response(JSON.stringify(x),{status,headers:{'content-type':'application/json','cache-control':'no-store','access-control-allow-origin':e.CORS_ORIGINS||'https://cad.pocketter.dpdns.org'}});
async function body(r:Request){const reader=r.body?.getReader();if(!reader)throw Error();let bytes=0,text='';const decoder=new TextDecoder();while(true){const x=await reader.read();if(x.done)break;bytes+=x.value.length;if(bytes>12000){await reader.cancel();throw Error();}text+=decoder.decode(x.value,{stream:true});}return JSON.parse(text+decoder.decode());}
async function hash(s:string){return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(s))),b=>b.toString(16).padStart(2,'0')).join('');}
async function limit(e:Env,key:string,max:number){const t=Math.floor(Date.now()/1000);const row=await e.DB.prepare('INSERT INTO request_limits(key,window_start,count) VALUES(?,?,1) ON CONFLICT(key) DO UPDATE SET count=CASE WHEN window_start<=? THEN 1 ELSE count+1 END,window_start=CASE WHEN window_start<=? THEN excluded.window_start ELSE window_start END RETURNING count').bind(key,t,t-3600,t-3600).first<{count:number}>();return !!row&&row.count<=max;}
export async function feedbackRoute(r:Request,e:Env){const path=new URL(r.url).pathname;
 if(path==='/v1/health'&&r.method==='GET'){try{await e.DB.prepare('SELECT 1 AS ok').first();return reply(e,{api:'operational',database:'operational',checkedAt:new Date().toISOString()});}catch{return reply(e,{api:'operational',database:'unavailable',checkedAt:new Date().toISOString()},503);}}
 if(path==='/v1/feedback'){
 if(r.method!=='POST')return reply(e,{message:'不支持的请求方式'},405);
 try{
 if(!await limit(e,'feedback:ip:'+await hash(await clientAddress(r,e)),10))return reply(e,{message:'提交过于频繁，请一小时后再试'},429);
 let d;try{d=await body(r);}catch{return reply(e,{message:'反馈格式错误或内容过长'},400);}
 if(!d||typeof d.email!=='string'||typeof d.message!=='string'||typeof d.category!=='string')return reply(e,{message:'请填写邮箱和问题描述'},400);
 const email=d.email.trim().toLowerCase(),message=d.message.trim();const categories=['installation','translation','compatibility','payment','suggestion','other'];
 if(email.length>254||!/^\S+@\S+\.\S+$/.test(email)||message.length<1||message.length>3000||!categories.includes(d.category))return reply(e,{message:'请输入有效邮箱、问题类型及 非空且不超过 3000 字的问题描述'},400);
 if(!await limit(e,'feedback:email:'+await hash(email),5))return reply(e,{message:'该邮箱提交过于频繁，请一小时后再试'},429);
 const id=crypto.randomUUID(),time=new Date().toISOString();const page=typeof d.page==='string'&&/^\/[a-zA-Z0-9/_\-.]*$/.test(d.page)?d.page.slice(0,160):'';
 await e.DB.prepare('INSERT INTO feedback(id,email,category,message,page,created_at,updated_at) VALUES(?,?,?,?,?,?,?)').bind(id,email,d.category,message,page,time,time).run();return reply(e,{id,message:'反馈已收到，请保留编号。需要补充信息时，我们会通过所填邮箱联系你。'},201);
 }catch{return reply(e,{message:'反馈暂时无法保存，请稍后再试；你的内容尚未提交成功'},503);}}
 if(path.startsWith('/v1/admin/feedback')){
 const actual=r.headers.get('authorization')||'',expected='Bearer '+(e.ADMIN_API_KEY||'');let diff=actual.length^expected.length;for(let i=0;i<expected.length;i++)diff|=(actual.charCodeAt(i)||0)^expected.charCodeAt(i);if(!e.ADMIN_API_KEY||e.ADMIN_API_KEY.length<32||diff)return reply(e,{message:'管理员验证失败'},401);
 if(path==='/v1/admin/feedback'&&r.method==='GET'){const before=new URL(r.url).searchParams.get('before')||'9999';const rows=await e.DB.prepare('SELECT * FROM feedback WHERE (created_at||id)<? ORDER BY created_at DESC,id DESC LIMIT 51').bind(before.slice(0,100)).all();const items=rows.results.slice(0,50) as {created_at:string;id:string}[];return reply(e,{items,nextCursor:rows.results.length>50?items.at(-1)!.created_at+items.at(-1)!.id:null});}
 const m=path.match(/^\/v1\/admin\/feedback\/([a-f0-9-]{36})$/);if(m&&r.method==='POST'){let d;try{d=await body(r);}catch{return reply(e,{message:'无效请求'},400);}if(!['new','resolved'].includes(d?.status))return reply(e,{message:'无效状态'},400);const result=await e.DB.prepare('UPDATE feedback SET status=?,updated_at=? WHERE id=?').bind(d.status,new Date().toISOString(),m[1]).run();return reply(e,{success:result.meta.changes>0},result.meta.changes?200:404);}
 return reply(e,{message:'接口不存在'},404);
 }
 return reply(e,{message:'接口不存在'},404);
}
