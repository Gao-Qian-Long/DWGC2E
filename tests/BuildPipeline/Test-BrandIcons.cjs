const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),assert=require('node:assert/strict');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const root=path.resolve(__dirname,'../..'),site=path.resolve('D:/DWGC2E_Website'),out=path.join(root,'artifacts/brand-unification-20260916');
(async()=>{
 const canonical=fs.readFileSync(path.join(root,'assets/icons/brand.svg'),'utf8');
 assert.match(canonical,/<path[^>]+d="M/);assert(!canonical.includes('System.Windows'));
 for(const f of ['assets/logo.svg','favicon.svg'])assert.equal(fs.readFileSync(path.join(site,f),'utf8'),canonical);
 const ico=fs.readFileSync(path.join(root,'assets/icons/icon.ico'));
 assert.equal(ico.readUInt16LE(2),1);assert.equal(ico.readUInt16LE(4),9);
 const sizes=[];for(let i=0;i<9;i++){const p=6+i*16,s=ico[p]||256,n=ico.readUInt32LE(p+8),o=ico.readUInt32LE(p+12);assert(o+n<=ico.length);assert.equal(ico.readUInt16LE(p+6),32);assert.equal(ico.subarray(o,o+8).toString('hex'),'89504e470d0a1a0a');assert.equal(ico.readUInt32BE(o+16),s);assert.equal(ico.readUInt32BE(o+20),s);sizes.push(s);}
 assert.deepEqual(sizes,[16,20,24,32,40,48,64,128,256]);
 const pages=fs.readdirSync(site).filter(n=>n.endsWith('.html'));
 for(const file of pages){const text=fs.readFileSync(path.join(site,file),'utf8');for(const m of text.matchAll(/(?:assets\/logo\.svg|favicon\.svg)[^"']*/g))assert(m[0].endsWith('?v=20260916-app-brand'),file);}
 const server=http.createServer((req,res)=>{const u=new URL(req.url,'http://local'),f=path.resolve(site,'.'+u.pathname);if(!f.startsWith(site+path.sep)){res.writeHead(403);return res.end();}fs.readFile(f,(e,b)=>{res.writeHead(e?404:200,{'Content-Type':({'.svg':'image/svg+xml','.html':'text/html; charset=utf-8','.css':'text/css','.js':'text/javascript'})[path.extname(f)]||'application/octet-stream'});res.end(e?'missing':b);});});
 await new Promise(r=>server.listen(0,'127.0.0.1',r));let browser;
 try{const origin='http://127.0.0.1:'+server.address().port;browser=await chromium.launch({channel:'msedge',headless:true});const p=await browser.newPage({viewport:{width:1366,height:900}});
 await p.route('**/*',r=>new URL(r.request().url()).origin===origin?r.continue():r.abort());
 await p.goto(origin+'/index.html',{waitUntil:'networkidle'});
 const brand=p.locator('header .brand img').first();await brand.waitFor();assert(await brand.evaluate(i=>i.complete&&i.naturalWidth>0));await p.screenshot({path:path.join(out,'website-desktop.png')});
 await p.setViewportSize({width:390,height:844});await p.screenshot({path:path.join(out,'website-mobile.png')});
 await p.goto(origin+'/favicon.svg?v=20260916-app-brand');await p.screenshot({path:path.join(out,'website-svg.png')});
 fs.writeFileSync(path.join(out,'verification.json'),JSON.stringify({passed:true,icoSizes:sizes,websitePagesChecked:pages.length,svgIdentical:true,browser:'Edge',network:'localhost only',deployment:false,releaseReplaced:false},null,2));console.log('PASS: nine ICO sizes, identical SVGs, versioned page references, desktop/mobile browser image load.');
 }finally{await browser?.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1});
