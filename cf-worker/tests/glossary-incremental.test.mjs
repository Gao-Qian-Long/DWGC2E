import {test} from 'node:test';
import assert from 'node:assert/strict';
import {applyGlossaryPatch} from '../src/account/glossary.ts';
const id=n=>'00000000-0000-4000-8000-'+String(n).padStart(12,'0');
const term=(n,source='source'+n)=>({id:id(n),source,target:'target'+n,source_lang:'ZH',target_lang:'EN',direction_pending:false});
test('selected upload A preserves unselected cloud C and never uploads B',()=>{
 const a=term(1),b=term(2),c=term(3); const result=applyGlossaryPatch([c],[a],[]);
 assert.deepEqual(result,[c,{...a,category:'默认分类'}]); assert.ok(!result.some(x=>x.id===b.id)); assert.deepEqual(c,term(3));
});
test('explicit deletion alone removes only the requested identity',()=>{
 const a=term(1),b=term(2); assert.deepEqual(applyGlossaryPatch([a,b],[],[a.id]),[b]);
 assert.deepEqual(applyGlossaryPatch([a,b],[],[]),[a,b]);
});
test('different directions and translations are retained, invalid batch is atomic',()=>{
 const a=term(1),reverse={...term(2,a.source),source_lang:'EN',target_lang:'ZH'};
 assert.equal(applyGlossaryPatch([a],[reverse],[]).length,2);
 const previous=[a]; assert.throws(()=>applyGlossaryPatch(previous,[{...term(3),source_lang:''}],[])); assert.deepEqual(previous,[a]);
});
test('capacity refuses growth rather than truncating existing records',()=>{
 const previous=Array.from({length:1000},(_,i)=>term(i+1));
 assert.throws(()=>applyGlossaryPatch(previous,[term(1001)],[])); assert.equal(previous.length,1000);
 assert.equal(applyGlossaryPatch(previous,[{...previous[0],target:'updated'}],[]).length,1000);
});
