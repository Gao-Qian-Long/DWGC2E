import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import {createHash} from 'node:crypto';
import {fileURLToPath} from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const result = (id, status, detail) => ({id, status, detail});
const secureUrl = value => {
  try { const u = new URL(value); return u.protocol === 'https:' && !u.username && !u.password; }
  catch { return false; }
};
export function inspectVersion(data, websiteOrigin, requireDownload = false) {
  if (!data || typeof data.latest_version !== 'string' || !/^\d+\.\d+\.\d+(?:[-+][\w.-]+)?$/.test(data.latest_version))
    return result('api.version', 'fail', '版本响应缺少有效 latest_version');
  if (!data.download_url) return result('api.version', requireDownload ? 'fail' : 'warn', `版本 ${data.latest_version}，尚无公开下载地址（未自动修改发行配置）`);
  if (!secureUrl(data.download_url) || new URL(data.download_url).href.replace(/\/$/, '') === websiteOrigin.replace(/\/$/, ''))
    return result('api.version', 'fail', '下载地址不是安全的独立下载入口');
  return result('api.version', 'pass', `版本 ${data.latest_version}；下载地址格式有效，未验证安装包内容`);
}
export function inspectCors(response, websiteOrigin) {
  const tokens = name => (response.headers.get(name) || '').toLowerCase().split(',').map(s => s.trim());
  const ok = response.status === 204 && response.headers.get('access-control-allow-origin') === websiteOrigin
    && ['content-type', 'authorization', 'idempotency-key'].every(h => tokens('access-control-allow-headers').includes(h))
    && ['get', 'post', 'put', 'patch', 'delete'].every(m => tokens('access-control-allow-methods').includes(m));
  return result('api.cors', ok ? 'pass' : 'fail', ok ? '预检状态、明确来源、跨端方法和请求头均匹配' : '预检状态/来源/方法/请求头不完整，不能判为通过');
}
export async function checkApi({baseUrl, websiteOrigin, fetchImpl = fetch, requireDownload = false, sameOriginProxy = false}) {
  if (!secureUrl(baseUrl) || !secureUrl(websiteOrigin)) throw Error('API 与官网地址必须为不含凭证的 HTTPS URL');
  baseUrl = baseUrl.replace(/\/$/, '');
  const checks = [];
  async function probe(id, route, inspect, options = {}) {
    try {
      const response = await fetchImpl(baseUrl + route, {...options, redirect:'manual', signal:AbortSignal.timeout(12000)});
      if (response.status >= 300 && response.status < 400) throw Error('redirect');
      checks.push(await inspect(response));
    } catch { checks.push(result(id, 'fail', '请求失败、超时、重定向或响应不是预期 JSON；未执行任何写操作')); }
  }
  await probe('api.health', '/v1/health', async r => {
    const d = await r.json(); return result('api.health', r.status === 200 && d.api === 'operational' && d.database === 'operational' ? 'pass' : 'fail', '检查 API 与数据库健康状态');
  });
  await probe('api.version', '/v1/version', async r => r.status === 200 ? inspectVersion(await r.json(), websiteOrigin, requireDownload) : result('api.version','fail',`HTTP ${r.status}`));
  await probe('api.plans', '/v1/billing/plans', async r => {
    const d = await r.json(); const valid = r.status === 200 && Array.isArray(d.plans) && typeof d.paymentsEnabled === 'boolean';
    return result('api.plans', !valid ? 'fail' : d.paymentsEnabled && d.plans.length === 0 ? 'warn' : 'pass', valid ? `购买开关=${d.paymentsEnabled}，套餐=${d.plans.length}；未下单` : '套餐响应格式或状态异常');
  });
  for (const route of ['/v1/profile','/v1/devices','/v1/billing/orders','/v1/terminology','/v1/translation/history']) {
    await probe('anonymous' + route, route, async r => {
      const d = await r.json(); return result('anonymous' + route, r.status === 401 && ['unauthenticated','session_expired','token_expired'].includes(d.error_code) ? 'pass' : 'fail', `匿名请求 HTTP ${r.status}，必须明确拒绝未登录访问`);
    });
  }
  if (sameOriginProxy) {
    await probe('proxy.no-store', '/v1/profile', r => result('proxy.no-store', /(?:^|,)\s*no-store\s*(?:,|$)/i.test(r.headers.get('cache-control') || '') ? 'pass' : 'fail', '同源 Pages 代理不要求跨域 CORS；检查私有响应禁止缓存'));
  } else await probe('api.cors', '/v1/profile', r => inspectCors(r, websiteOrigin), {method:'OPTIONS',headers:{Origin:websiteOrigin,'Access-Control-Request-Method':'PATCH','Access-Control-Request-Headers':'authorization,content-type,idempotency-key'}});
  return checks;
}
export function checkLocal({workspace = root, websiteRoot = 'D:/DWGC2E_Website'} = {}) {
  const checks = [];
  const inspect = (id, fn) => { try { checks.push(fn()); } catch { checks.push(result(id,'fail','本地文件缺失或格式无效，不能判为通过')); } };
  let site, release, workerVersion;
  inspect('local.website', () => {
    const context = {window:{}};
    vm.runInNewContext(fs.readFileSync(path.join(websiteRoot,'js/site-config.js'),'utf8'),context,{timeout:1000});
    site = context.window.DWGC2E_SITE;
    if (!site || typeof site.version !== 'string') throw Error();
    return result('local.website','pass',`官网配置版本 ${site.version}`);
  });
  inspect('local.release', () => {
    release = JSON.parse(fs.readFileSync(path.join(workspace,'release/build-info.json'),'utf8').replace(/^\uFEFF/, ''));
    const hash = createHash('sha256').update(fs.readFileSync(path.join(workspace,'release/DwgTranslator.exe'))).digest('hex');
    return result('local.release',hash.toLowerCase() === String(release.sha256).toLowerCase() ? 'pass' : 'fail',`已安装版本 ${release.version}；核对本地 EXE 与构建记录哈希（非数字签名验证）`);
  });
  inspect('local.worker', () => {
    const config = fs.readFileSync(path.join(workspace,'cf-worker/wrangler.toml'),'utf8');
    workerVersion = config.match(/^LATEST_VERSION\s*=\s*"([^"]*)"/m)?.[1];
    if (!workerVersion) throw Error();
    return result('local.worker','pass',`Worker 本地配置版本 ${workerVersion}（不是线上配置证明）`);
  });
  if (site && release && workerVersion) {
    const appVersion = String(release.version).split('+')[0];
    checks.push(result('local.versions',site.version === workerVersion && workerVersion === appVersion ? 'pass' : 'warn',`官网=${site.version} / Worker配置=${workerVersion} / 本地APP=${appVersion}；本地构建不等于获准公开发行`));
  }
  inspect('local.manifest', () => {
    const manifest = path.join(websiteRoot,'update/latest.json');
    return result('local.manifest','warn',fs.existsSync(manifest) ? '存在清单源文件，仍需验证发布白名单及线上响应' : '官网源目录未提供 update/latest.json；APP 依赖 Worker 回退，需单独验收发行链路');
  });
  return checks;
}

async function main(args) {
  const options = {baseUrl:'https://dwgc2e-api.maplehousezz.workers.dev',websiteOrigin:'https://cad.pocketter.dpdns.org'};
  const values = {'--base-url':'baseUrl','--website-origin':'websiteOrigin','--website-root':'websiteRoot','--output':'output'};
  const flags = new Set(['--live','--api-only','--require-download','--same-origin-proxy']);
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (values[a]) { if (!args[i+1] || args[i+1].startsWith('--')) throw Error(`缺少参数 ${a}`); options[values[a]] = args[++i]; }
    else if (flags.has(a)) options[a.slice(2)] = true;
    else throw Error(`未知参数 ${a}`);
  }
  if (options['api-only'] && !options.live) throw Error('--api-only 必须配合 --live，避免空检查误报通过');
  const checks = options['api-only'] ? [] : checkLocal(options);
  if (options.live) checks.push(...await checkApi({...options,requireDownload:options['require-download'],sameOriginProxy:options['same-origin-proxy']}));
  const report = {checkedAt:new Date().toISOString(),readOnly:true,productionWrites:false,checks,
    summary:{pass:checks.filter(c=>c.status==='pass').length,warn:checks.filter(c=>c.status==='warn').length,fail:checks.filter(c=>c.status==='fail').length}};
  if (options.output) fs.writeFileSync(path.resolve(options.output),JSON.stringify(report,null,2)+'\n');
  console.log(JSON.stringify(report,null,2));
  process.exitCode = report.summary.fail ? 1 : 0;
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main(process.argv.slice(2)).catch(e=>{console.error(e.message);process.exitCode=1;});
