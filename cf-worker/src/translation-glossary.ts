/** Protect matched spans upstream only: quota and idempotency still use original input. */
export function protectGlossary(text: string, entries: {source:string;target:string;priority?:number}[]) {
  const terms = new Map<string,{source:string;target:string;priority?:number}>();
  for (const entry of entries) {
    const source=entry.source.trim(), target=entry.target.trim();
    if (!source || !target) continue;
    const key=source.toLowerCase(), previous=terms.get(key);
    if(previous && (previous.priority??0)>(entry.priority??0))continue;
    if(previous && (previous.priority??0)===(entry.priority??0) && previous.target!==target) throw new Error('glossary_conflict');
    terms.set(key,{source,target,priority:entry.priority});
  }
  let prefix: string;
  do { prefix='__DWGTERM_'+crypto.randomUUID().replaceAll('-','')+'_'; }
  while(text.includes(prefix)||entries.some(e=>e.target.includes(prefix)));
  const occupied=new Uint8Array(text.length);
  const spans: {start:number;length:number;target:string;token:string}[]=[];
  for(const term of [...terms.values()].sort((a,b)=>(b.priority??0)-(a.priority??0)||b.source.length-a.source.length)) {
    const pattern=new RegExp(term.source.replace(/[.*+?^${}()|[\]\\]/g,'\\$&'),'giu');
    for(const match of text.matchAll(pattern)) {
      const start=match.index!;
      if(occupied.subarray(start,start+match[0].length).some(Boolean))continue;
      occupied.fill(1,start,start+match[0].length);
      spans.push({start,length:match[0].length,target:term.target,token:prefix+spans.length+'__'});
    }
  }
  let protectedText=text;
  for(const span of [...spans].sort((a,b)=>b.start-a.start))
    protectedText=protectedText.slice(0,span.start)+span.token+protectedText.slice(span.start+span.length);
  return {text:protectedText,restore(value:string):string|null {
    if(spans.some(s=>value.split(s.token).length!==2))return null;
    // Restore simultaneously so a target is never interpreted as another placeholder.
    const targets=new Map(spans.map(s=>[s.token,s.target]));
    const restored=value.replace(new RegExp(prefix+'\\d+__','g'),token=>targets.get(token)??token);
    return restored.includes(prefix)?null:restored;
  }};
}
