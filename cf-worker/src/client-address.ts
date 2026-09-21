/** Only the configured Pages proxy can attest an originating address. */
export type ProxyIdentityEnv = { WEB_PROXY_IDENTITY_KEY?: string };
const hex = (bytes: ArrayBuffer) => Array.from(new Uint8Array(bytes), b=>b.toString(16).padStart(2,'0')).join('');
// L4: a missing/short key silently degrades every request to the edge address; say so once
// per isolate instead of once per request.
let proxyKeyWarned = false;
/** Test hook: node:test runs each file in its own process, but several suites share one file. */
export const __resetProxyKeyWarningForTests = () => { proxyKeyWarned = false; };
export async function clientAddress(request: Request, env: ProxyIdentityEnv): Promise<string> {
  const edge = request.headers.get('cf-connecting-ip') || 'unknown';
  const ip=request.headers.get('x-dwgc-client-ip')||'', timestamp=request.headers.get('x-dwgc-client-time')||'', signature=request.headers.get('x-dwgc-client-signature')||'';
  if (!env.WEB_PROXY_IDENTITY_KEY || env.WEB_PROXY_IDENTITY_KEY.length<32 || !/^[0-9a-fA-F:.]{3,64}$/.test(ip) || !/^\d{10}$/.test(timestamp) || Math.abs(Date.now()/1000-Number(timestamp))>60 || !/^[0-9a-f]{64}$/.test(signature)) {
    if (!proxyKeyWarned && (!env.WEB_PROXY_IDENTITY_KEY || env.WEB_PROXY_IDENTITY_KEY.length<32)) {
      proxyKeyWarned = true;
      console.error(JSON.stringify({event:'proxy_identity_key_missing',effect:'proxy attested addresses fall back to the edge IP'}));
    }
    return edge;
  }
  const url=new URL(request.url);
  const payload=[timestamp,ip,request.method,url.pathname+url.search,request.headers.get('authorization')||''].join('\n');
  const key=await crypto.subtle.importKey('raw',new TextEncoder().encode(env.WEB_PROXY_IDENTITY_KEY),{name:'HMAC',hash:'SHA-256'},false,['sign']);
  const expected=hex(await crypto.subtle.sign('HMAC',key,new TextEncoder().encode(payload)));
  let diff=0;for(let i=0;i<64;i++)diff|=expected.charCodeAt(i)^signature.charCodeAt(i);
  return diff===0?ip:edge;
}
