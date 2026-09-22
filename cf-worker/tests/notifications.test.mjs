import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,readdirSync} from 'node:fs';
import {DatabaseSync} from 'node:sqlite';
import {setup} from './helpers/worker.mjs';

const KEY='notifications-admin-test-key-'.repeat(3);
const U1='11111111-1111-4111-8111-111111111111';
const U2='22222222-2222-4222-8222-222222222222';
const U3='33333333-3333-4333-8333-333333333333';

/** The shared fixture seeds 'alice'/'bob' with non-uuid ids; directed notifications address real
 *  account ids, so this adds three uuid users that share the fixture's password hash. */
function fixture(t){
 const x=setup(t);x.env.ADMIN_API_KEY=KEY;
 const hash=x.db.prepare("SELECT password_hash h FROM users WHERE id='alice'").get().h;
 for(const [id,account] of [[U1,'u1'],[U2,'u2'],[U3,'u3']])
  x.db.prepare('INSERT INTO users(id,account,email,password_hash,created_at) VALUES(?,?,?,?,?)').run(id,account,account+'@example.com',hash,new Date().toISOString());
 const admin=(path,body,method)=>x.request(path,{method:method||(body?'POST':'GET'),body,token:KEY});
 const asUser=(path,token,body)=>x.request(path,{method:body?'POST':'GET',body,token});
 return {...x,admin,asUser,hash};
}
const send=(x,over={})=>({title:'服务调整通知',body:'本周六 02:00–04:00 进行维护，期间翻译服务可能短暂不可用。',user_ids:[U1],expires_at:null,reason:'计划维护告知',request_id:crypto.randomUUID(),...over});
const future=minutes=>new Date(Date.now()+minutes*60000).toISOString();
const past=minutes=>new Date(Date.now()-minutes*60000).toISOString();

test('S8 the user feed requires a real session and never leaks across users',async t=>{
 const x=fixture(t);
 assert.equal((await x.request('/v1/notifications')).status,401);
 assert.equal((await x.request('/v1/notifications',{token:'not-a-session'})).status,401);
 assert.equal((await x.request('/v1/notifications/read',{method:'POST',token:'not-a-session',body:{ids:[crypto.randomUUID()]}})).status,401);
 const u1=await x.login('app','install-u1','u1'),u2=await x.login('app','install-u2','u2');
 assert.equal(u1.status,200);assert.equal(u2.status,200);
 const create=await x.admin('/v1/admin/notifications',send(x));
 assert.equal(create.status,201);
 const id=(await create.json()).notification.id;
 const mine=await(await x.asUser('/v1/notifications',u1.token)).json();
 assert.equal(mine.items.length,1);assert.equal(mine.items[0].id,id);assert.equal(mine.items[0].read_at,null);assert.equal(mine.unread_count,1);
 // Authorisation boundary: the recipient term in the SQL scopes every read to the caller.
 const theirs=await(await x.asUser('/v1/notifications',u2.token)).json();
 assert.deepEqual(theirs.items,[]);assert.equal(theirs.unread_count,0);
 // A foreign user cannot even write a receipt against somebody else's notification.
 const stolen=await(await x.asUser('/v1/notifications/read',u2.token,{ids:[id]})).json();
 assert.equal(stolen.marked,0);
 assert.equal(x.db.prepare('SELECT read_at FROM notification_recipients WHERE notification_id=? AND user_id=?').get(id,U1).read_at,null);
});

test('S8 read receipts are idempotent and never overwrite the first read time',async t=>{
 const x=fixture(t);
 const u1=await x.login('app','install-u1','u1'),u3=await x.login('app','install-u3','u3');
 const created=await(await x.admin('/v1/admin/notifications',send(x,{user_ids:[U1,U3]}))).json();
 const id=created.notification.id;
 assert.equal(created.notification.recipient_count,2);assert.equal(created.notification.read_count,0);
 assert.equal((await x.asUser('/v1/notifications/read',u1.token,{})).status,400);
 assert.equal((await x.asUser('/v1/notifications/read',u1.token,{ids:[]})).status,400);
 assert.equal((await x.asUser('/v1/notifications/read',u1.token,{ids:['not-a-uuid']})).status,400);
 assert.equal((await x.asUser('/v1/notifications/read',u1.token,{ids:Array.from({length:101},()=>crypto.randomUUID())})).status,400);
 const first=await(await x.asUser('/v1/notifications/read',u1.token,{ids:[id]})).json();
 assert.equal(first.success,true);assert.equal(first.marked,1);
 const stored=x.db.prepare('SELECT read_at FROM notification_recipients WHERE notification_id=? AND user_id=?').get(id,U1).read_at;
 assert.ok(stored&&Number.isFinite(Date.parse(stored)));
 // Replay: same ids, same user. The stored receipt must be byte-identical, not refreshed.
 const replay=await(await x.asUser('/v1/notifications/read',u1.token,{ids:[id,id]})).json();
 assert.equal(replay.marked,0);
 assert.equal(x.db.prepare('SELECT read_at FROM notification_recipients WHERE notification_id=? AND user_id=?').get(id,U1).read_at,stored);
 const feed=await(await x.asUser('/v1/notifications',u1.token)).json();
 assert.equal(feed.items[0].read_at,stored);assert.equal(feed.unread_count,0);
 // Only the caller's own receipt moves.
 assert.equal(x.db.prepare('SELECT read_at FROM notification_recipients WHERE notification_id=? AND user_id=?').get(id,U3).read_at,null);
 const roster=await(await x.admin(`/v1/admin/notifications/${id}/recipients`)).json();
 assert.equal(roster.total,2);assert.equal(roster.read_count,1);assert.equal(roster.unread_count,1);
 assert.deepEqual(roster.items.filter(i=>i.read_at===null).map(i=>i.user_id),[U3]);
 const read=await(await x.admin(`/v1/admin/notifications/${id}/recipients?filter=read`)).json();
 assert.deepEqual(read.items.map(i=>i.user_id),[U1]);
 assert.equal((await x.admin(`/v1/admin/notifications/${id}/recipients?filter=bogus`)).status,400);
});

test('S8 withdrawn notifications disappear for recipients but stay in the admin trail',async t=>{
 const x=fixture(t);
 const u1=await x.login('app','install-u1','u1');
 const id=(await(await x.admin('/v1/admin/notifications',send(x))).json()).notification.id;
 assert.equal((await(await x.asUser('/v1/notifications',u1.token)).json()).items.length,1);
 const withdrawn=await(await x.admin(`/v1/admin/notifications/${id}/withdraw`,{reason:'内容有误，撤回重发',request_id:crypto.randomUUID()})).json();
 assert.equal(withdrawn.success,true);assert.ok(withdrawn.notification.withdrawn_at);
 const feed=await(await x.asUser('/v1/notifications',u1.token)).json();
 assert.deepEqual(feed.items,[]);assert.equal(feed.unread_count,0);
 // Repeating the recall is a reported no-op, and it does not move the recall timestamp.
 const again=await(await x.admin(`/v1/admin/notifications/${id}/withdraw`,{reason:'再次尝试撤回',request_id:crypto.randomUUID()})).json();
 assert.equal(again.already_withdrawn,true);
 assert.equal(again.notification.withdrawn_at,withdrawn.notification.withdrawn_at);
 assert.equal((await x.admin(`/v1/admin/notifications/${id}/withdraw`,{request_id:crypto.randomUUID()})).status,400);
 assert.equal((await x.admin(`/v1/admin/notifications/${id}/withdraw`,{reason:'x'.repeat(501)})).status,400);
 // The audit row survives, so a recalled notice is still attributable.
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM notification_changes WHERE notification_id=? AND action='withdraw'").get(id).n,1);
 assert.equal((await(await x.admin(`/v1/admin/notifications/${id}`)).json()).notification.withdrawn_at,withdrawn.notification.withdrawn_at);
 // Editing a recalled notification is refused rather than silently resurrecting it.
 assert.equal((await x.admin(`/v1/admin/notifications/${id}`,{title:'新标题',body:'新正文',expires_at:null,revision:withdrawn.notification.revision,reason:'修改'})).status,409);
});

test('S8 expired notifications are withheld and the 2000-character body limit is enforced',async t=>{
 const x=fixture(t);
 const u1=await x.login('app','install-u1','u1');
 const soon=new Date(Date.now()+3600000).toISOString();
 const live=(await(await x.admin('/v1/admin/notifications',send(x,{expires_at:soon,user_ids:[U1,U3]}))).json()).notification;
 assert.equal(live.expires_at,soon);
 const expired=(await(await x.admin('/v1/admin/notifications',send(x,{expires_at:soon,user_ids:[U1]}))).json()).notification;
 // Age the second notification past its window; only the live one may still be returned.
 x.db.prepare('UPDATE notifications SET expires_at=? WHERE id=?').run(past(1),expired.id);
 const feed=await(await x.asUser('/v1/notifications',u1.token)).json();
 assert.deepEqual(feed.items.map(i=>i.id),[live.id]);assert.equal(feed.unread_count,1);
 // An expired notification cannot even be marked read, so no stale receipt is manufactured.
 assert.equal((await x.asUser('/v1/notifications/read',u1.token,{ids:[expired.id]})).status,200);
 assert.equal(x.db.prepare('SELECT read_at FROM notification_recipients WHERE notification_id=? AND user_id=?').get(expired.id,U1).read_at,null);
 // Body length: the site-wide announcement uses 2000 (admin/operations.ts:17) and this endpoint
 // must not widen it. 2000 is accepted, 2001 is rejected.
 assert.equal((await x.admin('/v1/admin/notifications',send(x,{body:'字'.repeat(2000),request_id:crypto.randomUUID()}))).status,201);
 assert.equal((await x.admin('/v1/admin/notifications',send(x,{body:'字'.repeat(2001),request_id:crypto.randomUUID()}))).status,400);
 assert.equal((await x.admin('/v1/admin/notifications',send(x,{title:'',request_id:crypto.randomUUID()}))).status,400);
 assert.equal((await x.admin('/v1/admin/notifications',send(x,{title:'标'.repeat(121),request_id:crypto.randomUUID()}))).status,400);
 assert.equal((await x.admin('/v1/admin/notifications',send(x,{expires_at:past(1),request_id:crypto.randomUUID()}))).status,400);
});

test('S8 admin create is idempotent, rejects unknown recipients and needs administrator credentials',async t=>{
 const x=fixture(t);
 assert.equal((await x.request('/v1/admin/notifications')).status,401);
 assert.equal((await x.request('/v1/admin/notifications',{method:'POST',body:send(x)})).status,401);
 assert.equal((await x.request('/v1/admin/notifications',{token:'regular-user-token'})).status,401);
 const body=send(x);
 const first=await(await x.admin('/v1/admin/notifications',body)).json();
 assert.equal(first.success,true);
 // Same request_id + same content replays instead of duplicating the send.
 const replay=await(await x.admin('/v1/admin/notifications',{...body,reason:'计划维护告知'})).json();
 assert.equal(replay.replayed,true);assert.equal(replay.notification.id,first.notification.id);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM notifications').get().n,1);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM notification_recipients').get().n,1);
 // Same request_id + different content is a conflict, never a second notification.
 assert.equal((await x.admin('/v1/admin/notifications',{...body,title:'另一个标题'})).status,409);
 assert.equal((await x.admin('/v1/admin/notifications',{...body,user_ids:[U2]})).status,409);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM notifications').get().n,1);
 for(const bad of [{...send(x),reason:''},{...send(x),reason:'x'.repeat(501)},{...send(x),request_id:'nope'},{...send(x),user_ids:[]},{...send(x),user_ids:[U1,U1]},{...send(x),user_ids:['nope']},{...send(x),user_ids:Array.from({length:501},()=>crypto.randomUUID())},{...send(x),evil:true},{...send(x),body:''}])
  assert.equal((await x.admin('/v1/admin/notifications',bad)).status,400);
 assert.equal((await x.admin('/v1/admin/notifications',send(x,{user_ids:['44444444-4444-4444-8444-444444444444']}))).status,400);
 assert.equal((await x.admin('/v1/admin/notifications/not-a-uuid')).status,404);
 // The {id} segment is a uuid; anything else is not a notification path at all, for every method.
 assert.equal((await x.admin('/v1/admin/notifications/not-a-uuid',{title:'a',body:'b',expires_at:null,revision:1,reason:'原因'})).status,404);
 assert.equal((await x.admin('/v1/admin/notifications/not-a-uuid/withdraw',{reason:'原因'})).status,404);
 assert.equal((await x.admin('/v1/admin/notifications/11111111-1111-4111-8111-ffffffffffff')).status,404);
});

test('S8 edits use optimistic concurrency and cannot overwrite a concurrent change',async t=>{
 const x=fixture(t);
 const u1=await x.login('app','install-u1','u1');
 const id=(await(await x.admin('/v1/admin/notifications',send(x))).json()).notification.id;
 const edit={title:'维护时间调整',body:'维护改到周日 02:00–04:00。',expires_at:null,revision:1,reason:'时间变更',request_id:crypto.randomUUID()};
 const ok=await(await x.admin(`/v1/admin/notifications/${id}`,edit)).json();
 assert.equal(ok.success,true);assert.equal(ok.notification.revision,2);assert.equal(ok.notification.title,'维护时间调整');
 assert.equal((await(await x.asUser('/v1/notifications',u1.token)).json()).items[0].title,'维护时间调整');
 // The stale revision is refused and the stored row keeps the newer content.
 assert.equal((await x.admin(`/v1/admin/notifications/${id}`,{...edit,title:'陈旧覆盖',request_id:crypto.randomUUID()})).status,409);
 assert.equal(x.db.prepare('SELECT title FROM notifications WHERE id=?').get(id).title,'维护时间调整');
 assert.equal((await x.admin(`/v1/admin/notifications/${id}`,{...edit,revision:99,request_id:crypto.randomUUID()})).status,409);
 // A replayed request_id returns the original result rather than applying a second edit.
 const replay=await(await x.admin(`/v1/admin/notifications/${id}`,edit)).json();
 assert.equal(replay.replayed,true);assert.equal(replay.notification.revision,2);
 // A guard trigger, not just the handler, protects the row: a hand-written conflicting ledger
 // entry is rejected by SQLite itself.
 assert.throws(()=>x.db.prepare('INSERT INTO notification_changes(id,notification_id,actor,action,before_revision,before_snapshot,after_value,reason,created_at) VALUES(?,?,?,?,?,?,?,?,?)')
  .run(crypto.randomUUID(),id,'key:test','edit',1,'{"title":"x","body":"y","expires_at":null,"withdrawn_at":null}','{"title":"z","body":"y","expires_at":null,"withdrawn_at":null}','原因',new Date().toISOString()),/notification_conflict/);
 assert.equal(x.db.prepare('SELECT revision FROM notifications WHERE id=?').get(id).revision,2);
});

test('S8 the admin list is paged, newest first, with read/recipient counters',async t=>{
 const x=fixture(t);
 for(const n of [0,1,2]) await x.admin('/v1/admin/notifications',send(x,{title:'通知 '+n,user_ids:[U1,U2],request_id:crypto.randomUUID()}));
 const u1=await x.login('app','install-u1','u1');
 const page=await(await x.admin('/v1/admin/notifications')).json();
 assert.equal(page.page,1);assert.equal(page.pageSize,25);assert.equal(page.hasMore,false);assert.equal(page.total,3);
 assert.deepEqual(page.items.map(i=>i.title),['通知 2','通知 1','通知 0']);
 assert.equal(page.items[0].recipient_count,2);assert.equal(page.items[0].read_count,0);
 const id=page.items[0].id;
 await x.asUser('/v1/notifications/read',u1.token,{ids:[id]});
 const after=await(await x.admin('/v1/admin/notifications')).json();
 assert.equal(after.items[0].read_count,1);assert.equal(after.items[0].recipient_count,2);
 for(const bad of ['0','-1','1e9','abc'])assert.equal((await x.admin(`/v1/admin/notifications?page=${bad}`)).status,400);
});

test('0023 is additive and idempotent and leaves every pre-existing row untouched',t=>{
 const directory=new URL('../migrations/',import.meta.url);
 const files=readdirSync(directory).filter(n=>n.endsWith('.sql')).sort();
 assert.equal(files[files.length-1],'0023_user_notifications.sql');
 const migration=readFileSync(new URL('0023_user_notifications.sql',directory),'utf8');
 const db=new DatabaseSync(':memory:');t.after(()=>db.close());
 db.exec('PRAGMA foreign_keys=ON');
 for(const name of files.filter(n=>n<'0023_user_notifications.sql'))db.exec(readFileSync(new URL(name,directory),'utf8'));
 assert.deepEqual(db.prepare("SELECT name FROM sqlite_master WHERE name IN ('notifications','notification_recipients','notification_changes')").all(),[]);
 // Populate the pre-migration database the way production is populated today, so the "existing
 // rows unchanged" clause is measured against real content rather than an empty schema.
 db.exec("INSERT INTO users(id,account,email,password_hash,created_at) VALUES('11111111-1111-4111-8111-111111111111','legacy','legacy@example.com','unused','2026-09-01')");
 db.exec("INSERT INTO usage_monthly VALUES('11111111-1111-4111-8111-111111111111','2026-09',321,100000,2)");
 db.exec("INSERT INTO subscriptions VALUES('11111111-1111-4111-8111-111111111111','pro','2026-09-01T00:00:00Z','2026-10-01T00:00:00Z',0,'2026-09-01T00:00:00Z')");
 const tables=db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name").all().map(r=>r.name);
 assert.equal(tables.includes('notifications'),false);
 const snapshot=()=>Object.fromEntries(tables.map(n=>[n,db.prepare(`SELECT * FROM "${n}" ORDER BY rowid`).all()]));
 const before=snapshot();
 db.exec(migration);
 // Applying the migration on a populated database must not move a single pre-existing row.
 assert.deepEqual(snapshot(),before);
 db.exec(migration);
 assert.deepEqual(snapshot(),before);
 assert.deepEqual(db.prepare('PRAGMA foreign_key_check').all(),[]);
 // DDL shape: the three additive objects exist, are empty, and carry the declared columns.
 for(const name of ['notifications','notification_recipients','notification_changes'])
  assert.equal(db.prepare(`SELECT COUNT(*) n FROM "${name}"`).get().n,0);
 const columns=n=>db.prepare(`PRAGMA table_info(${n})`).all().map(c=>c.name);
 assert.deepEqual(columns('notifications'),['id','request_id','title','body','actor','reason','created_at','updated_at','expires_at','withdrawn_at','withdrawn_actor','revision']);
 assert.deepEqual(columns('notification_recipients'),['notification_id','user_id','read_at','created_at']);
 assert.deepEqual(columns('notification_changes'),['id','notification_id','actor','action','before_revision','before_snapshot','after_value','reason','created_at']);
 const pk=db.prepare('PRAGMA table_info(notification_recipients)').all().filter(c=>c.pk>0).map(c=>c.name);
 assert.deepEqual(pk,['notification_id','user_id']);
 // Re-applying on a database that already holds notification data still changes nothing.
 const at=new Date().toISOString();
 db.prepare("INSERT INTO notifications VALUES('22222222-2222-4222-8222-222222222222','22222222-2222-4222-8222-222222222222','标题','正文','key:test','原因',?,?,NULL,NULL,NULL,1)").run(at,at);
 db.prepare("INSERT INTO notification_recipients VALUES('22222222-2222-4222-8222-222222222222','11111111-1111-4111-8111-111111111111',NULL,?)").run(at);
 const seeded=[...db.prepare('SELECT * FROM notifications').all(),...db.prepare('SELECT * FROM notification_recipients').all()];
 db.exec(migration);
 assert.deepEqual([...db.prepare('SELECT * FROM notifications').all(),...db.prepare('SELECT * FROM notification_recipients').all()],seeded);
 assert.deepEqual(snapshot(),before);
 // The trigger guard/apply pair is present and unique.
 assert.deepEqual(db.prepare("SELECT name FROM sqlite_master WHERE type='trigger' AND name LIKE 'notification_change_%' ORDER BY name").all().map(r=>r.name),['notification_change_apply','notification_change_guard']);
});

test('S8 notifications never enter the operation_settings content whitelist',async t=>{
 const x=fixture(t);
 const content=await(await x.request('/v1/site')).json();
 assert.equal(Object.hasOwn(content.content,'notifications'),false);
 const patch={value:{...content.content,notifications:[{title:'x'}]},revision:1,reason:'尝试把通知塞进公告配置',request_id:crypto.randomUUID()};
 assert.equal((await x.admin('/v1/admin/operations/settings/content',patch)).status,400);
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM operation_changes WHERE section='content'").get().n,0);
 assert.equal((await x.admin('/v1/admin/operations/settings')).status,200);
 // The notification module owns its own tables rather than borrowing the announcement section.
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM operation_settings WHERE section='content'").get().n,1);
});

test('directed notifications address the 64-char ids registration actually issues',async t=>{
 const x=fixture(t);
 // Registration mints ids as two de-hyphenated UUIDs concatenated (64 hex), so a UUID-shaped
 // check rejected every real account and the feature could never send anything.
 const hex64='a7f34b72138a42c0abc7653e394448304230937ffa764ede9d1325ad7a2bdb1a';
 x.db.prepare('INSERT INTO users(id,account,email,password_hash,created_at) VALUES(?,?,?,?,?)').run(hex64,'hex64','hex64@example.com',x.hash,new Date().toISOString());
 const created=await x.admin('/v1/admin/notifications',send(x,{user_ids:[hex64]}));
 assert.equal(created.status,201);
 assert.equal((await created.json()).notification.recipient_count,1);
 // A malformed id is still refused instead of silently dropping the recipient.
 assert.equal((await x.admin('/v1/admin/notifications',send(x,{user_ids:['not-an-id']}))).status,400);
});
