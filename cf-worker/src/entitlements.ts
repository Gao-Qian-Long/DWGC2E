import type {PaymentEnv} from './payments/index.ts';
/** Compensation ledgers arrived in a later migration than this module. A deployment where that
 * table is missing must degrade the bonus to zero instead of failing every translation request. */
async function compensationQuota(e:Pick<PaymentEnv,'DB'>,userId:string,yearMonth:string){
 const read=async(table:string)=>{
  const row=await e.DB.prepare(`SELECT COALESCE(SUM(amount),0) n FROM ${table} WHERE user_id=? AND year_month=?`).bind(userId,yearMonth).first<{n:number}>();
  return Number(row?.n||0);
 };
 try{return await read('quota_compensations_v3');}
 catch{
  try{const legacy=await read('quota_compensations');console.error(JSON.stringify({event:'quota_compensation_fallback',source:'quota_compensations'}));return legacy;}
  catch{console.error(JSON.stringify({event:'quota_compensation_fallback',source:'none'}));return 0;}
 }
}
export async function entitlementSnapshot(e:Pick<PaymentEnv,'DB'>,userId:string,at=new Date().toISOString()) {
 const row=await e.DB.prepare(`SELECT COALESCE((SELECT is_super FROM user_admin_state WHERE user_id=u.id),0) is_super,s.plan_name,s.starts_at,s.expires_at,q.quota FROM users u LEFT JOIN subscriptions s ON s.user_id=u.id LEFT JOIN subscription_quotas q ON q.user_id=u.id WHERE u.id=?`).bind(userId).first<Record<string,any>>();
 const tier=row?.is_super?'max':['pro','max'].includes(row?.plan_name)&&(!row?.expires_at||Date.parse(row.expires_at)>Date.parse(at))?row!.plan_name:'free';
 const catalog=await e.DB.prepare('SELECT quota FROM plan_catalog WHERE id=?').bind(tier).first<{quota:number}>();
 const bonus=await compensationQuota(e,userId,at.slice(0,7));
 const planBase=!row?.is_super&&tier!=='free'&&row?.quota!=null?Number(row.quota):Number(catalog?.quota||0);
 const base=planBase+Number(bonus||0);
 const usage=await e.DB.prepare('SELECT chars_used FROM usage_monthly WHERE user_id=? AND year_month=?').bind(userId,at.slice(0,7)).first<{chars_used:number}>();
 const goUsed=await e.DB.prepare('SELECT COALESCE(SUM(x.amount),0) n FROM addon_allocations x JOIN translation_requests r ON r.user_id=x.user_id AND r.request_id=x.request_id WHERE x.user_id=? AND r.year_month=?').bind(userId,at.slice(0,7)).first<{n:number}>();
 const addons=await e.DB.prepare('SELECT order_no,quota,remaining,starts_at,expires_at FROM addon_balances WHERE user_id=? AND expires_at>? AND starts_at<=? ORDER BY expires_at,order_no').bind(userId,at,at).all<Record<string,any>>();
 const used=Number(usage?.chars_used||0),baseUsed=Math.max(0,used-Number(goUsed?.n||0)),addonRemaining=addons.results.reduce((n,a)=>n+Number(a.remaining),0);
 const remaining=Math.max(0,base-baseUsed)+addonRemaining;
 return {userId,asOf:at,subscription:{is_super:Boolean(row?.is_super),plan_name:tier,starts_at:row?.starts_at||null,expires_at:row?.is_super||tier==='free'?null:row?.expires_at||null,auto_renew:false,entitlements:[]},addons:addons.results,usage:{plan_quota:planBase,compensation_quota:Number(bonus||0),base_quota:base,base_used:baseUsed,addon_remaining:addonRemaining,monthly_quota:used+remaining,used,remaining,reset_at:new Date(Date.UTC(new Date(at).getUTCFullYear(),new Date(at).getUTCMonth()+1,1)).toISOString()}};
}
