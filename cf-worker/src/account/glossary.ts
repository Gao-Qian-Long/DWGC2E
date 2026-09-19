// Compatibility boundary for APP whole-list uploads; no CAD/translation changes.
export const MAX_GLOSSARY_ENTRIES = 1000;
const UUID = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/;
type Entry = Record<string, unknown> & {source: string; target: string; id?: string};
type Stored = {entries_json: string; updated_at: string};
type Env = {DB: D1Database};
class GlossaryError extends Error {
  readonly code: string;
  readonly status: number;
  constructor(code: string, message: string, status = 409) { super(message); this.code = code; this.status = status; }
}
const conflict = () => new GlossaryError('glossary_conflict', '云端词库已变化或词条身份不明确，请重新读取并核对后上传。');
const object = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
async function storedEntries(row: Stored | null): Promise<Entry[]> {
  if (!row) return [];
  let value: unknown;
  try { value = JSON.parse(row.entries_json); } catch { throw new GlossaryError('glossary_data_invalid', '云端词库格式异常，已保留原数据，请联系支持。'); }
  if (!Array.isArray(value) || value.some(x => !object(x) || typeof x.source !== 'string' || typeof x.target !== 'string'))
    throw new GlossaryError('glossary_data_invalid', '云端词库格式异常，已保留原数据，请联系支持。');
  const ids = value.filter(x => x.id !== undefined).map(x => x.id);
  if (ids.some(id => typeof id !== 'string' || !UUID.test(id)) || new Set(ids).size !== ids.length)
    throw new GlossaryError('glossary_data_invalid', '云端词条标识异常，已保留原数据，请联系支持。');
  // Derive repeatable identities without writing on GET. Include position to preserve duplicates.
  return await Promise.all(value.map(async (entry, index) => {
    if (entry.id) return entry;
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(JSON.stringify([index, entry])));
    const h = Array.from(new Uint8Array(digest), b => b.toString(16).padStart(2, '0')).join('');
    return {...entry, id: h.slice(0,8)+'-'+h.slice(8,12)+'-5'+h.slice(13,16)+'-a'+h.slice(17,20)+'-'+h.slice(20,32)};
  }));
}
function validateInput(entries: unknown): Entry[] {
  if (!Array.isArray(entries) || entries.length > MAX_GLOSSARY_ENTRIES)
    throw new GlossaryError('glossary_limit', '云端术语库最多保存 1000 条', 400);
  return entries.map(x => {
    if (!object(x) || !['source', 'target'].every(k => typeof x[k] === 'string' && (x[k] as string).trim() && (x[k] as string).length <= 500)
      || Object.entries({category:128, folder:128, note:1000}).some(([k,max]) => x[k] !== undefined && (typeof x[k] !== 'string' || (x[k] as string).length > max))
      || (x.enabled !== undefined && typeof x.enabled !== 'boolean')
      || (x.id !== undefined && (typeof x.id !== 'string' || !UUID.test(x.id))))
      throw new GlossaryError('invalid_request', '词条格式无效：原文/译文 1–500 字，分类/目录最多 128 字，备注最多 1000 字；未保存任何修改。', 400);
    return {...x, source:(x.source as string).trim(), target:(x.target as string).trim()} as Entry;
  });
}
// Old APP DTOs omit id/note. Exact pair first, then unique source, never guess
// between multiple candidates. Existing server metadata is preserved if omitted.
function reconcile(incoming: Entry[], previous: Entry[]): Entry[] {
  const used = new Set<Entry>();
  const output = incoming.map(x => {
    let matches = x.id ? previous.filter(p => p.id === x.id) : previous.filter(p => p.source === x.source && p.target === x.target);
    if (!x.id && matches.length === 0) matches = previous.filter(p => p.source === x.source);
    if (matches.length > 1 || (x.id && matches.length !== 1) || (matches[0] && used.has(matches[0]))) throw conflict();
    const old = matches[0];
    if (old) used.add(old);
    return {...old, id:old?.id || crypto.randomUUID(), source:x.source, target:x.target,
      category:x.category ?? old?.category ?? '', folder:x.folder ?? old?.folder ?? '',
      enabled:x.enabled ?? old?.enabled ?? true, note:x.note ?? old?.note ?? ''};
  });
  if (new Set(output.map(x => x.source + '\0' + x.target)).size !== output.length)
    throw new GlossaryError('glossary_conflict', '上传中存在重复原文和译文，请先合并重复词条。');
  return output;
}
async function revision(row: Stored | null): Promise<string> {
  const bytes = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(JSON.stringify(row ? [row.entries_json,row.updated_at] : null)));
  return Array.from(new Uint8Array(bytes),b=>b.toString(16).padStart(2,'0')).join('');
}
export async function glossaryRoute(r: Request, e: Env, userId: string, origin: string, readBody: (r:Request)=>Promise<any>): Promise<Response> {
  const reply = (body: unknown, status = 200) => new Response(JSON.stringify(body), {status, headers:{'content-type':'application/json; charset=utf-8','cache-control':'no-store','access-control-allow-origin':origin}});
  try {
    if (!['GET','PUT','PATCH'].includes(r.method)) return reply({success:false,error_code:'method_not_allowed',message:'请求方法不支持'},405);
    let row = await e.DB.prepare('SELECT entries_json,updated_at FROM user_glossaries WHERE user_id=?').bind(userId).first<Stored>();
    const previous = await storedEntries(row);
    // Commit legacy identities once, atomically, so later web deletions cannot change surviving IDs.
    if (row && JSON.parse(row.entries_json).some((entry: Entry) => !entry.id)) {
      const serialized = JSON.stringify(previous);
      const migrated = await e.DB.prepare('UPDATE user_glossaries SET entries_json=?,updated_at=? WHERE user_id=? AND entries_json=? AND updated_at=?')
        .bind(serialized,row.updated_at,userId,row.entries_json,row.updated_at).run();
      if (!migrated.meta.changes) throw conflict();
      row = {...row,entries_json:serialized};
    }
    const token = await revision(row);
    if (r.method === 'GET') return reply({success:true,entries:previous,updated_at:row?.updated_at || null,revision:token,max_entries:MAX_GLOSSARY_ENTRIES});
    const body = await readBody(r);
    const patch = r.method === 'PATCH';
    if (patch && (body?.version !== 2 || typeof body.expected_revision !== 'string')) throw new GlossaryError('invalid_request','增量操作需要版本与修订号',400);
    const entries = validateInput(patch ? body?.upserts : body?.entries);
    if (body?.expected_revision !== undefined && (typeof body.expected_revision !== 'string' || !/^[a-f0-9]{64}$/.test(body.expected_revision)))
      return reply({success:false,error_code:'invalid_request',message:'词库版本标识无效'},400);
    if (body?.expected_revision !== undefined && body.expected_revision !== token) throw conflict();
    // Never silently shrink a legacy large glossary to the old APP's 1000-item view.
    if (!patch && previous.length > MAX_GLOSSARY_ENTRIES)
      throw new GlossaryError('glossary_limit', '现有云端词库超过 1000 条，已保留全部数据；请先备份并逐条整理，不能整库覆盖。');
    let clean: Entry[];
    if (patch) clean = applyGlossaryPatch(previous, entries, body.delete_ids);
    else {
      if (previous.some(x => x.source_lang || x.target_lang)) throw new GlossaryError('upgrade_required','词库已升级，请使用新版客户端增量同步，不能整库覆盖。');
      clean = reconcile(entries, previous);
    }
    const serialized = JSON.stringify(clean);
    const oldTime = Date.parse(row?.updated_at || '');
    const ts = new Date(Math.max(Date.now(), Number.isFinite(oldTime) ? oldTime + 1 : 0)).toISOString();
    // Compare-and-swap protects mutations between the read and write, including
    // concurrent web POST/DELETE. No retry with stale content, no schema migration.
    const written = row
      ? await e.DB.prepare('UPDATE user_glossaries SET entries_json=?,updated_at=? WHERE user_id=? AND entries_json=? AND updated_at=?').bind(serialized,ts,userId,row.entries_json,row.updated_at).run()
      : await e.DB.prepare('INSERT OR IGNORE INTO user_glossaries(user_id,entries_json,updated_at) VALUES(?,?,?)').bind(userId,serialized,ts).run();
    if (!written.meta.changes) throw conflict();
    return reply({success:true,entries:clean,updated_at:ts,revision:await revision({entries_json:serialized,updated_at:ts}),max_entries:MAX_GLOSSARY_ENTRIES});
  } catch (error) {
    if (error instanceof GlossaryError) return reply({success:false,error_code:error.code,message:error.message},error.status);
    throw error;
  }
}


const LANGUAGES = new Set(['ZH','ZH-TW','EN','JA','KO','RU','DE','FR','ES','PT','IT','NL','PL','TR','VI','TH','ID','AR']);
/** Pure atomic patch plan: never infer deletion from omitted entries. */
export function applyGlossaryPatch(previous: Entry[], incoming: Entry[], deletes: unknown): Entry[] {
  if (!Array.isArray(deletes) || deletes.some(id => typeof id !== 'string' || !UUID.test(id)) || new Set(deletes).size !== deletes.length) throw new GlossaryError('invalid_request','删除标识无效',400);
  const ids = new Set<string>();
  const clean = validateInput(incoming).map(x => {
    if (!x.id || ids.has(x.id) || deletes.includes(x.id)) throw conflict(); ids.add(x.id);
    if (typeof x.direction_pending !== 'boolean' || typeof x.source_lang !== 'string' || typeof x.target_lang !== 'string') throw new GlossaryError('invalid_request','缺少语言方向',400);
    if (!x.direction_pending && (!LANGUAGES.has(x.source_lang) || !LANGUAGES.has(x.target_lang) || x.source_lang === x.target_lang)) throw new GlossaryError('invalid_request','语言方向无效',400);
    if (x.direction_pending && (x.source_lang !== '' || x.target_lang !== '')) throw new GlossaryError('invalid_request','待确认方向必须留空',400);
    return {...previous.find(p => p.id === x.id), ...x, category: (x.category as string)?.trim() || '默认分类'};
  });
  const result = previous.filter(p => !deletes.includes(p.id) && !ids.has(p.id!)).concat(clean);
  if (result.length > MAX_GLOSSARY_ENTRIES && result.length > previous.length) throw new GlossaryError('glossary_limit','云端术语库最多保存 1000 条',400);
  const key = (x: Entry) => JSON.stringify([x.source_lang || '',x.target_lang || '',x.source.trim().toUpperCase(),x.target.trim()]);
  for (const x of clean) if (result.filter(p => key(p) === key(x)).length > 1) throw new GlossaryError('glossary_conflict','已有相同方向、原文和译文，请核对关联。');
  return result;
}
