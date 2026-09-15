export interface SessionEnv { DB: D1Database; SESSION_TTL_DAYS?: string }
export interface SessionUser {
  session_id: string; user_id: string; device_id: string; client_kind: 'web' | 'app';
  account: string; display_name: string; email: string; is_active: number; expires_at: string;
}
export async function tokenHash(value: string) {
  return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value))), x => x.toString(16).padStart(2, '0')).join('');
}
export async function authenticate(request: Request, env: SessionEnv): Promise<SessionUser | null> {
  const header = request.headers.get('authorization') || '';
  if (!header.startsWith('Bearer ') || header.length > 512) return null;
  const row = await env.DB.prepare(`SELECT s.id AS session_id,s.user_id,s.device_id,c.client_kind,
    u.account,u.display_name,u.email,u.is_active,s.expires_at
    FROM sessions s JOIN users u ON u.id=s.user_id JOIN session_contexts c ON c.session_id=s.id
    WHERE s.token_hash=? AND s.revoked_at IS NULL
    AND (c.client_kind='web' OR EXISTS(SELECT 1 FROM app_device_bindings b
      WHERE b.user_id=s.user_id AND b.device_id=s.device_id AND b.revoked=0))`)
    .bind(await tokenHash(header.slice(7))).first<SessionUser>();
  if (!row || !row.is_active || !Number.isFinite(Date.parse(row.expires_at)) || Date.parse(row.expires_at) <= Date.now()) return null;
  return row;
}
export async function issueSession(env: SessionEnv, userId: string, kind: 'web' | 'app', expectedPasswordHash: string, deviceId = '', deviceName = '') {
  const id = crypto.randomUUID(), token = crypto.randomUUID().replaceAll('-', '') + crypto.randomUUID().replaceAll('-', '');
  const timestamp = new Date().toISOString();
  const configured = Number(env.SESSION_TTL_DAYS || 30);
  const days = Number.isFinite(configured) ? Math.max(1, Math.min(30, configured)) : 30;
  const expires = new Date(Date.now() + days * 86400000).toISOString();
  const statements: D1PreparedStatement[] = [];
  if (kind === 'app') statements.push(env.DB.prepare(`INSERT INTO app_device_bindings
    (user_id,device_id,device_name,platform,first_seen,last_seen,revoked) SELECT ?,?,?,'Windows',?,?,0 FROM users WHERE id=? AND password_hash=? AND is_active=1
    ON CONFLICT(user_id,device_id) DO UPDATE SET device_name=excluded.device_name,last_seen=excluded.last_seen,revoked=0`)
    .bind(userId, deviceId, deviceName.slice(0,128), timestamp, timestamp, userId, expectedPasswordHash));
  statements.push(
    env.DB.prepare('INSERT INTO sessions(id,user_id,token_hash,device_id,expires_at,created_at) SELECT ?,?,?,?,?,? FROM users WHERE id=? AND password_hash=? AND is_active=1')
      .bind(id,userId,await tokenHash(token),kind === 'app' ? deviceId : '',expires,timestamp,userId,expectedPasswordHash),
    env.DB.prepare('INSERT INTO session_contexts(session_id,client_kind) SELECT ?,? WHERE EXISTS(SELECT 1 FROM sessions WHERE id=?)').bind(id,kind,id)
  );
  const results = await env.DB.batch(statements);
  if (!results[results.length - 1].meta.changes) throw new Error('session_credentials_changed');
  return {success:true,token,expires_at:expires};
}
export async function logout(env: SessionEnv, user: SessionUser) {
  await env.DB.prepare('UPDATE sessions SET revoked_at=? WHERE id=? AND user_id=? AND revoked_at IS NULL')
    .bind(new Date().toISOString(),user.session_id,user.user_id).run();
}
