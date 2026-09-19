import {test} from 'node:test';
import assert from 'node:assert/strict';
import {protectGlossary} from '../src/translation-glossary.ts';
test('longest equal-priority span and repeated occurrences restore without changing values',()=>{
 const p=protectGlossary('大法兰 法兰 法兰 10',[{source:'法兰',target:'Flange'},{source:'大法兰',target:'Large flange'}]);
 assert.equal(p.restore(p.text),'Large flange Flange Flange 10');
 assert.equal(p.restore(p.text+p.text),null);
});
test('user terminology wins even when a lower priority system phrase is longer',()=>{
 const p=protectGlossary('大法兰',[{source:'大法兰',target:'System',priority:0},{source:'法兰',target:'User term',priority:2}]);
 assert.equal(p.restore(p.text),'大User term');
});
test('glossary sources are literal, case-insensitive and targets never reinterpreted',()=>{
 const p=protectGlossary('a+b A+B',[{source:'A+B',target:'$& __GLOSSARY_0__'}]);
 assert.equal(p.restore(p.text),'$& __GLOSSARY_0__ $& __GLOSSARY_0__');
});
