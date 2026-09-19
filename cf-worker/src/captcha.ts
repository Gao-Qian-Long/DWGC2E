type Env = { DB:D1Database; PASSWORD_PEPPER:string };
export async function captchaHash(value:string) {
 return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(value))),x=>x.toString(16).padStart(2,'0')).join('');
}
/** A challenge is only usable by the client that requested it for the same mailbox and purpose. */
export async function captchaBinding(email:string,purpose:string,clientKey='') {
 return captchaHash(email+'|'+purpose+'|'+clientKey);
}
// Seven-segment template. The rendered image must not encode the answer: a parser that reads the
// path data back would defeat the challenge entirely (see the reported pre-fix behaviour, where the
// stroke order was a deterministic function of the digit). Every issuance shuffles strokes, jitters
// every path and adds displaced decoy strokes that read as interference but cannot be classified.
const glyphs=['abcdef','bc','abged','abgcd','fgbc','afgcd','afgecd','abc','abcdefg','abfgcd'];
const segmentPaths:Record<string,string>={a:'M3 1H15',b:'M17 3V15',c:'M17 19V31',d:'M3 33H15',e:'M1 19V31',f:'M1 3V15',g:'M3 17H15'};
const horizontalSegments=new Set(['a','d','g']);
export type CaptchaRandom=()=>number;
const shuffle=(values:string[],random:CaptchaRandom)=>{
 for(let i=values.length-1;i>0;i--){const j=random()%(i+1);const swap=values[i];values[i]=values[j];values[j]=swap;}
 return values;
};
export function renderCaptchaSvg(code:string,random:CaptchaRandom) {
 const span=(range:number)=>random()%range;
 const digitGlyph=(parts:string[])=>parts.map(letter=>`<path transform="translate(${span(5)-2} ${span(5)-2})" d="${segmentPaths[letter]}"/>`).join('');
 // Decoys are displaced along their own axis by 5-6 units, so they stay closest to a segment the
 // digit does not contain: the stroke set is never a valid glyph set, but a reader still sees the
 // intended numeral with a stray scratch next to it.
 const digitNoise=(parts:string[])=>{
  if(!span(2))return '';
  const candidates=shuffle(Object.keys(segmentPaths).filter(letter=>!parts.includes(letter)),random);
  const shift=(span(2)?1:-1)*(5+span(2));
  return candidates.slice(0,1).map(letter=>horizontalSegments.has(letter)
   ?`<path transform="translate(0 ${shift})" d="${segmentPaths[letter]}"/>`
   :`<path transform="translate(${shift} 0)" d="${segmentPaths[letter]}"/>`).join('');
 };
 const digits=[...code].map((digit,index)=>{
  const parts=shuffle([...glyphs[Number(digit)]],random);
  return `<g transform="translate(${12+index*29},9) rotate(${span(21)-10},9,17)">${digitGlyph(parts)}${digitNoise(parts)}</g>`;
 }).join('');
 const noise=Array.from({length:3},()=>`<path d="M${span(170)} ${span(52)}L${span(170)} ${span(52)}" stroke="#c3cddb" stroke-width="1"/>`).join('');
 return `<svg xmlns="http://www.w3.org/2000/svg" width="170" height="52" viewBox="0 0 170 52"><rect width="170" height="52" fill="#f1f5f9"/>${noise}<g fill="none" stroke="#233754" stroke-width="3" stroke-linecap="round">${digits}</g></svg>`;
}
export async function issueCaptcha(e:Env,email:string,purpose:string,clientKey='') {
 const random:CaptchaRandom=()=>crypto.getRandomValues(new Uint32Array(1))[0];
 const id=crypto.randomUUID(),code=String(random()%100000).padStart(5,'0');
 const binding=await captchaBinding(email,purpose,clientKey),answer=await captchaHash(id+'|'+code+'|'+e.PASSWORD_PEPPER);
 const seconds=Math.floor(Date.now()/1000);
 await e.DB.prepare('DELETE FROM numeric_captchas WHERE expires_at<?').bind(seconds-600).run();
 // At most one outstanding challenge per binding: a new image invalidates the previous one, so a
 // mailbox cannot be made to accumulate solvable challenges for a third party to consume later.
 await e.DB.prepare('DELETE FROM numeric_captchas WHERE binding=? AND used=0').bind(binding).run();
 await e.DB.prepare('INSERT INTO numeric_captchas(id,binding,answer_hash,expires_at) VALUES(?,?,?,?)').bind(id,binding,answer,seconds+300).run();
 return {captcha_id:id,image:'data:image/svg+xml;base64,'+btoa(renderCaptchaSvg(code,random)),expires_in:300};
}
export async function consumeCaptcha(e:Env,email:string,purpose:string,id:unknown,code:unknown,clientKey='') {
 if(typeof id!=='string'||!/^[-a-f0-9]{36}$/.test(id)||typeof code!=='string'||!/^\d{5}$/.test(code))return false;
 const binding=await captchaBinding(email,purpose,clientKey),hash=await captchaHash(id+'|'+code+'|'+e.PASSWORD_PEPPER);
 // Every syntactically valid guess consumes the challenge. No verify/use race.
 const row=await e.DB.prepare('UPDATE numeric_captchas SET attempts=attempts+1,used=1 WHERE id=? AND binding=? AND expires_at>? AND used=0 RETURNING answer_hash').bind(id,binding,Math.floor(Date.now()/1000)).first<{answer_hash:string}>();
 return row?.answer_hash===hash;
}
