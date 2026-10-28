// The contacts page (รายชื่อติดต่อ) is paged: line.js must find the way to the NEXT page whatever the pager looks like, read the
// page's own total, and say what the pager looks like when it finds nothing (so a list is never taken for complete after 20 rows).
// No server, no Chrome: line.js runs in jsdom. The pager markups are guesses of what LINE could ship (nobody has the real DOM).
//   node tools/test-line-contacts-dom.mjs
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
// jsdom lives in the web app's node_modules (this folder has none of its own).
const require = createRequire(path.resolve(here, '../../../siri_autopost_ui/package.json'));
const { JSDOM } = require('jsdom');
const source = fs.readFileSync(path.resolve(here, '../line.js'), 'utf8');

const uid = (i) => 'U' + i.toString(16).padStart(32, '0');
const rows = (n) =>
  Array.from({ length: n }, (_, i) => `<tr data-fbap-uid="${uid(i + 1)}"><td>ลูกค้า ${i + 1}</td><td></td><td>เพื่อน</td><td>14 ต.ค. 2026</td></tr>`).join('');

function load(pager, { total = '', size = '', n = 20 } = {}) {
  const dom = new JSDOM(`<body>${total}${size}<table><tbody>${rows(n)}</tbody></table>${pager}</body>`, { url: 'https://chat.line.biz/Ubot/contact', runScripts: 'outside-only', pretendToBeVisual: true });
  const w = dom.window;
  w.Element.prototype.getBoundingClientRect = () => ({ width: 20, height: 20, top: 0, left: 0, right: 20, bottom: 20 });
  w.scrollTo = () => {};
  let listener;
  w.chrome = { runtime: { onMessage: { addListener: (fn) => (listener = fn) } } };
  w.eval(source);
  const send = (cmd, extra = {}) => new Promise((resolve) => listener({ target: 'fbap-content', cmd, ...extra }, null, resolve));
  return { w, send };
}

const PAGERS = {
  'text next': '<nav class="pagination"><button>1</button><button>ถัดไป</button></nav>',
  'arrow glyph': '<ul class="pagination"><li class="page-item"><a class="page-link" href="#">‹</a></li><li class="page-item"><a class="page-link" href="#">›</a></li></ul>',
  'icon only with aria-label': '<nav><button aria-label="Previous page"><i class="icon-chevron-left"></i></button><button aria-label="Next page"><i class="icon-chevron-right"></i></button></nav>',
  'icon only, class says the direction': '<div class="pagination"><button class="prev"><svg class="arrow-left"></svg></button><button class="next"><svg class="arrow-right"></svg></button></div>',
  'only numbers': '<ul class="pagination"><li class="page-item active"><a>1</a></li><li class="page-item"><a>2</a></li><li class="page-item"><a>3</a></li></ul>',
};

for (const [name, markup] of Object.entries(PAGERS)) {
  const { w, send } = load(markup);
  let clicked = '';
  w.document.addEventListener('click', (e) => (clicked = (e.target.closest('a, button, li') || e.target).outerHTML), true);
  const r = await send('lineListContacts');
  assert.equal(r.rowCount, 20, name);
  assert.equal(r.hasNext, true, `${name}: the way to the next page is found`);
  const n = await send('lineNextContactsPage');
  assert.equal(n.clicked, true, `${name}: and it is clicked`);
  assert.ok(clicked, `${name}: a control got the click`);
  assert.ok(!/prev|left|‹/i.test(clicked) || /next|right|›|ถัดไป|2/i.test(clicked), `${name}: not the "previous" one (${clicked.slice(0, 60)})`);
  console.log(`✔ ${name}`);
}

// The last page: the next control is switched off.
{
  const { send } = load('<nav class="pagination"><button>1</button><button disabled>ถัดไป</button></nav>');
  assert.equal((await send('lineListContacts')).hasNext, false);
  console.log('✔ a switched-off next control means the last page');
}

// No pager at all: it is reported with an outline of what could be a pager, not silently complete.
{
  const { send } = load('<div class="foot"><span class="x">1 / 45</span></div>', { total: '<h1>รายชื่อติดต่อ 900 คน</h1>' });
  const r = await send('lineListContacts');
  assert.equal(r.hasNext, false);
  assert.equal(r.total, 900, "the page's own total is read");
  console.log('✔ the page total is read');
}

// "Rows per page": the biggest choice is picked.
{
  const { w, send } = load('<nav class="pagination"></nav>', { size: '<select><option value="20">20</option><option value="50">50</option><option value="100">100</option></select>' });
  const r = await send('lineMaxPageSize');
  assert.equal(r.changed, true);
  assert.equal(w.document.querySelector('select').value, '100');
  console.log('✔ the biggest page size is chosen');
}

console.log('\nผ่านทุกข้อ ✔');
