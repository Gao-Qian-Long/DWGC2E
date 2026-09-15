import {DatabaseSync} from 'node:sqlite';
import {readFileSync} from 'node:fs';
import {pbkdf2Sync} from 'node:crypto';
import worker from '../../src/index.ts';
export function setup(t) {
 const db=new DatabaseSync(':memory:'); db.exec(readFileSync(new URL('../../schema.sql',import.meta.url),'utf8'));t.after(()=>db.close());
 const DB={prepare(sql){return {sql,values:[],bind(...v){this.values=v;return this;},async first(){return db.prepare(sql).get(...this.values)||null;},async all(){return {results:db.prepare(sql).all(...this.values)};},async run(){return {success:true,meta:{changes:Number(db.prepare(sql).run(...this.values).changes)}};}};},async batch(statements){db.exec('BEGIN');try{const results=statements.map(s=>({success:true,meta:{changes:Number(db.prepare(s.sql).run(...s.values).changes)}}));db.exec('COMMIT');return results;}catch(e){db.exec('ROLLBACK');throw e;}}};
 const env={DB,PASSWORD_PEPPER:'test-only',SESSION_TTL_DAYS:'30'};
 const password='ExamplePassword1!',salt='test-salt';
 const hash='v2:'+salt+':'+pbkdf2Sync(password+'\\0'+env.PASSWORD_PEPPER,salt,100000,32,'sha256').toString('hex');
 for(const id of ['alice','bob'])db.prepare('INSERT INTO users(id,account,email,password_hash,created_at) VALUES(?,?,?,?,?)').run(id,id,id+'@example.com',hash,new Date().toISOString());
 const request=(path,{method='GET',body,token}={})=>worker.fetch(new Request('https://local.test'+path,{method,headers:{'content-type':'application/json',...(token?{authorization:'Bearer '+token}:{})},...(body!==undefined?{body:JSON.stringify(body)}:{})}),env);
 const login=async(kind='app',device='install-1',account='alice')=>{const response=await request('/v1/auth/'+(kind==='web'?'web/login':'login'),{method:'POST',body:{account,password,device_id:device,device_name:'Test PC'}});return {status:response.status,...await response.json()};};
 return {db,DB,env,request,login,password};
}
