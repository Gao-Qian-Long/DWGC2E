import type {SessionUser} from '../auth/sessions.ts';
type Env={DB:D1Database};
export async function accountDataRoute(r:Request,e:Env,user:SessionUser,origin:string,readBody:(r:Request)=>Promise<any>):Promise<Response|null>{
 const url=new URL(r.url),path=url.pathname;
 const reply=(data:unknown,status=200)=>new Response(JSON.stringify(data),{status,headers:{'content-type':'application/json','cache-control':'no-store','access-control-allow-origin':origin}});
 if(path==='/v1/terminology'&&r.method==='GET'){
  const row=await e.DB.prepare('SELECT entries_json FROM user_glossaries WHERE user_id=?').bind(user.user_id).first<{entries_json:string}>();
  return reply({items:row?JSON.parse(row.entries_json):[]});
 }
 if(path==='/v1/terminology'&&r.method==='POST'){
  const body=await readBody(r);
  if(!body||typeof body.source!=='string'||typeof body.target!=='string'||!body.source.trim()||!body.target.trim()||body.source.length>500||body.target.length>500||(body.note!==undefined&&(typeof body.note!=='string'||body.note.length>1000)))return reply({error_code:'invalid_request',message:'原文和译文需为 1–500 字，备注最多 1000 字'},400);
  const item={id:crypto.randomUUID(),source:body.source.trim(),target:body.target.trim(),note:body.note?.trim()||''};
  const result=await e.DB.batch([
   e.DB.prepare("INSERT OR IGNORE INTO user_glossaries(user_id,entries_json,updated_at) VALUES(?,'[]',?)").bind(user.user_id,new Date().toISOString()),
   e.DB.prepare("UPDATE user_glossaries SET entries_json=json_insert(entries_json,'$[#]',json(?)),updated_at=? WHERE user_id=? AND json_array_length(entries_json)<5000 AND NOT EXISTS(SELECT 1 FROM json_each(entries_json) WHERE json_extract(value,'$.source')=? AND json_extract(value,'$.target')=?)").bind(JSON.stringify(item),new Date().toISOString(),user.user_id,item.source,item.target)
  ]);
  return result[1].meta.changes?reply(item,201):reply({error_code:'glossary_conflict',message:'词条重复或词库已达到 5000 条上限'},409);
 }
 const match=path.match(/^\/v1\/terminology\/([a-f0-9-]{36})$/);
 if(match&&r.method==='DELETE'){
  const result=await e.DB.prepare("UPDATE user_glossaries SET entries_json=json_remove(entries_json,(SELECT '$['||key||']' FROM json_each(entries_json) WHERE json_extract(value,'$.id')=? LIMIT 1)),updated_at=? WHERE user_id=? AND EXISTS(SELECT 1 FROM json_each(entries_json) WHERE json_extract(value,'$.id')=?)").bind(match[1],new Date().toISOString(),user.user_id,match[1]).run();
  return result.meta.changes?reply({success:true}):reply({error_code:'not_found',message:'词条不存在'},404);
 }
 const projection=(x:any)=>({id:x.id,file_name:`文字翻译请求（${Number(x.chars)} 字符）`,status:'completed',source_language:x.src_lang,target_language:x.tgt_lang,created_at:x.created_at,characters:Number(x.chars),cached:!!x.cached,kind:'text_translation',download_url:null});
 if(path==='/v1/translation/history'&&r.method==='GET'){
  const before=(url.searchParams.get('before')||'9999').slice(0,150);
  const rows=await e.DB.prepare('SELECT id,src_lang,tgt_lang,chars,cached,created_at FROM usage_logs WHERE user_id=? AND (created_at||id)<? ORDER BY created_at DESC,id DESC LIMIT 51').bind(user.user_id,before).all();
  const items=rows.results.slice(0,50),last=items.at(-1) as any;
  return reply({items:items.map(projection),nextCursor:rows.results.length>50&&last?last.created_at+last.id:null});
 }
 const detail=path.match(/^\/v1\/translation\/tasks\/([a-f0-9-]{36})$/);
 if(detail&&r.method==='GET'){
  const row=await e.DB.prepare('SELECT id,src_lang,tgt_lang,chars,cached,created_at FROM usage_logs WHERE user_id=? AND id=?').bind(user.user_id,detail[1]).first();
  return row?reply(projection(row)):reply({error_code:'not_found',message:'记录不存在'},404);
 }
 return null;
}
