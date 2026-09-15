import {test} from 'node:test';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {readFileSync,readdirSync} from 'node:fs';
test('empty database full migration chain matches canonical schema including seeds and constraints',t=>{
 const a=new DatabaseSync(':memory:'),b=new DatabaseSync(':memory:');t.after(()=>{a.close();b.close();});
 b.exec(readFileSync(new URL('../schema.sql',import.meta.url),'utf8'));
 for(const file of readdirSync(new URL('../migrations/',import.meta.url)).filter(x=>x.endsWith('.sql')).sort())a.exec(readFileSync(new URL('../migrations/'+file,import.meta.url),'utf8'));
 const objects=db=>db.prepare("SELECT name,type FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY name").all();assert.deepEqual(objects(a),objects(b));
 for(const {name,type} of objects(a)){
  if(type==='table'){for(const pragma of ['table_info','foreign_key_list','index_list'])assert.deepEqual(a.prepare(`PRAGMA ${pragma}(${name})`).all(),b.prepare(`PRAGMA ${pragma}(${name})`).all(),name+':'+pragma);}
  if(type==='trigger'){const sql=db=>db.prepare('SELECT sql FROM sqlite_master WHERE name=?').get(name).sql.replace(/\s+/g,'');assert.equal(sql(a),sql(b),name);}
 }
 assert.deepEqual(a.prepare('SELECT * FROM plans ORDER BY id').all(),b.prepare('SELECT * FROM plans ORDER BY id').all());
 assert.deepEqual(a.prepare('PRAGMA foreign_key_check').all(),[]);
});
