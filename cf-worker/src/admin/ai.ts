import {allowedProviderHosts,allowedProviderSecrets,defaultPrompt,encryptProviderKey,probeProvider,providerEndpoint} from '../ai-router.ts';
import {adminRequestAuthorized,auditActor} from './session.ts';
type Env={DB:D1Database;AI_CONFIG_ENCRYPTION_KEY?:string;ADMIN_API_KEY?:string;CORS_ORIGINS?:string;AI_PROVIDER_SECRET_ALLOWLIST?:string;AI_PROVIDER_HOST_ALLOWLIST?:string};
type Row=Record<string,any>;
const reply=(value:unknown,status=200)=>new Response(JSON.stringify(value),{status,headers:{'content-type':'application/json; charset=utf-8','cache-control':'no-store'}});
const read=async(r:Request)=>{try{return await r.json() as Row;}catch{throw new Error('invalid_json');}};
const id=()=>crypto.randomUUID();
const now=()=>new Date().toISOString();
// Saving must apply the same host allowlist as routing, otherwise an URL is accepted here and
// only rejected later, at translation time, with no way for the operator to see why.
const validUrl=(value:unknown,e:Env)=>{if(typeof value!=='string'||value.length>2048)return false;try{providerEndpoint(value,allowedProviderHosts(e));return true;}catch{return false;}};
const providerView=(row:Row)=>({id:row.id,name:row.name,base_url:row.base_url,model:row.model,enabled:!!row.enabled,weight:row.weight,priority:row.priority,timeout_ms:row.timeout_ms,max_failures:row.max_failures,cooldown_seconds:row.cooldown_seconds,temperature:row.temperature,has_credential:!!(row.credential_ciphertext||row.secret_name),credential_source:row.secret_name?'worker_secret':row.credential_ciphertext?'encrypted_store':'missing',revision:row.revision,updated_at:row.updated_at,health:{consecutive_failures:Number(row.consecutive_failures||0),last_success_at:row.last_success_at||null,last_failure_at:row.last_failure_at||null,last_latency_ms:row.last_latency_ms??null,cooldown_until:row.cooldown_until||null,last_error_code:row.last_error_code||null}});
function validate(d:Row,e:Env,updating=false){
 if(!d||typeof d!=='object'||Array.isArray(d))return '配置格式无效';
 if(!updating||Object.hasOwn(d,'name'))if(typeof d.name!=='string'||!d.name.trim()||d.name.trim().length>80)return '名称需为 1–80 个字符';
 if(!updating||Object.hasOwn(d,'base_url'))if(!validUrl(d.base_url,e))return 'API 地址必须是 HTTPS，且主机需在允许列表内（AI_PROVIDER_HOST_ALLOWLIST）';
 if(!updating||Object.hasOwn(d,'model'))if(typeof d.model!=='string'||!d.model.trim()||d.model.length>200)return '模型名称无效';
 for(const [key,min,max] of [['weight',1,1000],['priority',1,1000],['timeout_ms',5000,120000],['max_failures',1,20],['cooldown_seconds',5,3600]] as const)
  if(Object.hasOwn(d,key)&&(!Number.isSafeInteger(d[key])||d[key]<min||d[key]>max))return `${key} 超出允许范围`;
 if(Object.hasOwn(d,'temperature')&&(typeof d.temperature!=='number'||d.temperature<0||d.temperature>2))return 'temperature 超出允许范围';
 if(Object.hasOwn(d,'api_key')&&(typeof d.api_key!=='string'||d.api_key.length<8||d.api_key.length>4096))return 'API 密钥无效';
 // Only explicitly allowed Worker Secret names may be referenced: an unrestricted name let a
 // provider read any secret in the environment. The message never lists the allowed names.
 if(Object.hasOwn(d,'secret_name')&&d.secret_name!==''&&(typeof d.secret_name!=='string'||!/^[A-Z][A-Z0-9_]{2,80}$/.test(d.secret_name)||!allowedProviderSecrets(e).has(d.secret_name)))return 'Worker Secret 未被允许用于模型服务，请改用密钥录入或联系运维添加白名单';
 if(Object.hasOwn(d,'enabled')&&typeof d.enabled!=='boolean')return 'enabled 必须为布尔值';
 if(Object.hasOwn(d,'clear_credential')&&typeof d.clear_credential!=='boolean')return 'clear_credential 必须为布尔值';
 const credentialActions=Number(typeof d.api_key==='string'&&!!d.api_key)+Number(typeof d.secret_name==='string'&&!!d.secret_name)+Number(d.clear_credential===true);
 if(credentialActions>1)return 'API 密钥、Worker Secret 和清除凭据不能同时设置';
 return '';
}
async function audit(e:Env,kind:string,target:string,actor:string,before:unknown,after:unknown,reason:string){await e.DB.prepare('INSERT INTO ai_config_changes(id,kind,target_id,actor,before_json,after_json,reason,created_at) VALUES(?,?,?,?,?,?,?,?)').bind(id(),kind,target,actor,JSON.stringify(before),JSON.stringify(after),reason,now()).run();}
async function saveCredential(d:Row,e:Env,current?:Row){
 if(typeof d.api_key==='string'&&d.api_key){const encrypted=await encryptProviderKey(d.api_key,e.AI_CONFIG_ENCRYPTION_KEY);return {ciphertext:encrypted.ciphertext,nonce:encrypted.nonce,secretName:null};}
 if(typeof d.secret_name==='string'&&d.secret_name)return {ciphertext:null,nonce:null,secretName:d.secret_name};
 if(d.clear_credential===true)return {ciphertext:null,nonce:null,secretName:null};
 return {ciphertext:current?.credential_ciphertext??null,nonce:current?.credential_nonce??null,secretName:current?.secret_name??null};
}
export async function adminAiRoute(r:Request,e:Env){
 const url=new URL(r.url),path=url.pathname;
 // The router gate protects this path today, but a module holding provider credentials and the
 // routing policy must verify its own caller instead of trusting whoever dispatched it.
 if(!await adminRequestAuthorized(r,e))return reply({message:'管理员登录已失效，请重新登录'},401);
 const actor=await auditActor(r,e);
 try{
  if(path==='/v1/admin/ai/providers'&&r.method==='GET'){
   const rows=await e.DB.prepare(`SELECT p.*,h.consecutive_failures,h.last_success_at,h.last_failure_at,h.last_latency_ms,h.cooldown_until,h.last_error_code FROM ai_providers p LEFT JOIN ai_provider_health h ON h.provider_id=p.id ORDER BY p.priority,p.name`).all<Row>();
   return reply({items:(rows.results||[]).map(providerView)});
  }
  if(path==='/v1/admin/ai/providers'&&r.method==='POST'){
   const d=await read(r),problem=validate(d,e);if(problem)return reply({message:problem},400);
   const credential=await saveCredential(d,e),providerId=id(),created=now();
   await e.DB.prepare(`INSERT INTO ai_providers(id,name,base_url,model,credential_ciphertext,credential_nonce,secret_name,enabled,weight,priority,timeout_ms,max_failures,cooldown_seconds,temperature,revision,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,1,?,?)`)
    .bind(providerId,d.name.trim(),String(d.base_url).replace(/\/$/,''),d.model.trim(),credential.ciphertext,credential.nonce,credential.secretName,d.enabled===false?0:1,d.weight??100,d.priority??100,d.timeout_ms??90000,d.max_failures??3,d.cooldown_seconds??60,d.temperature??0.1,created,created).run();
   const after=await e.DB.prepare('SELECT * FROM ai_providers WHERE id=?').bind(providerId).first<Row>();await audit(e,'provider_create',providerId,actor,null,providerView(after!),String(d.reason||'创建模型服务').slice(0,500));return reply(providerView(after!),201);
  }
  const match=path.match(/^\/v1\/admin\/ai\/providers\/([^/]+)$/);
  if(match&&r.method==='PUT'){
   const d=await read(r),problem=validate(d,e,true);if(problem)return reply({message:problem},400);
   const current=await e.DB.prepare('SELECT * FROM ai_providers WHERE id=?').bind(match[1]).first<Row>();if(!current)return reply({message:'模型服务不存在'},404);
   if(!Number.isSafeInteger(d.revision)||d.revision!==current.revision)return reply({message:'配置已被其他管理员修改，请刷新'},409);
   const credential=await saveCredential(d,e,current),next:Row={...current,...d,credential_ciphertext:credential.ciphertext,credential_nonce:credential.nonce,secret_name:credential.secretName};
   const result=await e.DB.prepare(`UPDATE ai_providers SET name=?,base_url=?,model=?,credential_ciphertext=?,credential_nonce=?,secret_name=?,enabled=?,weight=?,priority=?,timeout_ms=?,max_failures=?,cooldown_seconds=?,temperature=?,revision=revision+1,updated_at=? WHERE id=? AND revision=?`)
    .bind(String(next.name).trim(),String(next.base_url).replace(/\/$/,''),String(next.model).trim(),credential.ciphertext,credential.nonce,credential.secretName,next.enabled===false||next.enabled===0?0:1,next.weight,next.priority,next.timeout_ms,next.max_failures,next.cooldown_seconds,next.temperature,now(),match[1],current.revision).run();
   if(!result.meta.changes)return reply({message:'配置冲突，请刷新'},409);const after=await e.DB.prepare('SELECT * FROM ai_providers WHERE id=?').bind(match[1]).first<Row>();await audit(e,'provider_update',match[1],actor,providerView(current),providerView(after!),String(d.reason||'更新模型服务').slice(0,500));return reply(providerView(after!));
  }
  if(match&&r.method==='DELETE'){
   const d=await read(r),current=await e.DB.prepare('SELECT * FROM ai_providers WHERE id=?').bind(match[1]).first<Row>();if(!current)return reply({message:'模型服务不存在'},404);
   if(!Number.isSafeInteger(d.revision)||d.revision!==current.revision)return reply({message:'配置已变化，请刷新'},409);
   await e.DB.prepare('UPDATE ai_providers SET enabled=0,revision=revision+1,updated_at=? WHERE id=? AND revision=?').bind(now(),match[1],current.revision).run();const after=await e.DB.prepare('SELECT * FROM ai_providers WHERE id=?').bind(match[1]).first<Row>();await audit(e,'provider_disable',match[1],actor,providerView(current),providerView(after!),String(d.reason||'停用模型服务').slice(0,500));return reply({success:true});
  }
  const testMatch=path.match(/^\/v1\/admin\/ai\/providers\/([^/]+)\/test$/);
  if(testMatch&&r.method==='POST'){
   const provider=await e.DB.prepare('SELECT * FROM ai_providers WHERE id=?').bind(testMatch[1]).first<Row>();if(!provider)return reply({message:'模型服务不存在'},404);
   if(!provider.enabled||!(provider.credential_ciphertext||provider.secret_name))return reply({success:false,message:'请先启用并配置密钥'},409);
   try{const result=await probeProvider(e,testMatch[1]);return reply({success:true,latency_ms:result.latencyMs,context_version:result.contextVersion});}
   catch{return reply({success:false,error_code:'provider_probe_failed',message:'模型服务连接测试失败，请检查地址、模型、密钥与响应格式'},502);}
  }
  if(path==='/v1/admin/ai/policy'&&r.method==='GET'){
   const profile=await e.DB.prepare('SELECT * FROM ai_routing_profiles WHERE active=1 ORDER BY updated_at DESC,id LIMIT 1').first<Row>();const policy=profile?await e.DB.prepare('SELECT id,version,name,system_prompt,published,created_at FROM ai_prompt_policies WHERE id=?').bind(profile.prompt_policy_id).first<Row>():null;return reply({profile:profile||null,policy:policy?{...policy,system_prompt:policy.system_prompt}:null});
  }
  if(path==='/v1/admin/ai/publish'&&r.method==='POST'){
   const d=await read(r);if(typeof d.system_prompt!=='string'||d.system_prompt.trim().length<40||d.system_prompt.length>20000)return reply({message:'系统提示词长度无效'},400);if(typeof d.reason!=='string'||d.reason.trim().length<3||d.reason.length>500)return reply({message:'请填写发布原因'},400);
   const enabled=await e.DB.prepare('SELECT COUNT(*) n FROM ai_providers WHERE enabled=1 AND (credential_ciphertext IS NOT NULL OR secret_name IS NOT NULL)').first<Row>();if(Number(enabled?.n||0)<1)return reply({message:'至少需要一个已启用且有凭据的模型服务'},409);
   const previous=await e.DB.prepare('SELECT * FROM ai_routing_profiles WHERE active=1 ORDER BY updated_at DESC,id LIMIT 1').first<Row>();if(Object.hasOwn(d,'expected_active_profile_id')&&String(d.expected_active_profile_id||'')!==String(previous?.id||''))return reply({message:'配置已被其他管理员修改，请刷新'},409);const version=String(d.context_version||('ctx-'+Date.now()));if(!/^[a-zA-Z0-9._-]{3,100}$/.test(version))return reply({message:'上下文版本格式无效'},400);
   const policyId=id(),profileId=id(),timestamp=now();await e.DB.batch([e.DB.prepare('UPDATE ai_routing_profiles SET active=0 WHERE active=1'),e.DB.prepare('INSERT INTO ai_prompt_policies(id,version,name,system_prompt,published,created_at) VALUES(?,?,?,?,1,?)').bind(policyId,version,String(d.name||version).slice(0,100),d.system_prompt.trim(),timestamp),e.DB.prepare('INSERT INTO ai_routing_profiles(id,name,prompt_policy_id,context_version,active,created_at,updated_at) VALUES(?,?,?,?,1,?,?)').bind(profileId,String(d.name||version).slice(0,100),policyId,version,timestamp,timestamp)]);await audit(e,'profile_publish',profileId,actor,previous,{id:profileId,context_version:version,prompt_policy_id:policyId},d.reason.trim());return reply({success:true,context_version:version});
  }
  if(path==='/v1/admin/ai/rollback'&&r.method==='POST'){
   const d=await read(r);if(typeof d.profile_id!=='string'||typeof d.reason!=='string'||d.reason.trim().length<3)return reply({message:'请选择历史版本并填写原因'},400);const target=await e.DB.prepare('SELECT * FROM ai_routing_profiles WHERE id=?').bind(d.profile_id).first<Row>();if(!target)return reply({message:'历史配置不存在'},404);const previous=await e.DB.prepare('SELECT * FROM ai_routing_profiles WHERE active=1 ORDER BY updated_at DESC,id LIMIT 1').first<Row>();if(Object.hasOwn(d,'expected_active_profile_id')&&String(d.expected_active_profile_id||'')!==String(previous?.id||''))return reply({message:'配置已被其他管理员修改，请刷新'},409);await e.DB.batch([e.DB.prepare('UPDATE ai_routing_profiles SET active=0 WHERE active=1'),e.DB.prepare('UPDATE ai_routing_profiles SET active=1,updated_at=? WHERE id=?').bind(now(),target.id)]);await audit(e,'profile_rollback',target.id,actor,previous,target,d.reason.trim().slice(0,500));return reply({success:true,context_version:target.context_version});
  }
  if(path==='/v1/admin/ai/history'&&r.method==='GET'){const rows=await e.DB.prepare('SELECT id,kind,target_id,actor,before_json,after_json,reason,created_at FROM ai_config_changes ORDER BY created_at DESC,id DESC LIMIT 100').all<Row>();const profiles=await e.DB.prepare('SELECT id,name,context_version,active,created_at,updated_at FROM ai_routing_profiles ORDER BY created_at DESC LIMIT 50').all<Row>();return reply({changes:rows.results||[],profiles:profiles.results||[]});}
  return reply({message:'接口不存在'},404);
 }catch(error){const message=String(error);const code=message.includes('ai_encryption_key')?'encryption_unavailable':message.includes('invalid_json')?'invalid_request':message.includes('UNIQUE constraint failed')?'config_conflict':'ai_admin_failed';return reply({message:code==='encryption_unavailable'?'后台未配置模型密钥加密主密钥':code==='invalid_request'?'请求格式无效':code==='config_conflict'?'配置版本已存在或发生冲突':'模型配置操作失败',error_code:code},code==='encryption_unavailable'?503:code==='invalid_request'?400:code==='config_conflict'?409:500);}
}
export {defaultPrompt};
