import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,readdirSync} from 'node:fs';
import {join,relative} from 'node:path';
import {fileURLToPath} from 'node:url';

const root = fileURLToPath(new URL('../src/', import.meta.url));
const walk = dir => readdirSync(dir,{withFileTypes:true}).flatMap(entry =>
  entry.isDirectory() ? walk(join(dir,entry.name)) : (entry.name.endsWith('.ts') ? [join(dir,entry.name)] : []));

const findInterpolatedSql = () => {
  const found = [];
  for (const file of walk(root)) {
    readFileSync(file,'utf8').split('\n').forEach((line,index) => {
      if (/prepare\(\s*`/m.test(line) && line.includes('${'))
        found.push(relative(root,file).split('\\').join('/')+':'+(index+1));
    });
  }
  return found.sort();
};

// Ratchet: every SQL built with template interpolation is listed here, and each entry has been
// reviewed as a server-owned literal — a column projection, a table/column chosen from a fixed
// mapping, or a generated placeholder list. None of them carry request data.
// Adding an entry therefore means adding a review, not just updating a file.
const approved = JSON.parse(readFileSync(new URL('./sql-interpolation-approved.json',import.meta.url),'utf8')).sort();

test('every interpolated SQL statement is on the reviewed allowlist',()=>{
  const found = findInterpolatedSql();
  const added = found.filter(x => !approved.includes(x));
  const stale = approved.filter(x => !found.includes(x));
  assert.deepEqual(added,[],`new interpolated SQL needs review; if it only splices server-owned literals, add it to sql-interpolation-approved.json: ${added.join(', ')}`);
  assert.deepEqual(stale,[],`reviewed entries no longer exist, remove them from sql-interpolation-approved.json: ${stale.join(', ')}`);
});

test('no SQL statement is concatenated out of a variable',()=>{
  // Only the statement itself matters: a `+` after the closing quote of prepare() means the SQL
  // text is partly built at runtime. Concatenation inside bind() is a parameter, not a statement.
  const offenders = [];
  for (const file of walk(root)) {
    readFileSync(file,'utf8').split('\n').forEach((line,index) => {
      if (/prepare\(\s*['"`][^'"`]*['"`]\s*\+/.test(line))
        offenders.push(relative(root,file).split('\\').join('/')+':'+(index+1));
    });
  }
  assert.deepEqual(offenders,[]);
});
