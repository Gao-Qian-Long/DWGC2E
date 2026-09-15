/** EZFPY legacy wire protocol only. Never use MD5 for passwords or sessions. */
export function md5(text: string): string {
  const input = new TextEncoder().encode(text), size = Math.ceil((input.length + 9) / 64) * 64;
  const bytes = new Uint8Array(size); bytes.set(input); bytes[input.length] = 128;
  const view = new DataView(bytes.buffer); view.setUint32(size - 8, input.length * 8, true);
  view.setUint32(size - 4, Math.floor(input.length / 0x20000000), true);
  const shifts = [7,12,17,22,5,9,14,20,4,11,16,23,6,10,15,21];
  let a0=0x67452301,b0=0xefcdab89,c0=0x98badcfe,d0=0x10325476;
  for(let offset=0;offset<size;offset+=64){
    let a=a0,b=b0,c=c0,d=d0;
    for(let i=0;i<64;i++){
      const round=i>>4;
      const f=round===0?(b&c)|(~b&d):round===1?(d&b)|(~d&c):round===2?b^c^d:c^(b|~d);
      const g=round===0?i:round===1?(5*i+1)%16:round===2?(3*i+5)%16:(7*i)%16;
      const x=(a+f+Math.floor(Math.abs(Math.sin(i+1))*4294967296)+view.getUint32(offset+g*4,true))|0;
      const s=shifts[round*4+i%4], oldD=d;
      d=c;c=b;b=(b+((x<<s)|(x>>>(32-s))))|0;a=oldD;
    }
    a0=(a0+a)|0;b0=(b0+b)|0;c0=(c0+c)|0;d0=(d0+d)|0;
  }
  return [a0,b0,c0,d0].map(n=>[0,8,16,24].map(s=>((n>>>s)&255).toString(16).padStart(2,'0')).join('')).join('');
}
export function canonicalizeEzfpyParams(params: Record<string,string|null|undefined>): string {
  return Object.keys(params).sort().filter(k=>k!=='sign'&&k!=='sign_type'&&params[k]!=null&&params[k]!=='').map(k=>`${k}=${params[k]}`).join('&');
}
export const createEzfpySign=(params:Record<string,string|null|undefined>,key:string)=>md5(canonicalizeEzfpyParams(params)+key);
export function verifyEzfpySign(params:Record<string,string>,key:string):boolean {
  if(!/^[a-fA-F0-9]{32}$/.test(params.sign||''))return false;
  const expected=createEzfpySign(params,key),actual=params.sign.toLowerCase();let diff=0;
  for(let i=0;i<32;i++)diff|=expected.charCodeAt(i)^actual.charCodeAt(i);
  return diff===0;
}
export function moneyStringToCents(value:unknown):number {
  if(typeof value!=='string'||!/^\d{1,9}(\.\d{1,2})?$/.test(value))throw Error('invalid_money');
  const [whole,fraction='']=value.split('.');const n=Number(whole)*100+Number(fraction.padEnd(2,'0'));
  if(!Number.isSafeInteger(n)||n<=0)throw Error('invalid_money');return n;
}
export function centsToMoneyString(n:number):string {
  if(!Number.isSafeInteger(n)||n<=0)throw Error('invalid_money');return `${Math.floor(n/100)}.${String(n%100).padStart(2,'0')}`;
}
