/** Request ingress guard. Does not alter payload bytes, signatures, or business rules. */
export class RequestBodyError extends Error {
  readonly status: number;
  readonly code: string;
  constructor(status: number, code: string, message: string) { super(message); this.status = status; this.code = code; }
}
export function requestBodyLimit(path: string): number {
  // Includes worst-case escaped Unicode for all 1,000 glossary entries.
  if (path === '/v1/glossary' || path === '/v1/translate') return 16 * 1024 * 1024;
  if (path === '/v1/feedback' || path.startsWith('/v1/admin/feedback')) return 12000;
  if (path === '/v1/admin/users' || path.startsWith('/v1/admin/users/')) return 4096;
  return 64 * 1024;
}
const tooLarge = () => new RequestBodyError(413, 'payload_too_large', '请求内容过大，请减少内容后重试。');
export async function guardRequestBody(request: Request): Promise<Request> {
  if (!request.body || request.method === 'OPTIONS') return request;
  const path = new URL(request.url).pathname, limit = requestBodyLimit(path);
  const declared = request.headers.get('content-length');
  if (declared && /^\d+$/.test(declared) && Number(declared) > limit) {
    void request.body.cancel().catch(() => {});
    throw tooLarge();
  }
  const reader = request.body.getReader(), chunks: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      const {done, value} = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > limit) {
        void reader.cancel().catch(() => {});
        throw tooLarge();
      }
      chunks.push(value);
    }
  } catch (error) {
    if (error instanceof RequestBodyError) throw error;
    throw new RequestBodyError(400, 'invalid_request', '请求内容读取失败，请重试。');
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  // Gateway callbacks may be signed form data. Preserve their exact bytes and parsing.
  if (size && path !== '/v1/billing/notify/ezfpy' &&
      (request.headers.get('content-type') || '').split(';')[0].trim().toLowerCase() === 'application/json') {
    try { JSON.parse(new TextDecoder('utf-8', {fatal: true, ignoreBOM: false}).decode(bytes)); }
    catch { throw new RequestBodyError(400, 'invalid_json', '请求 JSON 格式无效。'); }
  }
  return new Request(request, {body: bytes});
}
