// Read-only verifier for protected Wrangler JSON snapshots, not a migration executor.
// Usage: node tools/verify-remote-schema.mjs <snapshot.json>
// Snapshot query: SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name
import {readFileSync} from 'node:fs';
import {DatabaseSync} from 'node:sqlite';
import assert from 'node:assert/strict';
const input=JSON.parse(readFileSync(process.argv[2],'utf8'));
const results=input.flatMap(x=>x.results||[]).filter(x=>x.type&&x.name&&x.sql);
const db=new DatabaseSync(':memory:');
try{
 db.exec(readFileSync(new URL('../schema.sql',import.meta.url),'utf8'));
 const expected=db.prepare("SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'").all();
 const normalize=s=>s.replace(/\s+/g,'').replace(/IFNOTEXISTS/gi,'').replaceAll('"','');
 for(const object of expected){const actual=results.find(x=>x.type===object.type&&x.name===object.name);assert.ok(actual,'Missing object: '+object.name);assert.equal(normalize(actual.sql),normalize(object.sql),'Unexpected definition: '+object.name)}
 console.log(JSON.stringify({canonicalObjects:expected.length,verified:true,extraObjects:results.filter(x=>!expected.some(y=>y.name===x.name)).map(x=>x.name)}));
}finally{db.close()}
