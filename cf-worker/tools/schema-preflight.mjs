import {DatabaseSync} from 'node:sqlite';
import {readFileSync} from 'node:fs';
import {resolve} from 'node:path';
// Offline, metadata-only preflight. No Cloudflare credentials or writes are used.
const source=process.argv[2];
if(!source)throw Error('Usage: node tools/schema-preflight.mjs <wrangler-metadata-json>');
const results=JSON.parse(readFileSync(resolve(source),'utf8'));
const objects=results[0].results;
const db=new DatabaseSync(':memory:');
const ordered=[...objects].sort((a,b)=>({table:0,index:1,trigger:2}[a.type]??3)-({table:0,index:1,trigger:2}[b.type]??3));
for(const row of ordered)if(row.sql)db.exec(row.sql);
const before=db.prepare('SELECT name,type FROM sqlite_master WHERE sql IS NOT NULL ORDER BY name').all();
for(const name of ['0006_feedback.sql','0007_session_device_separation.sql'])db.exec(readFileSync(new URL('../migrations/'+name,import.meta.url),'utf8'));
const after=db.prepare('SELECT name,type FROM sqlite_master WHERE sql IS NOT NULL ORDER BY name').all();
const fresh=new DatabaseSync(':memory:');fresh.exec(readFileSync(new URL('../schema.sql',import.meta.url),'utf8'));
const tables=['app_device_bindings','session_contexts','device_migration_audit','feedback'];
for(const name of tables){const a=db.prepare(`PRAGMA table_info(${name})`).all(),b=fresh.prepare(`PRAGMA table_info(${name})`).all();if(JSON.stringify(a)!==JSON.stringify(b))throw Error('Fresh/upgrade mismatch: '+name);}
console.log(JSON.stringify({source:resolve(source),productionWrites:0,added:after.filter(a=>!before.some(b=>a.name===b.name)),newTableParity:tables,unresolved:'Existing payment schema/ledger drift must be reconciled separately; this is not approval to apply all migrations.'},null,2));
fresh.close();db.close();
