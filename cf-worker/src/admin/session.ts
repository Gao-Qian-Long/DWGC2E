import {clientAddress} from '../client-address.ts';
type Env={DB:D1Database;ADMIN_API_KEY?:string;CORS_ORIGINS?:string;WEB_PROXY_IDENTITY_KEY?:string};
const cookieName='__Secure-dwgc_admin';
const digest=async(s:string)=>Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(s))),x=>x.toString(16).padStart(2,'0')).join('');
export function validAdminKey(r:Request,e:Env){const a=r.headers.get('authorization')||'',b='Bearer '+(e.ADMIN_API_KEY||'');let diff=a.length^b.length;for(let i=0;i<b.length;i++)diff|=(a.charCodeAt(i)||0)^b.charCodeAt(i);return !!e.ADMIN_API_KEY&&e.ADMIN_API_KEY.length>=32&&diff===0;}
function token(r:Request){return (r.headers.get('cookie')||'').split(';').map(v=>v.trim()).find(v=>v.startsWith(cookieName+'='))?.slice(cookieName.length+1)||'';}
const sameOrigin=(r:Request,e:Env)=>r.headers.get('origin')===(e.CORS_ORIGINS||'https://cad.pocketter.dpdns.org');
export async function adminActor(r:Request,e:Env){
 if(validAdminKey(r,e)) return 'key:'+(await digest(e.ADMIN_API_KEY!)).slice(0,16);
 const value=token(r);
 return /^[a-f0-9]{64}$/.test(value) ? 'session:'+(await digest(value)).slice(0,16) : 'unknown';
}
export async function adminSessionAuthorized(r:Request,e:Env){
 if(validAdminKey(r,e))return true;
 if(!e.ADMIN_API_KEY||e.ADMIN_API_KEY.length<32)return false;
 if(!['GET','HEAD'].includes(r.method)&&!sameOrigin(r,e))return false;
 const value=token(r);if(!/^[a-f0-9]{64}$/.test(value))return false;
 const row=await e.DB.prepare('SELECT key_fingerprint FROM admin_sessions WHERE token_hash=? AND expires_at>?').bind(await digest(value),Math.floor(Date.now()/1000)).first<{key_fingerprint:string}>();
 return !!row&&row.key_fingerprint===await digest(e.ADMIN_API_KEY);
}
export async function adminRequestAuthorized(r:Request,e:Env){return validAdminKey(r,e)||await adminSessionAuthorized(r,e);}
/** The gate records which credential authenticated the request; an arbitrary client value is ignored. */
export async function auditActor(r:Request,e:Env){
 const provided=(r.headers.get('x-admin-actor')||'').match(/^(?:key|session):[a-f0-9]{16}$/);
 if(provided)return provided[0];
 return validAdminKey(r,e)&&e.ADMIN_API_KEY?'key:'+(await digest(e.ADMIN_API_KEY)).slice(0,16):'unknown';
}
/** Administrator sign-in is the only credential ceremony on the Worker: count every attempt per
 * address so a lost or rotated key cannot be guessed at line speed. Returns false when over budget. */
async function loginAttemptAllowed(r:Request,e:Env,limit:number){
 const address=await digest(await clientAddress(r,e)),seconds=Math.floor(Date.now()/1000);
 const row=await e.DB.prepare("INSERT INTO request_limits(key,window_start,count) VALUES(?,?,1) ON CONFLICT(key) DO UPDATE SET window_start=CASE WHEN window_start<=?-? THEN ? ELSE window_start END,count=CASE WHEN window_start<=?-? THEN 1 ELSE count+1 END RETURNING count")
  .bind('admin-login:'+address,seconds,seconds,600,seconds,seconds,600).first<{count:number}>();
 return Number(row?.count||0)<=limit;
}
export async function adminSessionRoute(r:Request,e:Env){ const reply=(d:unknown,status=200,cookie?:string)=>new Response(JSON.stringify(d),{status,headers:{'content-type':'application/json','cache-control':'no-store',...(cookie?{'set-cookie':cookie}:{})}});
 const attributes='; Path=/api/v1/admin; HttpOnly; Secure; SameSite=Strict';
 if(r.method==='POST'){
  // Failed attempts are counted durably (request_limits) and logged without credential material.
  if(!await loginAttemptAllowed(r,e,10))return reply({message:'登录尝试过于频繁，请稍后再试'},429);
  let credentials;try{credentials=await r.json() as {username?:string};}catch{console.error(JSON.stringify({event:'admin_login_failed',reason:'invalid_body'}));return reply({message:'请输入用户名 admin 和管理员密钥'},401);}
  if(credentials?.username!=='admin'||!validAdminKey(r,e)){console.error(JSON.stringify({event:'admin_login_failed',reason:'invalid_credentials'}));return reply({message:'用户名或管理员密钥不正确'},401);}
  const now=Math.floor(Date.now()/1000),expires=now+8*3600;
  const value=Array.from(crypto.getRandomValues(new Uint8Array(32)),x=>x.toString(16).padStart(2,'0')).join('');
  const old=token(r);const statements=[e.DB.prepare('DELETE FROM admin_sessions WHERE expires_at<=?').bind(now),e.DB.prepare('INSERT INTO admin_sessions VALUES(?,?,?,?)').bind(await digest(value),await digest(e.ADMIN_API_KEY!),now,expires)];
  if(/^[a-f0-9]{64}$/.test(old))statements.push(e.DB.prepare('DELETE FROM admin_sessions WHERE token_hash=?').bind(await digest(old)));
  await e.DB.batch(statements);
  return reply({authenticated:true,expires_at:new Date(expires*1000).toISOString()},200,cookieName+'='+value+attributes+'; Max-Age=28800');
 }
 if(r.method==='GET')return await adminSessionAuthorized(r,e)?reply({authenticated:true}):reply({authenticated:false,message:'请登录后台'},401);
 if(r.method==='DELETE'){
  if(!sameOrigin(r,e)&&!validAdminKey(r,e))return reply({message:'请求来源不匹配'},403);
  const value=token(r);if(/^[a-f0-9]{64}$/.test(value))await e.DB.prepare('DELETE FROM admin_sessions WHERE token_hash=?').bind(await digest(value)).run();
  return reply({success:true},200,cookieName+'='+attributes+'; Max-Age=0');
 }
 return reply({message:'不支持的请求方式'},405);
}
