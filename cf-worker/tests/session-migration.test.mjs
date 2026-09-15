import {test} from 'node:test';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {readFileSync} from 'node:fs';
const schema=readFileSync(new URL('../schema.sql',import.meta.url),'utf8');
const migration=readFileSync(new URL('../migrations/0007_session_device_separation.sql',import.meta.url),'utf8');
test('additive migration preserves old devices and sessions; unknown sessions are not promoted',t=>{
 const db=new DatabaseSync(':memory:');t.after(()=>db.close());db.exec(schema.slice(0,schema.indexOf('-- Additive model:')));
 db.exec("INSERT INTO users(id,account,password_hash,created_at) VALUES('u','u','unused','2026-09-15'); INSERT INTO devices(device_id,user_id,first_seen,last_seen) VALUES('web-candidate','u','',''); INSERT INTO sessions(id,user_id,token_hash,device_id,expires_at,created_at) VALUES('s','u','hash','web-candidate','2099-01-01','2026-09-15');");
 const old=db.prepare('SELECT * FROM sessions').all();db.exec(migration);db.exec(migration);
 assert.deepEqual(db.prepare('SELECT * FROM sessions').all(),old);
 assert.equal(db.prepare('SELECT COUNT(*) n FROM devices').get().n,1);
 assert.equal(db.prepare('SELECT COUNT(*) n FROM session_contexts').get().n,0);
 assert.equal(db.prepare('SELECT COUNT(*) n FROM app_device_bindings').get().n,0);
});
test('new session failure rolls back APP binding in the same transaction',t=>{
 const db=new DatabaseSync(':memory:');t.after(()=>db.close());db.exec(schema);db.exec("INSERT INTO users(id,account,password_hash,created_at) VALUES('u','u','unused','');");
 assert.throws(()=>{db.exec('BEGIN');try{db.exec("INSERT INTO app_device_bindings(user_id,device_id,first_seen,last_seen) VALUES('u','d','',''); INSERT INTO session_contexts VALUES('missing-session','app'); COMMIT;");}catch(e){db.exec('ROLLBACK');throw e;}},/FOREIGN KEY/);
 assert.equal(db.prepare('SELECT COUNT(*) n FROM app_device_bindings').get().n,0);
});
