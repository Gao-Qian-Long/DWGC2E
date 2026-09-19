import { translationHistoryRoute } from './history.ts';
import { MAX_GLOSSARY_ENTRIES, applyGlossaryPatch } from './glossary.ts';
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
  let item: Record<string,unknown> & {source:string;target:string};
  try {
   [item]=applyGlossaryPatch([], [{id:crypto.randomUUID(),source:body.source.trim(),target:body.target.trim(),note:body.note?.trim()||'',
    category:body.category ?? '默认分类',folder:body.folder ?? '',enabled:body.enabled ?? true,
    source_lang:body.source_lang ?? '',target_lang:body.target_lang ?? '',direction_pending:body.direction_pending ?? !(body.source_lang && body.target_lang)}], []);
  } catch { return reply({error_code:'invalid_request',message:'术语分类、备注或语言方向无效，未保存'},400); }
  const result=await e.DB.batch([
   e.DB.prepare("INSERT OR IGNORE INTO user_glossaries(user_id,entries_json,updated_at) VALUES(?,'[]',?)").bind(user.user_id,new Date().toISOString()),
   e.DB.prepare("UPDATE user_glossaries SET entries_json=json_insert(entries_json,'$[#]',json(?)),updated_at=? WHERE user_id=? AND json_array_length(entries_json)<? AND NOT EXISTS(SELECT 1 FROM json_each(entries_json) WHERE json_extract(value,'$.source')=? AND json_extract(value,'$.target')=? AND coalesce(json_extract(value,'$.source_lang'),'')=? AND coalesce(json_extract(value,'$.target_lang'),'')=?)").bind(JSON.stringify(item),new Date().toISOString(),user.user_id,MAX_GLOSSARY_ENTRIES,item.source,item.target,item.source_lang,item.target_lang)
  ]);
  return result[1].meta.changes?reply(item,201):reply({error_code:'glossary_conflict',message:'词条重复或词库已达到 1000 条上限'},409);
 }
 const match=path.match(/^\/v1\/terminology\/([a-f0-9-]{36})$/);
 if(match&&r.method==='DELETE'){
  const result=await e.DB.prepare("UPDATE user_glossaries SET entries_json=json_remove(entries_json,(SELECT '$['||key||']' FROM json_each(entries_json) WHERE json_extract(value,'$.id')=? LIMIT 1)),updated_at=? WHERE user_id=? AND EXISTS(SELECT 1 FROM json_each(entries_json) WHERE json_extract(value,'$.id')=?)").bind(match[1],new Date().toISOString(),user.user_id,match[1]).run();
  return result.meta.changes?reply({success:true}):reply({error_code:'not_found',message:'词条不存在'},404);
 }
 return translationHistoryRoute(r,e,user.user_id,origin);
}
