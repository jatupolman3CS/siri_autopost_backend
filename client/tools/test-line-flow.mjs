// End-to-end test of the LINE Official Account channel: the real background.js and lib/line-flow.js (chrome.* and the
// chat.line.biz page faked, see cloud-harness.mjs; line.js itself is checked on its own in the first part) against a running
// SIRIAUTOPOST.Api, with the web app's flow: chat roster -> link set of chats -> schedule -> claim -> send -> result.
//   node tools/test-line-flow.mjs [apiUrl]          (default http://localhost:5100)
// Signs up a throwaway user on that API; it stays there with its workspace. NOTHING here touches real LINE or Facebook.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

import { lineChatTarget } from '../lib/shared.js';
import {
  API, ADMIN, data, local, page, linePage, lineUser, LINE_BOT, grantPermission, bg, api, setToken, step, sleep, background,
} from './cloud-harness.mjs';

const { tuning } = background;
tuning.lineSyncMinGapMs = 0; // the chat list may be read again at once (the real floor is 10 minutes)
tuning.loginProbeMs = 300; // look again for a login after a fraction of a second

// ---------- line.js on its own: time labels, and nothing a customer wrote leaves the page ----------

step('line.js: the address pattern, the time labels, and the state it reports carries no message text');
assert.deepEqual(lineChatTarget('chat.line.biz/BOT12/chat/USER123456/?x=1#y'), { botId: 'BOT12', userId: 'USER123456', url: 'https://chat.line.biz/BOT12/chat/USER123456' });
assert.equal(lineChatTarget('https://manager.line.biz/BOT12/chat/USER123456'), null);

const lineJsSource = fs.readFileSync(new URL('../line.js', import.meta.url), 'utf8');
function loadLineJs() {
  let listener = null;
  const sandbox = {
    document: { querySelectorAll: () => [], querySelector: () => null, createTreeWalker: () => ({ nextNode: () => null }), body: {}, title: '', activeElement: null, hidden: false },
    location: { hostname: 'chat.line.biz', pathname: '/', href: 'https://chat.line.biz/' },
    chrome: { runtime: { onMessage: { addListener: (fn) => { listener = fn; } } }, storage: { local: { get: async () => ({}) } } },
    NodeFilter: { SHOW_TEXT: 4, FILTER_ACCEPT: 1, FILTER_REJECT: 2 },
    getComputedStyle: () => ({}),
    setTimeout, clearTimeout, console, Date, Math, Promise, JSON, URL, String, Array, Object, Set, Map, RegExp, Number, parseFloat, Error,
  };
  sandbox.window = sandbox;
  vm.runInNewContext(lineJsSource, sandbox, { filename: 'line.js' });
  return (msg) => new Promise((resolve) => listener({ target: 'fbap-content', ...msg }, {}, resolve));
}
const lineJs = loadLineJs();
const NOW = new Date(2026, 9, 24, 15, 0, 0); // Saturday 24 Oct 2026, 15:00 local
const parse = async (label) => (await lineJs({ cmd: 'lineParseTime', label, now: NOW.toISOString() })).iso;
assert.equal(await parse('11.34 น.'), new Date(2026, 9, 24, 11, 34).toISOString());
assert.equal(await parse('23.59 น.'), NOW.toISOString(), 'a time later than now is not in the future');
assert.equal(await parse('เมื่อวาน'), new Date(2026, 9, 23).toISOString());
assert.equal(await parse('จันทร์'), new Date(2026, 9, 19).toISOString(), 'the latest such weekday');
assert.equal(await parse('พฤ.'), new Date(2026, 9, 22).toISOString());
assert.equal(await parse('12 ต.ค.'), new Date(2026, 9, 12).toISOString());
assert.equal(await parse('12/10/2569'), new Date(2026, 9, 12).toISOString(), 'a Buddhist-era year');
assert.equal(await parse('5 พ.ย.'), new Date(2025, 10, 5).toISOString(), 'a date without a year is never in the future');
assert.equal(await parse('ไม่รู้เรื่อง'), null);
assert.equal((await lineJs({ cmd: 'lineParseTime', label: '11.34 น.' })).hhmm, '11:34');

const STATE_KEYS = new Set([
  'ok', 'open', 'userId', 'headerName', 'composerFound', 'composerReady', 'composerEmpty', 'composerDisabled', 'overlay', 'manualButton',
  'autoReplyBar', 'cannotSend', 'bubbleCount', 'lastBubbleSide', 'lastBubbleTime', 'lastBubbleToday', 'unreadInList', 'outgoingCount',
  'incomingCount', 'sendEnabled', 'uploading', 'uploadDialog', 'sendFailed', 'failedCount', 'blocked',
]);
const state = await lineJs({ cmd: 'lineChatState' });
for (const [k, v] of Object.entries(state)) {
  assert.ok(STATE_KEYS.has(k), `lineChatState returns an unexpected key: ${k}`);
  assert.ok(v === null || ['boolean', 'number'].includes(typeof v) || (typeof v === 'string' && v.length <= 120), `${k} is not a flag, a count or a short string`);
}
// The only strings that come back are the customer's NAME (header, list rows) and the newest bubble's time of day. A result
// that carries `text` exists only in getText (what was typed into the box) and blockCheck (a dialog's warning).
const textKeys = lineJsSource.split('\n').filter((l) => /\btext:\s/.test(l) && !/^\s*(\/\/|\*)/.test(l));
for (const l of textKeys) assert.ok(/valueOf\(ed\)|text: ''|text\.slice|body\.slice|text: 'captcha'/.test(l), `a new "text:" in line.js: ${l.trim()}`);
// Enter is never pressed to send: the only Enter key event is the Shift+Enter new line of a contenteditable box.
for (const l of lineJsSource.split('\n').filter((x) => /pressKey\([^)]*'Enter'/.test(x))) assert.ok(/shiftKey: true/.test(l), `plain Enter in line.js: ${l.trim()}`);
// No screenshot of a LINE window may exist: the flow gets no captureWorker and no code here asks for a picture.
const flowSource = fs.readFileSync(new URL('../lib/line-flow.js', import.meta.url), 'utf8');
assert.ok(!/captureVisibleTab|captureWorker\(|shot:\s*await/.test(flowSource + lineJsSource), 'no screenshot code in the LINE files');
const bgSource = fs.readFileSync(new URL('../background.js', import.meta.url), 'utf8');
const wiring = bgSource.slice(bgSource.indexOf('createLineFlow({'), bgSource.indexOf('// Page work of the LINE flow'));
assert.ok(!/captureWorker\s*[,}:]/.test(wiring), 'captureWorker is not handed to the LINE flow');

// ---------- the web app ----------

step('owner signs up (granted Pro: chats are a Pro feature), the extension is paired, typing is on');
const email = `line${Date.now()}@test.co`;
let r = await api('POST', '/api/auth/signup', { email, password: 'password123' });
assert.equal(r.status, 200, r.text);
const ownerToken = r.json.token;
const ownerId = r.json.user.id;
r = await api('POST', '/api/auth/login', ADMIN);
assert.equal(r.status, 200, `admin login: ${r.text}`);
setToken(r.json.token);
r = await api('PUT', `/api/admin/customers/${ownerId}/plan`, { plan: 'pro' });
assert.equal(r.status, 200, r.text);
setToken(ownerToken);
const ws = (await api('GET', '/api/workspaces')).json[0];
assert.equal(ws.lineChat, true, 'the plan has chats');
const devBase = `/api/workspaces/${ws.id}/devices`;
const code = (await api('POST', `${devBase}/pairing`)).json.code;
r = await bg('cloudPair', { apiUrl: API, code, name: 'คอมทดสอบ LINE' });
assert.equal(r.ok, true, r.error);
const device = (await api('GET', devBase)).json[0];
const engine = (await api('GET', `/api/workspaces/${ws.id}/engine`)).json;
r = await api('PUT', `/api/workspaces/${ws.id}/engine/anti-ban`, {
  ...engine.antiBan, min: 1, max: 2, typing: true, scroll: false,
  limits: { ...engine.antiBan.limits, line: 200 },
  lineChat: { minGapSeconds: 1, maxGapSeconds: 2 },
  // The test makes jobs fail on purpose: the engine's own 'stop at N % failures' must not halt it half way.
  advanced: { ...engine.antiBan.advanced, minGap: 0, stopFailPct: 0 },
});
assert.equal(r.status, 200, r.text);

const accountsOf = async () => (await api('GET', `/api/workspaces/${ws.id}/accounts`)).json;
const fbAccount = () => accountsOf().then((a) => a.find((x) => x.platform === 'fb'));
const lineAccount = () => accountsOf().then((a) => a.find((x) => x.platform === 'line'));
assert.equal((await accountsOf()).filter((a) => a.platform === 'line').length, 0, 'no LINE account before LINE is switched on');

step('LINE is switched on from the status page: permission first, then the login probe; the web learns the OA');
let enable = await bg('lineEnable');
assert.equal(enable.ok, false);
assert.match(enable.error, /ยังไม่ได้อนุญาต/, 'refused without the host permission');
grantPermission('https://chat.line.biz/*');
linePage.loggedIn = false;
enable = await bg('lineEnable');
assert.equal(enable.ok, false);
assert.equal(enable.login, true, 'not logged in to LINE: the person is asked');
assert.ok(data.get('cloud').lineLoginLostAt > 0);
linePage.loggedIn = true;
enable = await bg('lineProbe');
assert.equal(enable.ok, true, enable.error);
assert.equal(enable.botId, LINE_BOT);
assert.equal(data.get('cloud').lineLoginLostAt, 0);
const line = await lineAccount();
assert.ok(line, 'the web created the LINE account from the heartbeat');
assert.equal(line.health, 'ok');
assert.equal((await fbAccount()).health, 'ok');
assert.match(line.name, /^LINE · /);

const CHATS = [
  'คุณสมชาย ใจดี', 'คุณสมหญิง รักสวย', 'คุณมานี มีตา', // the first schedule
  'คุณห้ามส่ง', // doNotSend
  'คุณกำลังคุย', 'คุณมีข้อความใหม่', 'คุณปิดแชท', 'คุณชื่อไม่ตรง', 'คุณนอกเวลา', 'คุณซ่อนในรายชื่อ', 'คุณไม่อยู่ในรายชื่อ', 'คุณล็อกอินหลุด', 'คุณแชทแบบแมนนวล', 'คุณโดนเตือน',
  'คุณหลายฟอง', 'คุณทดสอบแชท', 'คุณหลังเฟซบุ๊กหลุด', 'คุณอ่านหน้าไม่ออก',
];
CHATS.forEach((name, i) => linePage.chats.push({ userId: lineUser(i + 1), name, labels: i < 3 ? ['VIP'] : [], lastMessageAt: new Date(Date.now() - (i + 1) * 3600000).toISOString(), unread: i === 5 }));
const U = (name) => lineUser(CHATS.indexOf(name) + 1);

step('the chat list is read and sent to the web (names and labels, no messages)');
let sync = await bg('lineSyncChats');
assert.equal(sync.ok, true, sync.error);
assert.equal(sync.count, CHATS.length);
let dev = (await api('GET', devBase)).json[0];
assert.equal(dev.line.ready, true);
assert.equal(dev.line.chatCount, CHATS.length);
assert.ok(dev.line.syncedAt);
r = await api('GET', `/api/workspaces/${ws.id}/line/chats?accountId=${line.id}&pageSize=100`);
assert.equal(r.status, 200, r.text);
const roster = new Map(r.json.items.map((c) => [c.name, c]));
assert.equal(roster.size, CHATS.length);
assert.deepEqual(roster.get('คุณสมชาย ใจดี').labels, ['VIP']);
assert.equal(roster.get('คุณสมชาย ใจดี').url, `https://chat.line.biz/${LINE_BOT}/chat/${U('คุณสมชาย ใจดี')}`);
assert.equal(roster.get('คุณมีข้อความใหม่').unread, false, 'the contacts page carries no unread flag: it is looked at live before a chat is opened');
const cloudAfterSync = data.get('cloud');
assert.equal(cloudAfterSync.lineChats, CHATS.length);
assert.ok(cloudAfterSync.lineSyncedAt > 0);

step('a chat marked "do not send" cannot be imported into a set');
r = await api('PUT', `/api/workspaces/${ws.id}/line/chats/${roster.get('คุณห้ามส่ง').id}`, { doNotSend: true });
assert.equal(r.status, 200, r.text);
let probeSet = (await api('POST', `/api/workspaces/${ws.id}/link-sets`, { name: 'ชุดห้ามส่ง', channel: 'line' })).json;
r = await api('POST', `/api/workspaces/${ws.id}/link-sets/${probeSet.id}/links/from-line-chats`, { accountId: line.id, chatIds: [roster.get('คุณห้ามส่ง').id] });
assert.equal(r.status, 200, r.text);
assert.equal(r.json.added, 0);
assert.equal(r.json.skippedDoNotSend, 1);

// A collection with one post: the greeting has the customer's name in it, and a picture.
step('a collection (text with {{name}}, one picture) and a LINE link set of three customers');
r = await api('POST', `/api/workspaces/${ws.id}/collections`, { name: 'ชุดแชท' });
const collection = r.json;
const form = new FormData();
form.append('file', new Blob([Buffer.from([0xff, 0xd8, 0xff, 0xe0, 1, 2, 3])], { type: 'image/jpeg' }), 'poster.jpg');
r = await api('POST', `/api/workspaces/${ws.id}/media`, undefined, { form });
assert.equal(r.status, 200, r.text);
const image = r.json;
r = await api('POST', `/api/workspaces/${ws.id}/collections/${collection.id}/posts`, { text: 'สวัสดีครับ {{name}} วันนี้มีโปรโมชั่นพิเศษสำหรับคุณ', mediaIds: [image.id] });
assert.equal(r.status, 200, r.text);

const UTC_OFFSET = -new Date().getTimezoneOffset();
const ALL_DAY = { allowedFrom: '00:00', allowedTo: '23:59', skipIfActiveMinutes: 30, splitBubbles: false, imagesFirst: false };

// A LINE link set made of the named roster customers, and a schedule that sends to each of them once, now.
async function queueChats(setName, names, { lineOptions = ALL_DAY, collectionId = collection.id } = {}) {
  r = await api('POST', `/api/workspaces/${ws.id}/link-sets`, { name: setName, channel: 'line' });
  assert.equal(r.status, 200, r.text);
  const set = r.json;
  assert.equal(set.channel, 'line');
  r = await api('POST', `/api/workspaces/${ws.id}/link-sets/${set.id}/links/from-line-chats`, { accountId: line.id, chatIds: names.map((n) => roster.get(n).id) });
  assert.equal(r.status, 200, r.text);
  assert.equal(r.json.added, names.length);
  r = await api('POST', `/api/workspaces/${ws.id}/schedules`, {
    collectionId, linkSetId: set.id, mode: 'once', onceTime: '12:00', startNow: true, utcOffsetMinutes: UTC_OFFSET, order: 'shuffle', repeat: 'never', lineOptions,
  });
  assert.equal(r.status, 200, r.text);
  assert.ok(r.json.created >= 1, 'the opening round is queued');
  return { set, schedule: r.json.schedule, created: r.json.created };
}

const chatPosts = async () => {
  const from = new Date(Date.now() - 24 * 3600000).toISOString();
  const to = new Date(Date.now() + 48 * 3600000).toISOString();
  const res = await api('GET', `/api/workspaces/${ws.id}/posts?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
  assert.equal(res.status, 200, res.text);
  return res.json.filter((p) => p.platform === 'line').sort((a, b) => new Date(a.scheduledAt) - new Date(b.scheduledAt));
};
const postOf = async (name) => (await chatPosts()).find((p) => (p.targetUrl || '').endsWith(U(name)));
const sentTo = (name) => linePage.sent.filter((s) => s.userId === U(name));
const linkOf = async (setId, name) => (await api('GET', `/api/workspaces/${ws.id}/link-sets`)).json.find((s) => s.id === setId).links.find((l) => l.name === name);

// Runs the extension's rounds (a sync, then one claim) until the check passes; the server's 1-second gap makes one round per job.
async function until(check, what, ms = 90000) {
  const end = Date.now() + ms;
  for (;;) {
    await bg('cloudSync');
    const v = await check();
    if (v) return v;
    if (Date.now() > end) throw new Error(`timed out: ${what} (LINE state: ${JSON.stringify(Object.fromEntries(Object.entries(data.get('cloud')).filter(([k]) => k.startsWith('line'))))})`);
    await sleep(400);
  }
}

step('the schedule sends each customer the greeting with HIS name, typed, then the picture; results reach the web');
const first = await queueChats('ลูกค้าชุดแรก', ['คุณสมชาย ใจดี', 'คุณสมหญิง รักสวย', 'คุณมานี มีตา']);
assert.equal(first.created, 3);
const order = (await chatPosts()).map((p) => p.targetUrl.split('/').pop());
assert.equal(order.length, 3);
await until(async () => (await chatPosts()).filter((p) => p.status === 'success').length === 3, 'three chats sent');
const names = ['คุณสมชาย ใจดี', 'คุณสมหญิง รักสวย', 'คุณมานี มีตา'];
for (const n of names) {
  const sends = sentTo(n);
  assert.equal(sends.length, 2, `${n}: the text, then the picture`);
  assert.ok(sends[0].text.startsWith(`สวัสดีครับ ${n} วันนี้`), `${n}: ${sends[0].text}`);
  assert.deepEqual(sends[0].files, []);
  assert.equal(sends[1].text, '');
  assert.deepEqual(sends[1].files, ['poster'], 'the library picture was attached');
}
assert.deepEqual([...new Set(linePage.sent.map((s) => s.userId))], order, 'one chat after the other, in the order the web queued them');
assert.deepEqual(linePage.opened, order, 'every chat opened once, from the list');
assert.ok(linePage.inserts > 60, `the text is typed, not pasted (${linePage.inserts} keystrokes)`);
assert.equal(linePage.setTexts, 0, 'nothing was pasted');
assert.equal([...data.keys()].filter((k) => k.startsWith('cloudimg:')).length, 0, 'the media is removed after sending');
assert.equal(Object.keys(data.get('cloud')).filter((k) => /shot/i.test(k)).length, 0);
const done = (await chatPosts()).filter((p) => p.status === 'success');
assert.ok(done.every((p) => p.platform === 'line' && p.isTest === false));
assert.ok(done.every((p) => !p.postUrl), 'a chat has no post address');
for (const n of names) assert.equal((await linkOf(first.set.id, n)).health, 'ok');
assert.equal((await lineAccount()).health, 'ok');
assert.equal(data.get('cloud').lineLastJob.ok, true);
const live = (await api('GET', `${devBase}/${device.id}/live`)).json;
assert.ok(live.logs.some((l) => l.msg.includes('ส่งแชท LINE สำเร็จ')), 'the device log says it');
assert.ok(!live.logs.some((l) => l.msg.includes('Facebook')), 'no Facebook wording in a LINE job');

step('(a) a customer who wrote 5 minutes ago is not interrupted: the job moves later, nothing is sent');
const hhmm = (d) => `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
linePage.convo[U('คุณกำลังคุย')] = { side: 'in', time: hhmm(new Date(Date.now() - 5 * 60000)), today: true, unread: false };
const active = await queueChats('ลูกค้ากำลังคุย', ['คุณกำลังคุย']);
let p = await until(async () => {
  const x = await postOf('คุณกำลังคุย');
  return x && x.deferCount === 1 ? x : null;
}, 'the job is deferred');
assert.equal(p.status, 'queued');
assert.ok(new Date(p.scheduledAt) - Date.now() > 20 * 60000, `moved later: ${p.scheduledAt}`);
assert.equal(sentTo('คุณกำลังคุย').length, 0);
assert.equal((await linkOf(active.set.id, 'คุณกำลังคุย')).health, 'ok', 'a defer does not switch the link off');
assert.equal((await linkOf(active.set.id, 'คุณกำลังคุย')).failStreak, 0);

step('(a2) a customer with an UNREAD message is left alone before the chat is even opened');
linePage.convo[U('คุณมีข้อความใหม่')] = { side: 'in', time: null, today: false, unread: true };
await queueChats('ลูกค้ามีข้อความใหม่', ['คุณมีข้อความใหม่']);
p = await until(async () => {
  const x = await postOf('คุณมีข้อความใหม่');
  return x && x.deferCount === 1 ? x : null;
}, 'the unread chat is deferred');
assert.ok(!linePage.opened.includes(U('คุณมีข้อความใหม่')), 'opening it would have marked the message as read');
assert.equal(sentTo('คุณมีข้อความใหม่').length, 0);

step('(b) a chat whose box is dead is skipped for good: the link goes off, no failure counted');
linePage.composerDisabled = true;
const dead = await queueChats('ลูกค้าปิดแชท', ['คุณปิดแชท']);
p = await until(async () => {
  const x = await postOf('คุณปิดแชท');
  return x && x.status === 'skipped' ? x : null;
}, 'the dead chat is skipped');
linePage.composerDisabled = false;
assert.match(p.failureDetail, /ส่งข้อความในแชทนี้ไม่ได้/);
const deadLink = await linkOf(dead.set.id, 'คุณปิดแชท');
assert.equal(deadLink.health, 'off');
assert.equal(deadLink.enabled, false);
assert.equal(deadLink.failStreak, 0);
assert.equal(sentTo('คุณปิดแชท').length, 0);
assert.equal((await lineAccount()).health, 'ok');

step('(c) the open chat is not the customer of the job: nothing is typed or sent, the job fails');
linePage.headerName = 'คนอื่นโดยสิ้นเชิง';
const wrongBefore = linePage.inserts;
await queueChats('ลูกค้าชื่อไม่ตรง', ['คุณชื่อไม่ตรง']);
p = await until(async () => {
  const x = await postOf('คุณชื่อไม่ตรง');
  return x && x.status === 'failed' ? x : null;
}, 'the wrong chat fails');
linePage.headerName = null;
assert.match(p.failureDetail, /แชทที่เปิดไม่ตรงกับลูกค้า/);
assert.equal(sentTo('คุณชื่อไม่ตรง').length, 0);
assert.equal(linePage.inserts, wrongBefore, 'not a single character was typed');

step('(c1) a page whose bubbles cannot be read at all: "is the customer talking?" is unanswerable, so nothing is sent');
linePage.convo[U('คุณอ่านหน้าไม่ออก')] = { side: null };
const blindBefore = linePage.inserts;
await queueChats('ลูกค้าอ่านไม่ออก', ['คุณอ่านหน้าไม่ออก']);
p = await until(async () => {
  const x = await postOf('คุณอ่านหน้าไม่ออก');
  return x && x.status === 'failed' ? x : null;
}, 'the blind page fails');
assert.match(p.failureDetail, /ไม่พบข้อความในแชทนี้/);
assert.equal(sentTo('คุณอ่านหน้าไม่ออก').length, 0);
assert.equal(linePage.inserts, blindBefore, 'not a single character was typed');

step('(c2) a chat that is not in the list is found by its name in the search box, and the box is cleared again');
roster.get('คุณซ่อนในรายชื่อ');
linePage.chats.find((c) => c.name === 'คุณซ่อนในรายชื่อ').hidden = true;
await queueChats('ลูกค้าซ่อน', ['คุณซ่อนในรายชื่อ']);
await until(async () => (await postOf('คุณซ่อนในรายชื่อ'))?.status === 'success', 'the hidden chat is sent');
assert.equal(sentTo('คุณซ่อนในรายชื่อ').length, 2);
assert.equal(linePage.search, '', 'the customer name is not left in the search box');
assert.equal(linePage.target, 'composer');

step('(c3) a chat that even a search cannot find is opened by its address');
const lost = linePage.chats.find((c) => c.name === 'คุณไม่อยู่ในรายชื่อ');
lost.hidden = true;
lost.noSearch = true;
await queueChats('ลูกค้าไม่อยู่ในรายชื่อ', ['คุณไม่อยู่ในรายชื่อ']);
await until(async () => (await postOf('คุณไม่อยู่ในรายชื่อ'))?.status === 'success', 'the chat is opened from its address');
assert.equal(sentTo('คุณไม่อยู่ในรายชื่อ').length, 2);

step('(h) the OA\'s auto-reply covers the box: the chat is switched to manual first, then the text goes');
linePage.autoReply = true;
await queueChats('ลูกค้าแชทแมนนวล', ['คุณแชทแบบแมนนวล']);
await until(async () => (await postOf('คุณแชทแบบแมนนวล'))?.status === 'success', 'the manual chat is sent');
linePage.autoReply = false;
assert.deepEqual(linePage.switched, [U('คุณแชทแบบแมนนวล')]);
assert.equal(sentTo('คุณแชทแบบแมนนวล').length, 2);

step('(e) outside the allowed hours the web hands out nothing');
const h = new Date().getHours();
const at = (n) => `${String((h + n) % 24).padStart(2, '0')}:00`;
const sentBefore = linePage.sent.length;
const quiet = await queueChats('ลูกค้านอกเวลา', ['คุณนอกเวลา'], { lineOptions: { ...ALL_DAY, allowedFrom: at(2), allowedTo: at(3) } });
for (let i = 0; i < 6; i++) {
  await bg('cloudSync');
  await sleep(700);
}
p = await postOf('คุณนอกเวลา');
assert.equal(p.status, 'queued');
assert.ok(new Date(p.scheduledAt) > new Date(), `the post waits for its window: ${p.scheduledAt}`);
assert.equal(linePage.sent.length, sentBefore, 'nothing was sent');
assert.equal(sentTo('คุณนอกเวลา').length, 0);

step('(d) LINE asks for a login: that job fails as "login", the LINE account waits, Facebook stays healthy; then it heals by itself');
linePage.loggedIn = false;
await queueChats('ลูกค้าล็อกอินหลุด', ['คุณล็อกอินหลุด']);
p = await until(async () => {
  const x = await postOf('คุณล็อกอินหลุด');
  return x && x.status === 'failed' ? x : null;
}, 'the login failure');
assert.equal(p.failureCode, 'session');
assert.equal((await lineAccount()).health, 'relogin');
assert.equal((await fbAccount()).health, 'ok', 'a LINE login failure never touches the Facebook account');
assert.ok(data.get('cloud').lineLoginLostAt > 0);
assert.equal(data.get('cloud').loginLostAt, 0, 'the Facebook note is untouched');
dev = (await api('GET', devBase)).json[0];
assert.equal(dev.line.sessionLost, true, 'the web shows that LINE waits for a login');
await bg('cloudSync'); // the next heartbeat says LINE is not ready
dev = (await api('GET', devBase)).json[0];
assert.equal(dev.line.ready, false, 'the web is told LINE is not ready');
linePage.loggedIn = true;
await until(async () => (await lineAccount()).health === 'ok' && data.get('cloud').lineLoginLostAt === 0, 'the LINE login heals');
assert.equal((await fbAccount()).health, 'ok');

step('(i) "split bubbles" and "pictures first": the pictures go before the text, the text goes paragraph by paragraph');
r = await api('POST', `/api/workspaces/${ws.id}/collections`, { name: 'ชุดหลายฟอง' });
const splitCollection = r.json;
r = await api('POST', `/api/workspaces/${ws.id}/collections/${splitCollection.id}/posts`, {
  text: 'ย่อหน้าแรกของ {{name}}\n\nย่อหน้าที่สอง\n\nย่อหน้าที่สาม', mediaIds: [image.id],
});
assert.equal(r.status, 200, r.text);
await queueChats('ลูกค้าหลายฟอง', ['คุณหลายฟอง'], { collectionId: splitCollection.id, lineOptions: { ...ALL_DAY, splitBubbles: true, imagesFirst: true } });
await until(async () => (await postOf('คุณหลายฟอง'))?.status === 'success', 'the split message is sent');
assert.deepEqual(
  sentTo('คุณหลายฟอง').map((s) => (s.files.length ? `[${s.files}]` : s.text)),
  ['[poster]', 'ย่อหน้าแรกของ คุณหลายฟอง', 'ย่อหน้าที่สอง', 'ย่อหน้าที่สาม'],
);

step('(j) a test post to one of the owner\'s own chats (a roster chat in no set): same job path, no schedule options');
r = await api('POST', `/api/workspaces/${ws.id}/test-post`, {
  chatId: roster.get('คุณทดสอบแชท').id, collectionId: collection.id, text: 'ทดสอบถึง {{name}}', deviceId: device.id,
});
assert.equal(r.status, 200, r.text);
assert.equal(r.json.isTest, true);
await until(async () => (await postOf('คุณทดสอบแชท'))?.status === 'success', 'the test chat is sent');
assert.ok(sentTo('คุณทดสอบแชท')[0].text.includes('คุณทดสอบแชท'), sentTo('คุณทดสอบแชท')[0].text);

step('(k) Facebook asks for a login: LINE jobs are still claimed and sent meanwhile (it is not starved behind Facebook)');
r = await api('POST', `/api/workspaces/${ws.id}/link-sets`, { name: 'ชุดเฟซบุ๊ก', channel: 'fb' });
assert.equal(r.status, 200, r.text);
const fbSet = r.json;
r = await api('POST', `/api/workspaces/${ws.id}/link-sets/${fbSet.id}/links`, { name: 'กลุ่มเฟซบุ๊ก', url: 'https://www.facebook.com/groups/fbwhilelinewaits', code: 'F1' });
assert.equal(r.status, 200, r.text);
page.loggedIn = false;
r = await api('POST', `/api/workspaces/${ws.id}/schedules`, {
  collectionId: collection.id, linkSetId: fbSet.id, mode: 'once', onceTime: '12:00', startNow: true, utcOffsetMinutes: UTC_OFFSET, order: 'shuffle', repeat: 'never',
});
assert.equal(r.status, 200, r.text);
await until(async () => (await fbAccount()).health === 'relogin', 'Facebook waits for a login');
assert.ok(data.get('cloud').loginLostAt > 0);
assert.equal((await lineAccount()).health, 'ok');
await queueChats('ลูกค้าตอนเฟซบุ๊กหลุด', ['คุณหลังเฟซบุ๊กหลุด']);
await until(async () => (await postOf('คุณหลังเฟซบุ๊กหลุด'))?.status === 'success', 'LINE is sent while Facebook waits for a login');
assert.equal((await fbAccount()).health, 'relogin', 'Facebook still waits');
page.loggedIn = true;
await until(async () => (await fbAccount()).health === 'ok' && data.get('cloud').loginLostAt === 0, 'Facebook heals by itself');

step('(m) the web\'s "sync LINE chats" button, the daily read and the 10-minute floor');
linePage.chats.push({ userId: lineUser(900), name: 'คุณจากปุ่มบนเว็บ', labels: [], lastMessageAt: new Date().toISOString(), unread: false });
const countNow = async () => (await api('GET', devBase)).json[0].line.chatCount;
// A complete read removes the chats the list no longer shows (the two hidden ones above) and adds the new one.
const visibleChats = () => linePage.chats.filter((c) => !c.hidden).length;
r = await api('POST', `${devBase}/${device.id}/commands`, { cmd: 'syncLineChats' });
assert.equal(r.status, 200, r.text);
const syncCmd = r.json;
let cmdDone;
for (let i = 0; i < 40; i++) {
  await bg('cloudConfig', { mode: 'auto' });
  cmdDone = (await api('GET', `${devBase}/${device.id}/commands/${syncCmd.id}`)).json;
  if (cmdDone.status === 'done') break;
  await sleep(250);
}
assert.equal(cmdDone.status, 'done');
assert.equal(cmdDone.result.ok, true, 'the command is answered at once');
await until(async () => (await countNow()) === visibleChats(), 'the button read the list');
assert.ok(linePage.contactReads > 0, 'the customers were read from the contacts page');
assert.equal(linePage.chatListReads, 0, 'the chat list was not scrolled when the contacts page worked');
tuning.lineSyncMinGapMs = 10 * 60000;
const throttled = await bg('lineSyncChats');
assert.equal(throttled.ok, false);
assert.equal(throttled.throttled, true, 'one read per 10 minutes');
tuning.lineSyncMinGapMs = 0;
linePage.chats.push({ userId: lineUser(901), name: 'คุณจากรอบ 6 ชั่วโมง', labels: [], lastMessageAt: null, unread: false });
await local.set({ cloud: { ...data.get('cloud'), lineSyncNextAt: 1 } }); // the next automatic read is due
await until(async () => (await countNow()) === visibleChats(), 'the automatic read');
assert.ok(data.get('cloud').lineSyncNextAt > Date.now() + 20 * 3600000, 'the next one is about a day away');

step('(m2) when the contacts page shows no rows, the chat list is read instead');
linePage.contactsOff = true;
const listReadsBefore = linePage.chatListReads;
const fallback = await bg('lineSyncChats');
assert.equal(fallback.ok, true, fallback.error);
assert.ok(linePage.chatListReads > listReadsBefore, 'the chat list was scrolled');
linePage.contactsOff = false;

step('(m3) the web pastes an OA link: the browser takes that OA and reads its customers right away');
const contactReadsBefore = linePage.contactReads;
async function webCommand(cmd, args) {
  r = await api('POST', `${devBase}/${device.id}/commands`, { cmd, args });
  assert.equal(r.status, 200, r.text);
  let done;
  for (let i = 0; i < 40; i++) {
    await bg('cloudConfig', { mode: 'auto' });
    done = (await api('GET', `${devBase}/${device.id}/commands/${r.json.id}`)).json;
    if (done.status === 'done') break;
    await sleep(250);
  }
  assert.equal(done.status, 'done', `${cmd} was answered`);
  return done.result;
}
const bad = await webCommand('connectLine', { link: 'https://example.com/nothing' });
assert.equal(bad.ok, false, 'an address that is not a LINE OA is refused');
const good = await webCommand('connectLine', { link: `https://chat.line.biz/${LINE_BOT}/contact` });
assert.equal(good.ok, true, good.error);
assert.equal(data.get('cloud').lineBotId, LINE_BOT);
assert.equal(data.get('cloud').lineEnabled, true);
await until(async () => linePage.contactReads > contactReadsBefore, 'the customers were read from the contacts page');

step('(l) several OAs on the account chooser: the person picks one on the status page');
linePage.chooser = true;
linePage.accounts = [{ botId: 'Ua0000000000000000000000000000001', name: 'ร้านอื่น' }, { botId: LINE_BOT, name: linePage.oaName }];
await local.set({ cloud: { ...data.get('cloud'), lineBotId: '', lineOaName: '' } });
let choice = await bg('lineProbe');
assert.equal(choice.ok, true, choice.error);
assert.equal(choice.needsChoice, true);
assert.equal(data.get('cloud').lineChoices.length, 2);
choice = await bg('lineChoose', { botId: 'Unknown' });
assert.equal(choice.ok, false);
choice = await bg('lineChoose', { botId: LINE_BOT });
assert.equal(choice.ok, true, choice.error);
assert.equal(data.get('cloud').lineBotId, LINE_BOT);
assert.deepEqual(data.get('cloud').lineChoices, []);
linePage.chooser = false;

step('(g) a LINE warning page rests the whole device; the extension sends nothing more and no picture is taken');
linePage.blocked = true;
const sentBeforeBlock = linePage.sent.length;
await queueChats('ลูกค้าโดนเตือน', ['คุณโดนเตือน']);
p = await until(async () => {
  const x = await postOf('คุณโดนเตือน');
  return x && x.status === 'failed' ? x : null;
}, 'the blocked job fails');
linePage.blocked = false;
assert.equal(p.failureCode, 'rate_limit');
assert.equal(linePage.sent.length, sentBeforeBlock);
dev = (await api('GET', devBase)).json[0];
assert.ok(dev.autoPausedUntil, 'the device rests');
assert.match(dev.autoPauseReason, /LINE/);
assert.ok(data.get('cloud').pausedUntil > Date.now());
const lastResultHadNoPicture = (await api('GET', `${devBase}/${device.id}/live`)).json.logs.some((l) => /จับภาพ|screenshot/i.test(l.msg));
assert.equal(lastResultHadNoPicture, false);

console.log('\nผ่านทุกข้อ ✔');
process.exit(0);
