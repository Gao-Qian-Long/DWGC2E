export interface MailEnv {
  DB?: D1Database;
  BREVO_API_KEY?: string; RESEND_API_KEY?: string; MAIL_FROM?: string;
  MAIL_PROVIDER?: string; MAIL_FALLBACK_ENABLED?: string;
}
type Provider = "brevo" | "resend";
export interface MailMessage { to: string; subject: string; html: string; id: string }
export function mailProviders(env: MailEnv): Provider[] {
  const primary = env.MAIL_PROVIDER || "brevo";
  if (primary !== "brevo" && primary !== "resend" && primary !== "round_robin") throw new Error("Invalid MAIL_PROVIDER");
  const fallback = env.MAIL_FALLBACK_ENABLED || "true";
  if (fallback !== "true" && fallback !== "false") throw new Error("Invalid MAIL_FALLBACK_ENABLED");
  if (!env.MAIL_FROM || /[\r\n]/.test(env.MAIL_FROM)) throw new Error("Invalid MAIL_FROM");
  const candidates: Provider[] = primary === "round_robin" ? ["brevo", "resend"]
    : fallback === "true" ? [primary, primary === "brevo" ? "resend" : "brevo"] : [primary];
  const available = candidates.filter(p => Boolean(p === "brevo" ? env.BREVO_API_KEY : env.RESEND_API_KEY));
  if (!available.length) throw new Error("Mail key missing");
  return available;
}
// One atomic write allocates the preferred provider across Worker instances.
// A fallback never advances the cursor. No in-memory counters or read-then-write race.
export const MAIL_ROTATION_SQL = `INSERT INTO mail_routing_state (id, slot) VALUES ('verification', 0)
ON CONFLICT(id) DO UPDATE SET slot = 1 - mail_routing_state.slot
RETURNING slot`;
export async function selectMailProviders(env: MailEnv): Promise<Provider[]> {
  const providers = mailProviders(env);
  if (env.MAIL_PROVIDER !== "round_robin" || providers.length < 2) return providers;
  if (!env.DB) throw new Error("Mail routing database missing");
  const row = await env.DB.prepare(MAIL_ROTATION_SQL).first<{slot: number}>();
  if (!row || (row.slot !== 0 && row.slot !== 1)) throw new Error("Invalid mail routing state");
  const ordered = row.slot === 0 ? providers : [...providers].reverse();
  return env.MAIL_FALLBACK_ENABLED === "false" ? ordered.slice(0, 1) : ordered;
}
export async function deliverMail(env: MailEnv, message: MailMessage,
  fetcher: typeof fetch = fetch, timeoutMs = 8000, selected?: Provider[]): Promise<{ok: boolean; uncertain?: boolean}> {
  let uncertain = false;
  const providers = selected ?? await selectMailProviders(env);
  for (const [attempt, provider] of providers.entries()) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const from = env.MAIL_FROM!.trim();
      const match = /^(.*?)\s*<([^<>]+)>$/.exec(from);
      const response = await fetcher(provider === "brevo" ? "https://api.brevo.com/v3/smtp/email" : "https://api.resend.com/emails", {
        method: "POST", signal: controller.signal,
        headers: provider === "brevo"
          ? { "api-key": env.BREVO_API_KEY!, "Content-Type": "application/json" }
          : { Authorization: "Bearer " + env.RESEND_API_KEY, "Content-Type": "application/json", "Idempotency-Key": "verification-" + message.id },
        body: JSON.stringify(provider === "brevo" ? {
          sender: { name: match?.[1]?.trim() || "QLCAD", email: match?.[2] || from },
          to: [{ email: message.to }], subject: message.subject, htmlContent: message.html,
        } : { from, to: [message.to], subject: message.subject, html: message.html }),
      });
      // No recipient, verification code, keys or vendor response bodies in logs.
      console.info(JSON.stringify({ event: "verification_mail", primary: providers[0], attempt: attempt + 1, provider, status: response.status, request_id: message.id }));
      if (response.ok) {
        // Acceptance is not delivery. Keep a provider correlation ID for Gmail bounce/
        // suppression investigation without logging a recipient, OTP, or raw payload.
        let providerId: string | undefined;
        try {
          const receipt:any = await response.json();
          const value = provider === "brevo" ? receipt?.messageId : receipt?.id;
          if (typeof value === "string" && value.length <= 256 && !/[\r\n]/.test(value)) providerId = value;
        } catch { /* An empty/malformed receipt must not resend an accepted message. */ }
        console.info(JSON.stringify({event:"verification_mail_accepted",provider,request_id:message.id,provider_message_id:providerId,recipient_domain:message.to.split("@").pop()?.toLowerCase(),delivery_confirmed:false}));
        return { ok: true };
      }
      if (response.body) await response.body.cancel().catch(() => {});
      // A server error can still follow acceptance; preserve the code if all attempts fail.
      if (response.status >= 500) uncertain = true;
      // Only retry provider-specific authentication, quota or service failures.
      if (![401, 402, 403, 429].includes(response.status) && response.status < 500) return { ok: false, uncertain };
    } catch {
      // Network failures can occur AFTER acceptance; don't blindly duplicate delivery.
      console.warn(JSON.stringify({ event: "verification_mail_uncertain", provider, request_id: message.id }));
      return { ok: false, uncertain: true };
    } finally { clearTimeout(timer); }
  }
  return { ok: false, uncertain };
}
