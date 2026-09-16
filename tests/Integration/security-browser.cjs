// Real HTTP CSP enforcement. No production accounts or network access.
const {test,before,after}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),crypto=require('node:crypto');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const root=path.resolve(process.env.WEBSITE_TEST_ROOT||path.resolve(__dirname,'../../../DWGC2E_Website'));
const policy=fs.readFileSync(path.join(root,'_headers'),'utf8').match(/^\s*Content-Security-Policy:\s*(.+)$/m)?.[1];
let browser,server,origin;
before(async()=>{
 assert.ok(policy,'An actual CSP is required');
 server=http.createServer((req,res)=>{
  const url=new URL(req.url,'http://local');
  if(url.pathname==='/security-eval-probe.js'){res.writeHead(200,{'content-type':'application/javascript'});return res.end("try { new Function('window.__attack++')(); window.__evalBlocked=false; } catch { window.__evalBlocked=true; }");}
  if(url.pathname==='/api/security-probe'){res.writeHead(200,{'content-type':'application/json'});return res.end('{"ok":true}');}
  const file=path.resolve(root,'.'+decodeURIComponent(url.pathname));
  if(!file.startsWith(root+path.sep)){res.writeHead(403);return res.end();}
  fs.readFile(file,(err,data)=>{res.writeHead(err?404:200,{'Content-Security-Policy':policy,'Content-Type':({'.html':'text/html; charset=utf-8','.js':'application/javascript','.css':'text/css','.svg':'image/svg+xml','.json':'application/json'})[path.extname(file)]||'application/octet-stream'});res.end(err?'missing':data);});
 });
 await new Promise(r=>server.listen(0,'127.0.0.1',r));origin='http://127.0.0.1:'+server.address().port;
 browser=await chromium.launch({channel:'msedge',headless:true});
});
after(async()=>{await browser?.close();if(server)await new Promise(r=>server.close(r));});
async function page(t,width=1280){
 const p=await browser.newPage({viewport:{width,height:900}});t.after(()=>p.close());
 await p.route('**/*',r=>new URL(r.request().url()).origin===origin?r.continue():r.abort());
 await p.addInitScript(()=>{window.__violations=[];document.addEventListener('securitypolicyviolation',e=>window.__violations.push({directive:e.effectiveDirective,blocked:e.blockedURI}));});return p;
}
test('all inline script bodies have exact CSP hashes; no inline event attributes',()=>{
 assert.ok(!/script-src[^;]*'unsafe-(inline|eval)'/.test(policy));
 for(const f of fs.readdirSync(root).filter(x=>x.endsWith('.html'))){
  const html=fs.readFileSync(path.join(root,f),'utf8').replace(/\r\n/g,'\n');
  for(const m of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi))if(!/\bsrc\s*=/.test(m[1]))assert.ok(policy.includes("'sha256-"+crypto.createHash('sha256').update(m[2]).digest('base64')+"'"),f+' inline script');
  assert.ok(!/<[^>]+\son\w+\s*=/i.test(html),f+' inline event');
 }
});
for(const width of [390,1280])test('all public HTML pages load without CSP violations at '+width,async t=>{
 const p=await page(t,width),errors=[];p.on('pageerror',e=>errors.push(e.message));
 for(const file of fs.readdirSync(root).filter(x=>x.endsWith('.html'))){
  const response=await p.goto(origin+'/'+file);assert.equal(response.status(),200,file);
  await p.waitForLoadState('networkidle');
  assert.deepEqual(await p.evaluate(()=>window.__violations),[],file);
  assert.deepEqual(errors,[],file);
 }
});
test('browser blocks script injection, handlers, eval and foreign connections but allows own API',async t=>{
 const p=await page(t);await p.goto(origin+'/index.html');
 assert.equal(await p.evaluate(()=>document.documentElement.classList.contains('js')),true);
 const result=await p.evaluate(async()=>{
  window.__attack=0;
  const s=document.createElement('script');s.textContent='window.__attack++';document.body.append(s);
  const b=document.createElement('button');b.setAttribute('onclick','window.__attack++');document.body.append(b);b.click();
  // DevTools evaluate bypasses eval CSP; run the probe in a real same-origin script.
  await new Promise((resolve,reject)=>{const probe=document.createElement('script');probe.src='/security-eval-probe.js';probe.onload=resolve;probe.onerror=reject;document.body.append(probe);});
  const evalBlocked=window.__evalBlocked;
  let fetchBlocked=false;try{await fetch('https://csp-probe.invalid/');}catch{fetchBlocked=true;}
  const own=await(await fetch('/api/security-probe')).json();
  return {attack:window.__attack,evalBlocked,fetchBlocked,own};
 });
 assert.deepEqual(result,{attack:0,evalBlocked:true,fetchBlocked:true,own:{ok:true}});
 await p.waitForFunction(()=>window.__violations.length>=4);
 const violations=await p.evaluate(()=>window.__violations);
 for(const directive of ['script-src-elem','script-src-attr','script-src','connect-src'])assert.ok(violations.some(x=>x.directive===directive),JSON.stringify(violations));
});
