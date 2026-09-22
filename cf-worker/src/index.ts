import { protectGlossary } from "./translation-glossary.ts";
import {MAX_PROVIDER_RESULT_CHARS,routeCompletion,translationContext} from "./ai-router.ts";
import {adminAiRoute} from "./admin/ai.ts";
import {issueCaptcha,consumeCaptcha} from './captcha.ts';
import {adminActor,adminSessionRoute,adminSessionAuthorized,validAdminKey} from './admin/session.ts';
import {operationsRoute,settings} from './admin/operations.ts';
import {adminPlansRoute} from './admin/plans.ts';
import {entitlementSnapshot} from './entitlements.ts';
import { clientAddress } from './client-address.ts';
import { guardRequestBody, RequestBodyError } from './request-body.ts';
import { glossaryRoute } from './account/glossary.ts';
import { adminUsersRoute } from './admin/users.ts';
import { adminNotificationsRoute } from './admin/notifications.ts';
import { notificationsRoute } from './notifications/index.ts';
import {runPaymentRecovery,recoveryAdmin} from './payments/recovery.ts';
import { accountDataRoute } from './account/data.ts';
import { authenticate, issueSession, logout } from './auth/sessions.ts';
import { feedbackRoute } from './feedback.ts';
import { billingRoute, notifyPayment, inspectPayment, type PaymentEnv } from "./payments/index.ts";
import { deliverMail, mailProviders, selectMailProviders } from "./mail.ts";
interface Env extends PaymentEnv {
  WEB_PROXY_IDENTITY_KEY?: string;
  DB: D1Database;
  DEEPSEEK_API_KEY?: string;
  AI_CONFIG_ENCRYPTION_KEY?: string;
  JWT_SECRET?: string;
  PASSWORD_PEPPER: string;
  ADMIN_API_KEY?: string;
  CORS_ORIGINS?: string;
  DEFAULT_PLAN?: string;
  MAX_TRANSLATE_ITEMS?: string;
  MAX_TEXT_LENGTH?: string;
  TRANSLATE_RATE_REQUESTS?: string;
  TRANSLATE_RATE_CHARS?: string;
  SESSION_TTL_DAYS?: string;
  MAIL_PROVIDER?: string;
  MAIL_FALLBACK_ENABLED?: string;
  RESEND_API_KEY?: string;
  BREVO_API_KEY?: string;
  MAIL_FROM?: string; DEVICE_PLATFORM?: string;
  LATEST_VERSION?: string; DOWNLOAD_URL?: string; BACKUP_DOWNLOAD_URL?: string; RELEASE_NOTES?: string;
  WORKER_BUILD_ID?: string; WORKER_DEPLOYED_AT?: string; WORKER_SCHEMA_VERSION?: string; AI_ROUTING_VERSION?: string;
}
type J = Record<string, any>;
const now = () => new Date().toISOString();
const json = (x: any, status = 200, origin = "*") =>
  new Response(status === 204 ? null : JSON.stringify(x), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "access-control-allow-origin": origin,
      "access-control-allow-headers": "Content-Type, Authorization, Idempotency-Key",
      "access-control-allow-methods": "GET,POST,PUT,PATCH,DELETE,OPTIONS",
      "cache-control": "no-store",
    },
  });
const cors = (env: Env) =>
  env.CORS_ORIGINS || "https://cad.pocketter.dpdns.org";
const text = async (r: Request) => {
  try {
    return (await r.json()) as J;
  } catch {
    return null;
  }
};
const random = () =>
  crypto.randomUUID().replaceAll("-", "") +
  crypto.randomUUID().replaceAll("-", "");
async function digest(s: string) {
  const b = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return [...new Uint8Array(b)]
    .map((x) => x.toString(16).padStart(2, "0"))
    .join("");
}
// The legacy "dwgc2e-password-v1" fallback uses a fixed salt, so identical passwords produced a
// shared digest. Every successful login rewrites the hash with a per-account random salt (see the
// upgrade below); the fallback therefore only survives for accounts that have not logged in since
// per-account salts were introduced. Watch the 'password_hash_migrated_v2' log to know when it
// can be removed — do not remove it while that counter is still moving.
async function pass(p: string, pepper: string, salt = random()) {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(p + "\\0" + pepper), "PBKDF2", false, ["deriveBits"]);
  const bits = await crypto.subtle.deriveBits({ name: "PBKDF2", salt: new TextEncoder().encode(salt), iterations: 100000, hash: "SHA-256" }, key, 256);
  const hex = [...new Uint8Array(bits)].map(x => x.toString(16).padStart(2,"0")).join("");
  return salt === "dwgc2e-password-v1" ? hex + ":pbkdf2" : "v2:" + salt + ":" + hex;
}
// M-W3: digests are compared in constant time so a leaked hash table cannot be probed by
// response timing; the pattern matches payments/sign.ts and admin/session.ts.
function timingSafeEqualHex(expected: string, actual: string) {
  if (expected.length !== actual.length) return false;
  let diff = 0;
  for (let i = 0; i < expected.length; i++) diff |= expected.charCodeAt(i) ^ actual.charCodeAt(i);
  return diff === 0;
}
async function passwordMatches(password: string, pepper: string, stored: string) {
  const salt = stored.startsWith("v2:") ? stored.split(":")[1] : "dwgc2e-password-v1";
  return timingSafeEqualHex(await pass(password, pepper, salt), stored);
}
async function takeLimit(e: Env, key: string, seconds: number, max: number) {
  const timestamp = Math.floor(Date.now()/1000);
  const row = await e.DB.prepare("INSERT INTO request_limits(key,window_start,count) VALUES(?,?,1) ON CONFLICT(key) DO UPDATE SET window_start=CASE WHEN window_start<=?-? THEN ? ELSE window_start END,count=CASE WHEN window_start<=?-? THEN 1 ELSE count+1 END RETURNING count")
    .bind(key,timestamp,timestamp,seconds,timestamp,timestamp,seconds).first<J>();
  return Number(row?.count || 0) <= max;
}
const LOGIN_FAIL_LIMIT = 10;
const LOGIN_LOCKOUT_SECONDS = 900;
/** Failed-login run for one account. Read-only: the lockout check must not itself increment. */
async function loginFailures(e: Env, accountKey: string) {
  const row = await e.DB.prepare("SELECT count,window_start FROM request_limits WHERE key=?")
    .bind("login-fail:" + accountKey).first<{count:number;window_start:number}>();
  if (!row) return 0;
  const seconds = Math.floor(Date.now()/1000);
  return seconds - Number(row.window_start || 0) >= LOGIN_LOCKOUT_SECONDS ? 0 : Number(row.count || 0);
}
/** Counter window that charges `amount` units instead of one, for character budgets. */
async function takeAmount(e: Env, key: string, seconds: number, max: number, amount: number) {
  const timestamp = Math.floor(Date.now()/1000);
  const row = await e.DB.prepare("INSERT INTO request_limits(key,window_start,count) VALUES(?,?,?) ON CONFLICT(key) DO UPDATE SET window_start=CASE WHEN window_start<=?-? THEN ? ELSE window_start END,count=CASE WHEN window_start<=?-? THEN ? ELSE count+? END RETURNING count")
    .bind(key,timestamp,amount,timestamp,seconds,timestamp,timestamp,seconds,amount,amount).first<J>();
  return Number(row?.count || 0) <= max;
}

function quota(e: Env) {
  return Number(e.DEFAULT_PLAN === "free" ? 100000 : 1000000);
}
function normalizeEmail(v: any) {
  return String(v || "")
    .trim()
    .toLowerCase();
}
function normalizeAccount(v: any) {
  return String(v || "")
    .trim()
    .toLowerCase();
}
async function registrationConflict(email: string, e: Env, account?: string) {
  // Include inactive accounts: disabling an account must not free its identity.
  if (await e.DB.prepare("SELECT 1 FROM users WHERE lower(email)=?").bind(email).first())
    return json({ success: false, error_code: "email_exists",
      message: "该邮箱已注册，请直接登录或使用忘记密码" }, 409, cors(e));
  if (account && await e.DB.prepare("SELECT 1 FROM users WHERE account=?").bind(account).first())
    return json({ success: false, error_code: "account_exists",
      message: "该账号已存在，请更换账号或直接登录" }, 409, cors(e));
  return null;
}
async function sendCode(email: string, purpose: string, e: Env) {
  if (purpose === "register") {
    const conflict = await registrationConflict(email, e);
    if (conflict) return conflict;
  }
  try { mailProviders(e); } catch {
    return json(
      {
        success: false,
        error_code: "mail_not_configured",
        message: "邮箱服务尚未配置",
      },
      503,
      cors(e),
    );
  }
  const recent = await e.DB.prepare(
    "SELECT COUNT(*) n FROM email_verification_codes WHERE email=? AND purpose=? AND julianday(created_at)>julianday('now','-10 minutes')",
  )
    .bind(email, purpose)
    .first<J>();
  if (Number(recent?.n || 0) >= 3)
    return json(
      {
        success: false,
        error_code: "rate_limited",
        message: "验证码发送过于频繁，请稍后再试",
      },
      429,
      cors(e),
    );
  if (!(await takeLimit(e,"mail:" + await digest(email + "|" + purpose),600,3)))
    return json({success:false,error_code:"rate_limited",message:"验证码发送过于频繁，请稍后再试"},429,cors(e));
  let providers: Awaited<ReturnType<typeof selectMailProviders>>;
  try { providers = await selectMailProviders(e); } catch {
    // Do not invalidate an existing code or send anything when routing is unavailable.
    console.error(JSON.stringify({ event: "mail_routing_unavailable" }));
    return json({ success: false, error_code: "mail_routing_unavailable",
      message: "邮件调度暂不可用，请联系管理员检查数据库迁移" }, 503, cors(e));
  }
  const code = String(100000 + crypto.getRandomValues(new Uint32Array(1))[0] % 900000),
    id = random(),
    ts = now(),
    expires = new Date(Date.now() + 600000).toISOString();
  await e.DB.batch([e.DB.prepare(
    "UPDATE email_verification_codes SET used_at=? WHERE email=? AND purpose=? AND used_at IS NULL",
  ).bind(ts, email, purpose),
  e.DB.prepare(
    "INSERT INTO email_verification_codes(id,email,purpose,code_hash,expires_at,attempts,created_at) VALUES(?,?,?,?,?,0,?)",
  )
    .bind(
      id,
      email,
      purpose,
      // H-W1: the hash is peppered like every other credential digest; a leaked code table
      // without PASSWORD_PEPPER cannot be verified offline. Rotating the pepper invalidates
      // only the outstanding 10-minute codes.
      await digest(code + "|" + email + "|" + purpose + "|" + e.PASSWORD_PEPPER),
      expires,
      ts,
    )]);
  const result = await deliverMail(e, {
    id, to: email,
    subject: purpose === "register" ? "DWGC2E 注册验证码" : "DWGC2E 密码重置验证码",
    html: '<p>你的验证码是：</p><p style="font-size:28px;font-weight:700;letter-spacing:6px">' +
      code + '</p><p>验证码 10 分钟内有效。如非本人操作，请忽略此邮件。</p>',
  }, fetch, 8000, providers);
  if (!result.ok) {
    // Preserve uncertain deliveries in case the message arrives late.
    if (!result.uncertain) await e.DB.prepare(
      "UPDATE email_verification_codes SET used_at=? WHERE id=? AND used_at IS NULL",
    ).bind(now(), id).run();
    return json({ success: false,
      error_code: result.uncertain ? "mail_delivery_uncertain" : "mail_send_failed",
      message: result.uncertain ? "邮件发送状态暂未确认，请先检查收件箱，稍后再试" : "验证码邮件发送失败，请稍后再试",
    }, 502, cors(e));
  }
  return json(
    { success: true, message: "验证码已发送", expires_in: 600 },
    200,
    cors(e),
  );
}
async function requestRegisterCode(r: Request, e: Env) {
  const b = await text(r),
    email = normalizeEmail(b?.email);
  if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email))
    return json(
      {
        success: false,
        error_code: "invalid_email",
        message: "邮箱格式不正确",
      },
      400,
      cors(e),
    );
  const address = await clientAddress(r, e);
  // Whether a mailbox is already registered must not be free to probe: the conflict check below
  // now runs only after a throttled, client-bound CAPTCHA has been solved and consumed.
  if (!(await takeLimit(e, "register-code:" + await digest(email), 600, 10)) ||
      !(await takeLimit(e, "register-code-ip:" + await digest(address), 600, 30)))
    return json({success:false,error_code:"rate_limited",message:"请求过于频繁，请稍后再试"},429,cors(e));
  if (!await consumeCaptcha(e,email,'register',b?.captcha_id,b?.captcha_code,address)) return json({error_code:'invalid_captcha',message:'数字验证码错误或过期，请刷新图片后重试'},400,cors(e));
  return sendCode(email, "register", e);
}
async function requestPasswordCode(r: Request, e: Env) {
  const b = await text(r),
    email = normalizeEmail(b?.email);
  if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email))
    return json(
      {
        success: false,
        error_code: "invalid_email",
        message: "邮箱格式不正确",
      },
      400,
      cors(e),
    );
  const address = await clientAddress(r, e);
  if (!(await takeLimit(e, "reset-code:" + await digest(email), 600, 10)) ||
      !(await takeLimit(e, "reset-code-ip:" + await digest(address), 600, 30)))
    return json({success:false,error_code:"rate_limited",message:"请求过于频繁，请稍后再试"},429,cors(e));
  if (!await consumeCaptcha(e,email,'password_reset',b?.captcha_id,b?.captcha_code,address)) return json({error_code:'invalid_captcha',message:'数字验证码错误或过期，请刷新图片后重试'},400,cors(e));
  return sendCode(email, "password_reset", e);
}
async function verifyCode(
  email: string,
  purpose: string,
  code: string,
  e: Env,
) {
  const row = await e.DB.prepare(
    "SELECT * FROM email_verification_codes WHERE email=? AND purpose=? AND used_at IS NULL ORDER BY created_at DESC LIMIT 1",
  )
    .bind(email, purpose)
    .first<J>();
  if (
    !row ||
    Number(row.attempts || 0) >= 5 ||
    new Date(row.expires_at) <= new Date()
  )
    return false;
  const ok = timingSafeEqualHex(
    await digest(code + "|" + email + "|" + purpose + "|" + e.PASSWORD_PEPPER),
    row.code_hash,
  );
  if (!ok) {
    await e.DB.prepare(
      "UPDATE email_verification_codes SET attempts=attempts+1 WHERE id=? AND used_at IS NULL AND attempts<5",
    )
      .bind(row.id)
      .run();
    return false;
  }
  return { id: row.id as string, claim: now() + "|" + random() };
}
async function resetPassword(r: Request, e: Env) {
  const b = await text(r),
    email = normalizeEmail(b?.email),
    code = String(b?.code || b?.verification_code || ""),
    password = String(b?.new_password || b?.password || "");
  if (password.length < 8 || password.length > 128)
    return json(
      {
        success: false,
        error_code: "invalid_request",
        message: "新密码需为 8–128 位",
      },
      400,
      cors(e),
    );
  // Unauthenticated callers must not be able to burn a mailbox' verification code for free: the
  // five attempts that exhaust the code now also exhaust this window.
  if (!(await takeLimit(e,"reset:" + await digest(email),600,5)) ||
      !(await takeLimit(e,"reset-ip:" + await digest(await clientAddress(r,e)),600,30)))
    return json({success:false,error_code:"rate_limited",message:"操作过于频繁，请稍后再试"},429,cors(e));
  const verified = await verifyCode(email, "password_reset", code, e);
  if (!verified)
    return json(
      {
        success: false,
        error_code: "invalid_code",
        message: "验证码无效或已过期",
      },
      400,
      cors(e),
    );
  const u = await e.DB.prepare("SELECT id FROM users WHERE lower(email)=?")
    .bind(email)
    .first<J>();
  if (!u) return json({success:false,error_code:"invalid_code",message:"验证码或账号无效"},400,cors(e));
  const results = await e.DB.batch([
    e.DB.prepare("UPDATE email_verification_codes SET used_at=? WHERE id=? AND used_at IS NULL AND attempts<5 AND julianday(expires_at)>julianday(?)").bind(verified.claim,verified.id,now()),
    e.DB.prepare("UPDATE users SET password_hash=? WHERE id=? AND EXISTS(SELECT 1 FROM email_verification_codes WHERE id=? AND used_at=?)").bind(await pass(password,e.PASSWORD_PEPPER),u.id,verified.id,verified.claim),
    e.DB.prepare("UPDATE sessions SET revoked_at=? WHERE user_id=? AND revoked_at IS NULL AND EXISTS(SELECT 1 FROM email_verification_codes WHERE id=? AND used_at=?)").bind(now(),u.id,verified.id,verified.claim)
  ]);
  if (!results[0].meta.changes) return json({success:false,error_code:"invalid_code",message:"验证码已使用或过期"},400,cors(e));
  return json({ success: true, message: "密码已重置" }, 200, cors(e));
}
async function register(r: Request, e: Env) {
  const b = await text(r);
  const account = normalizeAccount(b?.account),
    password = String(b?.password || ""),
    email = normalizeEmail(b?.email),
    code = String(b?.verification_code || b?.code || "");
  const requestedDisplayName = b?.display_name;
  // Registration is the only remaining writer of display_name, so it enforces the same 1-80
  // limit as PATCH /v1/profile instead of trusting the client.
  const displayName = requestedDisplayName === undefined || requestedDisplayName === null || requestedDisplayName === ""
    ? account
    : typeof requestedDisplayName === "string" ? requestedDisplayName.trim() : "";
  if (account.length < 3 || password.length < 8 || password.length > 128 || !email || !code || !displayName || displayName.length > 80)
    return json(
      {
        success: false,
        error_code: "invalid_request",
        message: "账号至少 3 位，密码需为 8–128 位，显示名称需为 1–80 个字符",
      },
      400,
      cors(e),
    );
  if (!(await takeLimit(e,"register:" + await digest(email),600,5)) ||
      !(await takeLimit(e,"register-ip:" + await digest(await clientAddress(r,e)),600,30)))
    return json({success:false,error_code:"rate_limited",message:"操作过于频繁，请稍后再试"},429,cors(e));
  const conflict = await registrationConflict(email, e, account);
  if (conflict) return conflict;
  const verified = await verifyCode(email, "register", code, e);
  if (!verified)
    return json(
      {
        success: false,
        error_code: "invalid_code",
        message: "邮箱验证码无效或已过期",
      },
      400,
      cors(e),
    );
  const id = random(),
    ts = now();
  try {
    await e.DB.batch([
      e.DB.prepare("UPDATE email_verification_codes SET used_at=? WHERE id=? AND used_at IS NULL AND attempts<5 AND julianday(expires_at)>julianday(?)").bind(verified.claim,verified.id,ts),
      e.DB.prepare(
        "INSERT INTO users(id,account,password_hash,display_name,email,created_at) SELECT ?,?,?,?,?,? WHERE EXISTS(SELECT 1 FROM email_verification_codes WHERE id=? AND used_at=?)",
      ).bind(
        id,
        account,
        await pass(password, e.PASSWORD_PEPPER),
        displayName,
        email,
        ts, verified.id, verified.claim,
      ),
      e.DB.prepare(
        "INSERT INTO subscriptions(user_id,plan_name,starts_at,updated_at) SELECT ?,?,?,? WHERE EXISTS(SELECT 1 FROM users WHERE id=?)",
      ).bind(id, e.DEFAULT_PLAN || "free", ts, ts,id),
    ]);
  } catch (error) {
    // Another request may register the identity after our preflight check.
    // The unique constraints and transactional batch prevent duplicate/partial users.
    const concurrentConflict = await registrationConflict(email, e, account);
    if (concurrentConflict) return concurrentConflict;
    throw error; // Do not misreport unrelated database failures as duplicate accounts.
  }
  if (!(await e.DB.prepare("SELECT 1 FROM users WHERE id=?").bind(id).first())) {
    const conflict = await registrationConflict(email,e,account);
    return conflict || json({success:false,error_code:"invalid_code",message:"验证码已使用或过期"},400,cors(e));
  }
  return json({ success: true, message: "注册成功，请登录" }, 201, cors(e));
}
async function login(r: Request, e: Env, kind: 'web' | 'app' = 'app') {
  const b = await text(r);
  if (!b?.account || !b?.password)
    return json(
      {
        success: false,
        error_code: "invalid_request",
        message: "账号和密码不能为空",
      },
      400,
      cors(e),
    );
  const account = normalizeAccount(b.account);
  const accountKey = await digest(account);
  // Rate limiting alone only slows guessing down; a run of failures must also close the account
  // for a while, otherwise a slow attacker gets unlimited attempts across windows.
  if (await loginFailures(e, accountKey) >= LOGIN_FAIL_LIMIT)
    return json({ success:false,error_code:"account_locked",message:`登录失败次数过多，请 ${Math.round(LOGIN_LOCKOUT_SECONDS/60)} 分钟后重试` },429,cors(e));
  if (!(await takeLimit(e, "login-account:" + accountKey, 600, 20)) ||
      !(await takeLimit(e, "login-ip:" + await digest(await clientAddress(r,e)), 600, 100)))
    return json({ success:false,error_code:"rate_limited",message:"登录尝试过于频繁" },429,cors(e));
  const u = await e.DB.prepare("SELECT * FROM users WHERE account=?")
    .bind(account)
    .first<J>();
  if (
    !u || !u.is_active ||
    !(await passwordMatches(String(b.password), e.PASSWORD_PEPPER, u.password_hash))
  ) {
    await takeLimit(e, "login-fail:" + accountKey, LOGIN_LOCKOUT_SECONDS, LOGIN_FAIL_LIMIT);
    return json(
      {
        success: false,
        error_code: "invalid_credentials",
        message: "账号或密码错误",
      },
      401,
      cors(e),
    );
  }
  // A correct password clears the failure run, so the lockout never punishes a successful login.
  await e.DB.prepare("DELETE FROM request_limits WHERE key=?").bind("login-fail:" + accountKey).run();
  if (!u.password_hash.startsWith("v2:")) {
    const upgraded = await pass(String(b.password), e.PASSWORD_PEPPER);
    const result = await e.DB.prepare("UPDATE users SET password_hash=? WHERE id=? AND password_hash=?").bind(upgraded,u.id,u.password_hash).run();
    if (!result.meta.changes) return json({error_code:'invalid_credentials',message:'凭证已变更，请重新登录'},401,cors(e));
    u.password_hash = upgraded;
    // Identity-free counter: the only way to know when the fixed-salt fallback is dead.
    console.log(JSON.stringify({event:'password_hash_migrated_v2'}));
  }
  const device = String(b.device_id || '').trim();
  if (kind === 'app' && (!device || device.length > 128 || device.startsWith('web-')))
    return json({success:false,error_code:'invalid_device',message:'请通过网页登录入口登录；APP 设备标识必须有效'},400,cors(e));
  try {
    return json(await issueSession(e, String(u.id), kind, String(u.password_hash), device, String(b.device_name || '')),200,cors(e));
  } catch (error) {
    if (String(error).includes('session_credentials_changed')) return json({error_code:'invalid_credentials',message:'凭证已变更，请重新登录'},401,cors(e));
    if (String(error).includes('device_limit')) return json({success:false,error_code:'device_limit',message:'最多绑定 3 台 APP 安装实例，请在网页设备管理中解除旧绑定'},403,cors(e));
    throw error;
  }
}

async function effectiveQuota(e: Env, userId: string) { return (await entitlementSnapshot(e,userId)).usage.base_quota; }
async function translate(r: Request, e: Env, user: J) {
  const b = await text(r);
  const items = Array.isArray(b?.items) ? b.items : [];
  // L7: a non-numeric binding made Number() yield NaN and silently disabled the limit.
  const maxItems = Number.isFinite(Number(e.MAX_TRANSLATE_ITEMS)) ? Number(e.MAX_TRANSLATE_ITEMS) : 100;
  const maxTextLength = Number.isFinite(Number(e.MAX_TEXT_LENGTH)) ? Number(e.MAX_TEXT_LENGTH) : 2000;
  if (!b || !items.length || items.length > maxItems)
    return json({success:false,error_code:"invalid_request",message:"翻译条目数量无效"},400,cors(e));
  const clean = items.map(x => ({id: x?.id, text: x?.text, context: typeof x?.context === "string" ? x.context.slice(0,500) : ""}));
  if (clean.some(x => !Number.isSafeInteger(x.id) || typeof x.text !== "string" || !x.text.trim() || x.text.length > maxTextLength) || new Set(clean.map(x=>x.id)).size !== clean.length)
    return json({success:false,error_code:"invalid_text",message:"文本为空、过长或标识无效"},400,cors(e));
  const languages = /^[a-zA-Z]{2,8}(?:-[a-zA-Z]{2,8})?$/;
  if (!languages.test(b.source_lang) || !languages.test(b.target_lang)) return json({success:false,error_code:"invalid_language",message:"语言代码无效"},400,cors(e));
  const glossary = Array.isArray(b.glossary) ? b.glossary.slice(0,1000).filter((x:any)=>typeof x?.source==="string" && typeof x?.target==="string").map((x:any)=>({source:x.source.slice(0,500),target:x.target.slice(0,500),...(Number.isSafeInteger(x.priority)?{priority:Math.max(0,Math.min(100,x.priority))}:{})})) : [];
  const glossaryCharacters = glossary.reduce((sum:any,x:any)=>sum + x.source.length + x.target.length, 0);
  if (clean.reduce((sum:any,x:any)=>sum + x.text.length + x.context.length, 0) + glossaryCharacters > 250_000)
    return json({success:false,error_code:"payload_too_large",message:"本次翻译数据过大，请拆分后重试"},413,cors(e));
  const protection = {protect_dimensions:b.protection?.protect_dimensions !== false,protect_tolerances:b.protection?.protect_tolerances !== false,protect_models:b.protection?.protect_models !== false,glossary_first:b.protection?.glossary_first !== false};
  const mode = b.billing_mode ?? 'online';
  const taskId = b.billing_task_id ?? null;
  if (!['online','offline'].includes(mode) || (taskId !== null && (typeof taskId !== 'string' || !/^[a-zA-Z0-9_-]{8,128}$/.test(taskId))) || (mode === 'offline' && !taskId))
    return json({error_code:'invalid_billing_context',message:'计费模式或任务标识无效'},400,cors(e));
  const payload = {source_lang:b.source_lang,target_lang:b.target_lang,items:clean,glossary,protection,...(taskId ? {billing_mode:mode,billing_task_id:taskId} : {})};
  let protectedItems: ReturnType<typeof protectGlossary>[];
  try { protectedItems = clean.map(item => protectGlossary(item.text, glossary)); }
  catch { return json({success:false,error_code:'glossary_conflict',message:'同一原文存在不同术语译法，请先解决冲突'},400,cors(e)); }
  const hash = await digest(JSON.stringify(payload));
  // L6: the idempotency key is part of the billing contract; a server-side random fallback
  // silently disabled replay protection for clients that omit the header.
  const requestId = r.headers.get("Idempotency-Key");
  if (!requestId) return json({success:false,error_code:"invalid_request_id",message:"缺少请求标识，请携带 Idempotency-Key 请求头"},400,cors(e));
  if (!/^[a-zA-Z0-9_-]{8,128}$/.test(requestId)) return json({success:false,error_code:"invalid_request_id",message:"请求标识无效"},400,cors(e));
  const timestamp = Math.floor(Date.now()/1000), ym=now().slice(0,7);
  const expired = JSON.stringify({success:false,error_code:"request_expired",message:"请求已超时，预留额度已退还"});
  await e.DB.prepare("UPDATE translation_requests SET state='settled',billed=0,response_json=?,response_status=504,completed_at=strftime('%Y-%m-%dT%H:%M:%fZ',expires_at,'unixepoch') WHERE user_id=? AND state='reserved' AND expires_at<=?").bind(expired,user.user_id,timestamp).run();
  const previous = await e.DB.prepare("SELECT * FROM translation_requests WHERE user_id=? AND request_id=?").bind(user.user_id,requestId).first<J>();
  const replay = (row:J) => row.payload_hash !== hash ? json({success:false,error_code:"idempotency_conflict",message:"请求标识已用于不同内容"},409,cors(e)) : row.state === "settled" ? json(JSON.parse(row.response_json),row.response_status,cors(e)) : json({success:false,error_code:"request_in_progress",message:"请求仍在处理中，请使用相同请求标识查询"},409,cors(e));
  if (previous) return replay(previous);
  let percent = 100;
  if (taskId) {
    await e.DB.prepare("INSERT OR IGNORE INTO translation_billing_tasks(user_id,task_id,mode,percent) VALUES(?,?,?,?)").bind(user.user_id,taskId,mode,mode==='offline'?30:100).run();
    const billing = await e.DB.prepare("SELECT mode,percent FROM translation_billing_tasks WHERE user_id=? AND task_id=?").bind(user.user_id,taskId).first<J>();
    if (!billing || billing.mode !== mode) return json({error_code:'billing_mode_conflict',message:'同一任务不能更改计费模式，请保留原模式重试'},409,cors(e));
    percent = billing.percent;
  }
  // L9: translation was the only metered write without a rate limit, so one stolen session could
  // burn the whole monthly quota — and real upstream spend — within minutes. Two buckets: a
  // request count and a per-minute character budget sized for one full batch plus headroom.
  const rateRequests = Number.isFinite(Number(e.TRANSLATE_RATE_REQUESTS)) && Number(e.TRANSLATE_RATE_REQUESTS) > 0 ? Number(e.TRANSLATE_RATE_REQUESTS) : 30;
  const rateChars = Number.isFinite(Number(e.TRANSLATE_RATE_CHARS)) && Number(e.TRANSLATE_RATE_CHARS) > 0 ? Number(e.TRANSLATE_RATE_CHARS) : 120000;
  const rateLimited = (message: string) => new Response(JSON.stringify({success:false,error_code:"rate_limited",message}),{status:429,headers:{"content-type":"application/json; charset=utf-8","access-control-allow-origin":cors(e),"retry-after":"60","cache-control":"no-store"}});
  if (!await takeLimit(e, `translate:${user.user_id}`, 60, rateRequests)) return rateLimited("翻译请求过于频繁，请稍后重试");
  const budgetChars = clean.reduce((sum:any,x:any)=>sum + x.text.length + x.context.length,0);
  if (!await takeAmount(e, `translate-chars:${user.user_id}:${Math.floor(timestamp/60)}`, 120, rateChars, Math.max(1,budgetChars))) return rateLimited("本分钟翻译字数已达上限，请稍后重试");
  const quotaValue = await effectiveQuota(e,user.user_id);
  await e.DB.prepare("INSERT INTO usage_monthly(user_id,year_month,chars_used,chars_quota,task_count) VALUES(?,?,0,?,0) ON CONFLICT(user_id,year_month) DO UPDATE SET chars_quota=excluded.chars_quota").bind(user.user_id,ym,quotaValue).run();
  const chars=clean.reduce((n,x)=>n+x.text.length,0);
  try {
    // M-W2: the reservation must outlive the worst upstream path. ai-router.ts:288 allows a
    // 120s per-provider timeout and routeCompletion may retry across providers, so 300s is the
    // budget; anything shorter risked settling the reservation (504 refund) while the upstream
    // call was still in flight.
    const inserted=await e.DB.prepare("INSERT OR IGNORE INTO translation_requests(user_id,request_id,payload_hash,year_month,reserved,expires_at,source_language,target_language,started_at,billing_task_id) VALUES(?,?,?,?,?,?,?,?,?,?)").bind(user.user_id,requestId,hash,ym,Math.ceil(chars*percent/100),timestamp+300,b.source_lang,b.target_lang,now(),taskId).run();
    if (!inserted.meta.changes) return replay((await e.DB.prepare("SELECT * FROM translation_requests WHERE user_id=? AND request_id=?").bind(user.user_id,requestId).first<J>())!);
  } catch(error) {
    if (String(error).includes("quota_exceeded")) return json({success:false,error_code:"quota_exceeded",message:"本月翻译额度不足"},402,cors(e));
    throw error;
  }
  let body:J={success:false,error_code:"upstream_unavailable",message:"翻译服务暂时不可用"}, status=503, billed=0;
  try {
    const completion=await routeCompletion(e,requestId,{...payload,glossary:[],items:clean.map((item,i)=>({...item,text:protectedItems[i].text}))});
    const parsed:unknown=JSON.parse(completion.content.trim().replace(/^```(?:json)?\s*|```$/g, ""));
    if (!Array.isArray(parsed) || parsed.length > clean.length || parsed.some(x=>!x || typeof x!=="object" || !Number.isSafeInteger(x.id) || typeof x.translated_text!=="string" || x.translated_text.length > MAX_PROVIDER_RESULT_CHARS)) throw new Error("invalid_result");
    const inputIds = new Set(clean.map(x=>x.id));
    const resultIds = new Set<number>();
    const duplicateResultIds = new Set<number>();
    let resultCharacters = 0;
    for (const item of parsed as Array<{id:number;translated_text:string}>) {
      if (!inputIds.has(item.id)) throw new Error("invalid_result");
      if (resultIds.has(item.id)) duplicateResultIds.add(item.id);
      resultIds.add(item.id);
      resultCharacters += item.translated_text.length;
      if (resultCharacters > MAX_PROVIDER_RESULT_CHARS) throw new Error("invalid_result");
    }
    const results=clean.map((x,index)=>{
      const matches=parsed.filter(y=>y.id===x.id);
      if(duplicateResultIds.has(x.id) || matches.length!==1 || !matches[0].translated_text.trim()) return {id:x.id,error_code:duplicateResultIds.has(x.id)?"duplicate_result":"missing_result"};
      const translated=protectedItems[index].restore(matches[0].translated_text.trim());
      if(translated===null) return {id:x.id,error_code:"glossary_not_preserved"};
      // Numeric values must not disappear, including signs, decimal places and tolerances.
      const tokens=(value:string)=>value.match(/[+-]?\d+(?:[.,]\d+)?/g)||[];
      if ((protection.protect_dimensions || protection.protect_tolerances) && JSON.stringify(tokens(x.text))!==JSON.stringify(tokens(translated))) return {id:x.id,error_code:"protected_value_changed"};
      const models=(value:string)=>value.match(/\b(?=[A-Za-z0-9_-]*[A-Za-z])(?=[A-Za-z0-9_-]*\d)[A-Za-z0-9_-]+\b/g)||[];
      if(protection.protect_models && models(x.text).some(m=>!translated.includes(m))) return {id:x.id,error_code:"protected_model_changed"};
      return {id:x.id,translated_text:translated,from_cache:false};
    });
    billed=results.reduce((sum,x,i)=>sum+(x.translated_text?clean[i].text.length:0),0);
    body={success:true,characters_used:billed,cached_count:0,context_version:completion.contextVersion,items:results};status=200;
  } catch(error) {
    body={success:false,error_code:"upstream_invalid_or_unavailable",message:"翻译服务超时或返回无效内容，未扣除额度"};status=502;billed=0;
  }
  if (taskId) {
    // One atomic statement reads the cumulative count, settles quota and advances the
    // task via trigger. Concurrent chunks cannot read the same rounding remainder.
    const delta = "(SELECT CAST(((original_chars+?)*percent+99)/100 AS INTEGER)-CAST((original_chars*percent+99)/100 AS INTEGER) FROM translation_billing_tasks WHERE user_id=? AND task_id=?)";
    await e.DB.prepare(`UPDATE translation_requests SET state='settled',original_chars=?,billed=${delta},response_json=json_set(?,'$.characters_used',${delta},'$.original_characters',?,'$.billing_percent',?,'$.billing_mode',?,'$.billing_rule','task-cumulative-v1'),response_status=?,completed_at=? WHERE user_id=? AND request_id=? AND state='reserved'`)
      .bind(billed,billed,user.user_id,taskId,JSON.stringify(body),billed,user.user_id,taskId,billed,percent,mode,status,now(),user.user_id,requestId).run();
  } else {
  await e.DB.prepare("UPDATE translation_requests SET state='settled',billed=?,response_json=?,response_status=?,completed_at=? WHERE user_id=? AND request_id=? AND state='reserved'").bind(billed,JSON.stringify(body),status,now(),user.user_id,requestId).run();
  }
  const saved=(await e.DB.prepare("SELECT * FROM translation_requests WHERE user_id=? AND request_id=?").bind(user.user_id,requestId).first<J>())!;
  return replay(saved);
}
// M-W1: request_limits / sessions / email_verification_codes have no other cleanup path.
// Exported for deterministic testing; scheduled() samples it (~every 10 minutes) because the
// cron fires every minute and a full sweep each tick is needless write load.
export async function runRetentionCleanup(e: Env) {
  const cutoff = new Date(Date.now() - 86400000).toISOString();
  const windowCutoff = Math.floor(Date.now()/1000) - 600;
  await e.DB.batch([
    e.DB.prepare("DELETE FROM request_limits WHERE window_start < ?").bind(windowCutoff),
    e.DB.prepare("DELETE FROM email_verification_codes WHERE expires_at < ? OR (used_at IS NOT NULL AND used_at < ?)").bind(cutoff,cutoff),
    e.DB.prepare("DELETE FROM sessions WHERE expires_at < ? OR (revoked_at IS NOT NULL AND revoked_at < ?)").bind(cutoff,cutoff)
  ]);
}
export default {
  async scheduled(event:ScheduledController,e:Env,ctx:ExecutionContext){
    const runId=crypto.randomUUID(),started=Date.now();
    console.log(JSON.stringify({event:'payment_recovery_started',runId,scheduledTime:event.scheduledTime}));
    ctx.waitUntil(runPaymentRecovery(e).then(()=>{
      console.log(JSON.stringify({event:'payment_recovery_completed',runId,durationMs:Date.now()-started}));
    }).catch(()=>{
      // Never log database/provider errors: they may contain private order data.
      console.error(JSON.stringify({event:'payment_recovery_failed',runId,durationMs:Date.now()-started}));
      throw new Error('payment_recovery_failed');
    }));
    // A cleanup failure must never disturb payment recovery, hence the swallow-and-log.
    // Deterministic every fifth minute: a random sample could skip many consecutive runs and let
    // the rate-limit table grow without bound.
    if (Math.floor(Date.now() / 60000) % 5 === 0) {
      try { await runRetentionCleanup(e); }
      catch { console.error(JSON.stringify({event:'retention_cleanup_failed',runId})); }
    }
  },
  async fetch(r: Request, e: Env) {
    try { return await route(await guardRequestBody(r),e); } catch(error) {
      if (error instanceof RequestBodyError) return json({success:false,error_code:error.code,message:error.message},error.status,cors(e));
      const message=String(error);
      const code=message.includes("device_conflict")?"device_conflict":message.includes("device_limit")?"device_limit":"internal_error";
      console.error(JSON.stringify({event:"request_failed",code}));
      return json({success:false,error_code:code,message:code==="internal_error"?"服务暂时不可用，请稍后重试":"设备归属冲突或达到设备上限"},code==="internal_error"?500:409,cors(e));
    }
  }
};
async function route(r: Request,e: Env) {
    const origin = cors(e);
    if (r.method === "OPTIONS") return json({}, 204, origin);
    const u = new URL(r.url),
      p = u.pathname;
    if(p==='/v1/admin/session')return adminSessionRoute(r,e);
    if(p.startsWith('/v1/admin/')&&!validAdminKey(r,e)){
      if(!await adminSessionAuthorized(r,e))return json({message:'管理员登录已失效，请重新登录'},401,origin);
      const headers=new Headers(r.headers);headers.set('authorization','Bearer '+e.ADMIN_API_KEY);headers.set('x-admin-actor',await adminActor(r,e));r=new Request(r,{headers});
    } else if (p.startsWith('/v1/admin/')) {
      const headers=new Headers(r.headers);headers.set('x-admin-actor',await adminActor(r,e));r=new Request(r,{headers});
    }
    if(p==='/v1/site'||p.startsWith('/v1/admin/operations/'))return operationsRoute(r,e);
    if(['/v1/auth/register/request-code','/v1/auth/register'].includes(p)&&r.method==='POST'&&!(await settings(e,'controls')).registration_open)return json({message:'新用户注册暂时关闭',error_code:'registration_paused'},403,origin);
    if(p==='/v1/translate'&&r.method==='POST'&&(await settings(e,'controls')).maintenance)return json({message:'翻译服务正在维护，请稍后重试',error_code:'maintenance'},503,origin);
    if (p.startsWith('/v1/admin/ai/')) return adminAiRoute(r,e);
    if (p === '/v1/admin/plans' || p.startsWith('/v1/admin/plans/')) return adminPlansRoute(r,e);
    if (p === '/v1/admin/users' || p.startsWith('/v1/admin/users/')) return adminUsersRoute(r,e);
    // Directed notifications are an independent route module: operation_settings `content` is a
    // strict equality whitelist and must not absorb per-user messages. The /v1/admin/* gate above
    // has already authenticated the caller and injected x-admin-actor.
    if (p === '/v1/admin/notifications' || p.startsWith('/v1/admin/notifications/')) return adminNotificationsRoute(r,e,text);
    if (p === "/v1/feedback" || p === "/v1/health" || p.startsWith("/v1/admin/feedback")) return feedbackRoute(r,e);
    if (p === "/" && r.method === "GET")
      return json(
        { service: "DWGC2E API", status: "ok", version: e.LATEST_VERSION || "0.1.0" },
        200,
        origin,
      );
    if (p === '/v1/auth/captcha' && r.method === 'POST') {
      const b=await text(r),email=normalizeEmail(b?.email),purpose=b?.purpose;
      if(!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)||!['register','password_reset'].includes(purpose))return json({message:'请先填写有效邮箱'},400,origin);
      const address=await clientAddress(r,e);
      if(!await takeLimit(e,'captcha:'+await digest(address),60,15))return json({message:'刷新过于频繁，请稍后再试'},429,origin);
      // The per-address window above still lets one mailbox be refreshed indefinitely from many
      // addresses; a challenge is single-use, so issuing is the actual brute-force budget.
      if(!await takeLimit(e,'captcha-mail:'+await digest(email+'|'+purpose),600,20))return json({message:'该邮箱获取验证码过于频繁，请稍后再试'},429,origin);
      return json(await issueCaptcha(e,email,purpose,address),200,origin);
    }
    if (p === "/v1/auth/register/request-code" && r.method === "POST")
      return requestRegisterCode(r, e);
    if (p === "/v1/auth/password/request-code" && r.method === "POST")
      return requestPasswordCode(r, e);
    if (p === "/v1/auth/password/reset" && r.method === "POST")
      return resetPassword(r, e);
    if (p === "/v1/auth/register" && r.method === "POST") return register(r, e);
    if (p === "/v1/auth/login" && r.method === "POST") return login(r, e);
    if (p === "/v1/auth/web/login" && r.method === "POST") return login(r, e, 'web');
    if(p==='/v1/version'&&r.method==='GET'){const release=await settings(e,'release');const latestVersion=release.latest_version||e.LATEST_VERSION||'0.0.0';return json({service:'DWGC2E API',latest_version:latestVersion,app_version:latestVersion,worker_build_id:e.WORKER_BUILD_ID||'local',schema_version:e.WORKER_SCHEMA_VERSION||'unknown',deployed_at:e.WORKER_DEPLOYED_AT||null,ai_routing_version:e.AI_ROUTING_VERSION||'router-v1',mandatory:false,release_notes:release.release_notes||'',download_url:release.download_url||'',backup_download_url:release.backup_download_url||''},200,origin);}
    if (p==='/v1/admin/billing/recovery'||/^\/v1\/admin\/billing\/orders\/[^/]+\/review$/.test(p)) return recoveryAdmin(r,e);
    if (/^\/v1\/admin\/billing\/orders\/[^/]+\/inspect$/.test(p)) return inspectPayment(r, e);
    if (p === "/v1/billing/notify/ezfpy") return notifyPayment(r, e);
    if (p === '/v1/billing/plans' && r.method === 'GET' && !r.headers.has('authorization')) return billingRoute(r,e,{user_id:'',email:''},origin);
    const user = await authenticate(r, e);
    if (p === "/v1/glossary") {
      if (!user) return json({ error_code: "unauthenticated", message: "请先登录" }, 401, origin);
      return glossaryRoute(r, e, String(user.user_id), origin, text);
    }
    if (!user)
      return json(
        { error_code: "unauthenticated", message: "请先登录" },
        401,
        origin,
      );
    if (p === '/v1/auth/logout' && r.method === 'POST') {
      await logout(e, user); return json({success:true},200,origin);
    }
    const accountData = await accountDataRoute(r,e,user,origin,text);
    if(accountData) return accountData;
    if (p === '/v1/notifications' || p === '/v1/notifications/read') return notificationsRoute(r,e,user,origin,text);
    if (p.startsWith("/v1/billing/")) return billingRoute(r, e, { user_id: String(user.user_id), email: String(user.email || "") }, origin);
    if (p === '/v1/profile' && r.method === 'PATCH') {
      const body = await text(r);
      if (!body || typeof body.display_name !== 'string' || !body.display_name.trim() || body.display_name.trim().length > 80)
        return json({error_code:'invalid_request',message:'显示名称需为 1–80 个字符'},400,origin);
      if (body.email !== undefined && normalizeEmail(body.email) !== normalizeEmail(user.email))
        return json({error_code:'email_change_requires_verification',message:'邮箱不能直接修改，请联系支持处理'},400,origin);
      await e.DB.prepare('UPDATE users SET display_name=? WHERE id=?').bind(body.display_name.trim(),user.user_id).run();
      return json({success:true,display_name:body.display_name.trim()},200,origin);
    }
    if (p === '/v1/auth/password' && r.method === 'PATCH') {
      const body = await text(r);
      if (!body || typeof body.current_password !== 'string' || typeof body.new_password !== 'string' || body.new_password.length < 8 || body.new_password.length > 128)
        return json({error_code:'invalid_request',message:'请输入当前密码及 8–128 位新密码'},400,origin);
      if (!await takeLimit(e,'password-change:'+user.user_id,600,5)) return json({error_code:'rate_limited',message:'尝试过于频繁，请稍后再试'},429,origin);
      const existing = await e.DB.prepare('SELECT password_hash FROM users WHERE id=?').bind(user.user_id).first<J>();
      if (!existing || !await passwordMatches(body.current_password,e.PASSWORD_PEPPER,existing.password_hash))
        return json({error_code:'invalid_current_password',message:'当前密码不正确'},400,origin);
      const next = await pass(body.new_password,e.PASSWORD_PEPPER);
      const result = await e.DB.batch([
        e.DB.prepare('UPDATE users SET password_hash=? WHERE id=? AND password_hash=?').bind(next,user.user_id,existing.password_hash),
        e.DB.prepare('UPDATE sessions SET revoked_at=? WHERE user_id=? AND revoked_at IS NULL AND EXISTS(SELECT 1 FROM users WHERE id=? AND password_hash=?)').bind(now(),user.user_id,user.user_id,next)
      ]);
      return result[0].meta.changes ? json({success:true,message:'密码已修改，请重新登录'},200,origin) : json({error_code:'password_changed',message:'密码已发生变更，请重新登录'},409,origin);
    }
    if (p === "/v1/profile" && r.method === "GET")
      return json(
        {
          user_id: user.user_id,
          display_name: user.display_name || user.account,
          account: user.account,
          email: user.email || user.account,
          is_active: true,
        },
        200,
        origin,
      );
    if (p === "/v1/subscription" && r.method === "GET") {
      return json((await entitlementSnapshot(e,user.user_id)).subscription,200,origin);
    }
    if (p === "/v1/usage" && r.method === "GET") return json((await entitlementSnapshot(e,user.user_id)).usage,200,origin);
    if (p === '/v1/devices' && r.method === 'GET') {
      const waitDays=(await settings(e,'controls')).device_wait_days;
      const rows = await e.DB.prepare('SELECT device_id,device_name,platform,first_seen,last_seen FROM app_device_bindings WHERE user_id=? AND revoked=0 ORDER BY last_seen DESC').bind(user.user_id).all<J>();
      const list = (rows.results || []).map(d=>{const time=Date.parse(d.first_seen)+waitDays*86400000;return {...d,can_revoke:Number.isFinite(time)&&Date.now()>time,unbind_available_at:Number.isFinite(time)?new Date(time).toISOString():null};});
      return json({devices:list,used_devices:list.length,max_devices:3,unbind_wait_days:waitDays},200,origin);
    }
    if (p === '/v1/devices/bind' && r.method === 'POST') {
      const body = await text(r), deviceId = String(body?.device_id || '').trim();
      if (user.client_kind !== 'app' || deviceId !== user.device_id)
        return json({success:false,error_code:'app_binding_required',message:'仅允许 APP 确认当前安装实例绑定'},403,origin);
      const result = await e.DB.prepare('UPDATE app_device_bindings SET device_name=?,last_seen=? WHERE user_id=? AND device_id=? AND revoked=0')
        .bind(String(body?.device_name || '').slice(0,128),now(),user.user_id,deviceId).run();
      if (!result.meta.changes) return json({error_code:'unauthenticated',message:'设备绑定已失效，请重新登录'},401,origin);
      const count = await e.DB.prepare('SELECT COUNT(*) n FROM app_device_bindings WHERE user_id=? AND revoked=0').bind(user.user_id).first<J>();
      return json({success:true,used_devices:Number(count?.n || 0),max_devices:3},200,origin);
    }
    if (p === '/v1/devices/revoke' && r.method === 'POST') {
      const body = await text(r), deviceId = String(body?.device_id || '').trim();
      if (!deviceId || deviceId.length > 128) return json({success:false,error_code:'invalid_device',message:'设备标识无效'},400,origin);
      const binding=await e.DB.prepare('SELECT first_seen FROM app_device_bindings WHERE device_id=? AND user_id=? AND revoked=0').bind(deviceId,user.user_id).first<J>();
      if(!binding)return json({error_code:'device_not_found',message:'设备不存在或已经移除'},404,origin);
      const waitDays=(await settings(e,'controls')).device_wait_days;
      const unlock=Date.parse(binding.first_seen)+waitDays*86400000;
      if(!Number.isFinite(unlock)||Date.now()<=unlock)return json({error_code:'device_binding_locked',message:`设备绑定超过 ${waitDays} 天后才可解绑`,unbind_available_at:Number.isFinite(unlock)?new Date(unlock).toISOString():null},409,origin);
      try { await e.DB.batch([
        e.DB.prepare('UPDATE app_device_bindings SET revoked=1 WHERE device_id=? AND user_id=? AND revoked=0').bind(deviceId,user.user_id),
        e.DB.prepare("UPDATE sessions SET revoked_at=? WHERE user_id=? AND device_id=? AND revoked_at IS NULL AND id IN (SELECT session_id FROM session_contexts WHERE client_kind='app') AND EXISTS(SELECT 1 FROM app_device_bindings WHERE user_id=? AND device_id=? AND revoked=1)").bind(now(),user.user_id,deviceId,user.user_id,deviceId)
      ]); } catch(error) {if(String(error).includes('device_binding_locked'))return json({error_code:'device_binding_locked',message:`设备绑定超过 ${waitDays} 天后才可解绑`},409,origin);throw error;}
      return json({success:true,device_id:deviceId},200,origin);
    }
    if (p === '/v1/translation-context' && r.method === 'GET') return json({context_version:await translationContext(e)},200,origin);
    if (p === '/v1/translate' && r.method === 'POST') {
      if (user.client_kind !== 'app') return json({error_code:'app_binding_required',message:'请使用已绑定设备的 APP 执行翻译'},403,origin);
      return translate(r,e,user);
    }
    return json(
      { error_code: "not_found", message: "接口不存在" },
      404,
      origin,
    );
}
