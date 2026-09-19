import {test} from 'node:test';
import assert from 'node:assert/strict';
import {setup} from './helpers/worker.mjs';
import {validSetting} from '../src/admin/operations.ts';
const key='admin-device-isolated-test-'.repeat(3);
test('admin force revoke is audited and atomic; users remain locked; retries cannot revoke a new binding',async t=>{
 const x=setup(t);x.env.ADMIN_API_KEY=key;
 const app=await x.login(),web=await x.login('web','install-1'),other=await x.login('app','install-2');
 const first=x.db.prepare("SELECT first_seen FROM app_device_bindings WHERE device_id='install-1'").get().first_seen;
 const body={user_id:'alice',device_id:'install-1',first_seen:first,reason:'设备丢失',request_id:crypto.randomUUID()};
 const admin=(b,token=key)=>x.request('/v1/admin/operations/devices/revoke',{method:'POST',token,body:b});
 assert.equal((await x.request('/v1/devices/revoke',{method:'POST',token:app.token,body:{device_id:'install-1',force:true}})).status,409);
 assert.equal((await admin(body,app.token)).status,401);
 assert.equal((await admin({...body,reason:''})).status,400);
 assert.equal((await admin({...body,first_seen:'2000-01-01'})).status,409);
 assert.equal((await admin(body)).status,200);
 assert.equal(x.db.prepare('SELECT applied FROM admin_device_revocations').get().applied,1);
 assert.equal((await x.request('/v1/devices',{token:app.token})).status,401);
 assert.equal((await x.request('/v1/devices',{token:web.token})).status,200);
 assert.equal((await x.request('/v1/devices',{token:other.token})).status,200);
 assert.equal((await(await admin(body)).json()).replayed,true);
 const rebound=await x.login();assert.equal(rebound.status,200);
 assert.equal((await(await admin(body)).json()).replayed,true);
 assert.equal(x.db.prepare("SELECT revoked FROM app_device_bindings WHERE device_id='install-1'").get().revoked,0);
 assert.throws(()=>x.db.prepare("UPDATE app_device_bindings SET revoked=1 WHERE device_id='install-1'").run(),/device_binding_locked/);
 assert.equal((await admin({...body,request_id:crypto.randomUUID()})).status,409);
 const audit=await(await x.request('/v1/admin/operations/audit',{token:key})).json();assert.ok(audit.items.some(i=>i.reason==='设备丢失'));
});
test('waiting days accepts integer 0–365, default policy remains 20',async t=>{
 const x=setup(t);x.env.ADMIN_API_KEY=key;const app=await x.login();
 assert.equal((await(await x.request('/v1/devices',{token:app.token})).json()).unbind_wait_days,20);
 const value={registration_open:true,purchases_open:true,maintenance:false,device_wait_days:0};
 for(const days of [-1,0.5,366,'20',null])assert.equal(validSetting('controls',{...value,device_wait_days:days}),false);
 for(const days of [0,1,19,20,365])assert.equal(validSetting('controls',{...value,device_wait_days:days}),true);
 assert.equal((await x.request('/v1/admin/operations/settings/controls',{method:'POST',token:key,body:{value,revision:1,reason:'允许即时换机',request_id:crypto.randomUUID()}})).status,200);
 x.db.prepare("UPDATE app_device_bindings SET first_seen='2026-01-01T00:00:00Z'").run();
 assert.equal((await x.request('/v1/devices/revoke',{method:'POST',token:app.token,body:{device_id:'install-1'}})).status,200);
});

test('0017 is repeatable, preserves existing data, and rolls back revoke if session invalidation fails',async t=>{
 const x=setup(t);x.env.ADMIN_API_KEY=key;await x.login();
 const {readFileSync}=await import('node:fs');const sql=readFileSync(new URL('../migrations/0017_admin_device_revocations.sql',import.meta.url),'utf8');
 const before=x.db.prepare('SELECT * FROM app_device_bindings').all();x.db.exec(sql);x.db.exec(sql);assert.deepEqual(x.db.prepare('SELECT * FROM app_device_bindings').all(),before);
 x.db.exec("CREATE TRIGGER reject_test_session BEFORE UPDATE OF revoked_at ON sessions BEGIN SELECT RAISE(ABORT,'test_disk_failure'); END");
 const response=await x.request('/v1/admin/operations/devices/revoke',{method:'POST',token:key,body:{user_id:'alice',device_id:'install-1',first_seen:before[0].first_seen,reason:'故障回滚',request_id:crypto.randomUUID()}});
 assert.equal(response.status,503);assert.equal(x.db.prepare('SELECT COUNT(*) n FROM admin_device_revocations').get().n,0);assert.deepEqual(x.db.prepare('SELECT * FROM app_device_bindings').all(),before);
});
