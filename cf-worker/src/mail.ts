export interface MailEnv {
  BREVO_API_KEY?: string; RESEND_API_KEY?: string; MAIL_FROM?: string;
  MAIL_PROVIDER?: string; MAIL_FALLBACK_ENABLED?: string;
}
type Provider = "brevo" | "resend";
export interface MailMessage { to: string; subject: string; html: string; id: string }
export function mailProviders(env: MailEnv): Provider[] {
  const primary = env.MAIL_PROVIDER || "brevo";
  if (primary !== "brevo" && primary !== "resend") throw new Error("Invalid MAIL_PROVIDER");
  const fallback = env.MAIL_FALLBACK_ENABLED || "true";
  if (fallback !== "true" && fallback !== "false") throw new Error("Invalid MAIL_FALLBACK_ENABLED");
  if (!env.MAIL_FROM || /[\r\n]/.test(env.MAIL_FROM)) throw new Error("Invalid MAIL_FROM");
  const candidates: Provider[] = fallback === "true" ? [primary, primary === "brevo" ? "resend" : "brevo"] : [primary];
  const available = candidates.filter(p => Boolean(p === "brevo" ? env.BREVO_API_KEY : env.RESEND_API_KEY));
  if (!available.length) throw new Error("Mail key missing");
  return available;
}
export async function deliverMail(env: MailEnv, message: MailMessage,
  fetcher: typeof fetch = fetch, timeoutMs = 8000): Promise<{ok: boolean; uncertain?: boolean}> {
  let uncertain = false;
  for (const provider of mailProviders(env)) {
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
          sender: { name: match?.[1]?.trim() || "DWGC2E", email: match?.[2] || from },
          to: [{ email: message.to }], subject: message.subject, htmlContent: message.html,
        } : { from, to: [message.to], subject: message.subject, html: message.html }),
      });
      // No recipient, verification code, keys or vendor response bodies in logs.
      console.info(JSON.stringify({ event: "verification_mail", provider, status: response.status, request_id: message.id }));
      if (response.body) await response.body.cancel().catch(() => {});
      if (response.ok) return { ok: true };
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
