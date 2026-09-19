// Read-only history projection: reuse the billing source of truth, never re-bill
// or replay the provider to populate a page. Legacy usage logs remain visible.
type Env = {DB:D1Database};
const historySql = `WITH history AS (
 SELECT 'request_'||request_id AS id,user_id,reserved AS requested_characters,billed AS characters,
 CASE WHEN state='reserved' THEN 'processing' WHEN response_status=200 AND billed=reserved THEN 'completed'
 WHEN response_status=200 AND billed>0 THEN 'partial' ELSE 'failed' END AS status,
 source_language,target_language,
 COALESCE(started_at,strftime('%Y-%m-%dT%H:%M:%fZ',expires_at-180,'unixepoch')) AS created_at,
 CASE WHEN started_at IS NULL THEN 'request_start_estimate' ELSE 'recorded' END AS timestamp_kind,0 AS cached,expires_at,state,
 started_at,completed_at,CASE WHEN completed_at IS NULL THEN NULL WHEN response_status=504 THEN 'expiry_deadline' ELSE 'recorded' END AS completion_kind
 FROM translation_requests WHERE user_id=?
 UNION ALL
 SELECT id,user_id,chars,chars,'completed',src_lang,tgt_lang,created_at,'recorded',cached,NULL,'settled',NULL,NULL,NULL
 FROM usage_logs WHERE user_id=?
)`;
function project(row:any) {
 const status=row.state==='reserved' && row.expires_at<=Math.floor(Date.now()/1000)?'expired':row.status;
 return {id:row.id,file_name:`文字翻译请求（已计费 ${Number(row.characters)} / 请求 ${Number(row.requested_characters)} 字符）`,
 status,source_language:row.source_language,target_language:row.target_language,created_at:row.created_at,
 timestamp_kind:row.timestamp_kind,started_at:row.started_at,completed_at:row.completed_at,completion_kind:row.completion_kind,characters:Number(row.characters),requested_characters:Number(row.requested_characters),
 cached:!!row.cached,kind:'text_translation',download_url:null};
}
export async function translationHistoryRoute(r:Request,e:Env,userId:string,origin:string):Promise<Response|null> {
 const url=new URL(r.url),path=url.pathname;
 const reply=(data:unknown,status=200)=>new Response(JSON.stringify(data),{status,headers:{'content-type':'application/json','cache-control':'no-store','access-control-allow-origin':origin}});
 if(r.method!=='GET')return null;
 if(path==='/v1/translation/history') {
  const before=url.searchParams.get('before')||'9999';
  if(before.length>256)return reply({error_code:'invalid_cursor',message:'分页标识无效，请刷新记录'},400);
  const result=await e.DB.prepare(historySql+' SELECT * FROM history WHERE (created_at||id)<? ORDER BY created_at DESC,id DESC LIMIT 51').bind(userId,userId,before).all();
  const rows=result.results.slice(0,50),last=rows.at(-1) as any;
  return reply({items:rows.map(project),nextCursor:result.results.length>50&&last?last.created_at+last.id:null});
 }
 const detail=path.match(/^\/v1\/translation\/tasks\/([a-zA-Z0-9_-]{8,136})$/);
 if(!detail)return null;
 const row=await e.DB.prepare(historySql+' SELECT * FROM history WHERE id=?').bind(userId,userId,detail[1]).first();
 return row?reply(project(row)):reply({error_code:'not_found',message:'记录不存在'},404);
}

