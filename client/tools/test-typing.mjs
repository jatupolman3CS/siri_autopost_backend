// Does the extension type a job's text like a person? Runs the real background.js (chrome.* and the Facebook page
// faked, see cloud-harness.mjs) against a fake web API, so it needs no server and no database:
//   node tools/test-typing.mjs
// A post from the web is typed character by character however long it is (a long one at a quicker pace, never
// pasted); only the web's "typing off" switch turns it into a paste. The waits the extension asks for are recorded
// (and then shortened), so the test also knows how long a person would have needed to type.
import assert from 'node:assert/strict';

import { local, bg, page, step, realSetTimeout } from './cloud-harness.mjs';

// ---------- what the extension did ----------

const waits = []; // the waits (ms) typeHuman asked for: the pauses between keystrokes (other timers of the extension are not counted)
const harnessSetTimeout = globalThis.setTimeout;
globalThis.setTimeout = (fn, ms, ...a) => {
  const stack = new Error().stack;
  if ((ms ?? 0) < 100000 && stack.includes('typeHuman') && /at sleep/.test(stack)) waits.push(ms ?? 0); // not the message time-outs of tabCmd
  return harnessSetTimeout(fn, Math.min(ms ?? 0, 2), ...a);
};

const cmds = {}; // content.js commands by name
const sendMessage = chrome.tabs.sendMessage;
chrome.tabs.sendMessage = async (tabId, msg) => {
  cmds[msg.cmd] = (cmds[msg.cmd] || 0) + 1;
  return sendMessage(tabId, msg);
};

// ---------- a fake web API ----------

const jobs = [];
const results = [];
globalThis.fetch = async (url, init = {}) => {
  const path = new URL(url).pathname;
  const json = (body, status = 200) => ({ ok: status < 300, status, json: async () => body, blob: async () => new Blob([]) });
  if (path === '/api/device/heartbeat') return json({ deviceName: 'Test PC', workspaceName: 'Test', jobsPaused: false, online: true, groups: 0 });
  if (path === '/api/device/groups') return json(0);
  if (path === '/api/device/jobs/claim') return jobs.length ? json(jobs.shift()) : json(null, 204);
  const result = path.match(/^\/api\/device\/jobs\/(.+)\/result$/);
  if (result) {
    results.push({ id: result[1], ...JSON.parse(init.body) });
    return json({});
  }
  return json({});
};
await local.set({ cloud: { enabled: true, apiUrl: 'http://fake.test', deviceKey: 'key', deviceId: 'd1' } });

let n = 0;
const graphemes = (s) => Array.from(new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(s)).length; // what the extension counts
const sentence = 'ห้องพักใหม่ใกล้รถไฟฟ้า เดินทางสะดวก ราคาเป็นกันเอง ทักแชทสอบถามได้เลยค่ะ ';
const text = (len) => Array.from(sentence.repeat(Math.ceil(len / sentence.length)).slice(0, len)).map((c, i) => (i % 70 === 69 ? '\n' : c)).join('');
const job = (content, antiBan) => ({
  postId: `post-${++n}`, groupName: 'กลุ่มทดสอบ', groupUrl: 'https://www.facebook.com/groups/plantlovers', content, media: [],
  antiBan: { typing: true, typingSpeed: 'normal', scroll: false, advanced: {}, ...antiBan },
  kind: 'post', targetKind: 'group', pageTags: '', shot: false,
});

// Posts one job through the extension; returns what it did.
async function run(j) {
  jobs.push(j);
  for (const k of Object.keys(cmds)) delete cmds[k];
  waits.length = 0;
  const before = results.length;
  page.posted.length = 0;
  // cloudSync: settings first, then one job (a post lasts minutes, so the answer comes when it is done)
  const taken = await bg('cloudSync');
  assert.equal(taken.ok, true, JSON.stringify(taken));
  for (let i = 0; i < 6000 && results.length === before; i++) await new Promise((r) => realSetTimeout(r, 10));
  assert.equal(results.length, before + 1, 'the extension reported the result');
  const logs = (await local.get('logs')).logs.map((l) => l.msg);
  return { result: results.at(-1), cmds: { ...cmds }, seconds: waits.reduce((a, b) => a + b, 0) / 1000, logs, posted: page.posted.map((p) => p.text) };
}

// ---------- tests ----------

step('a post longer than the old 600-character limit is typed, not pasted');
const long = text(900);
const longLen = graphemes(long);
assert.ok(longLen > 600, `the sample is longer than the old limit (${longLen} characters)`);
let r = await run(job(long));
assert.equal(r.result.ok, true, JSON.stringify(r.result));
assert.deepEqual(r.posted, [long], 'the text that went out is the text of the job');
assert.ok((r.cmds.insert || 0) > 150, `typed in many pieces (insert x${r.cmds.insert})`);
assert.ok(!r.cmds.setText && !r.cmds.paste, `nothing is pasted (setText ${r.cmds.setText}, paste ${r.cmds.paste})`);
assert.ok(r.cmds.newline >= 10, 'line breaks are made one by one');
assert.ok(r.seconds > 90 && r.seconds < 400, `a person would need ${Math.round(r.seconds)} s`);
assert.ok(r.logs.some((m) => m.includes(`พิมพ์ข้อความ ${longLen} ตัวอักษรทีละตัว`)), 'the log says it typed');
console.log(`  ${Math.round(r.seconds)} s of simulated waiting, ${r.cmds.insert} insert commands`);

step('a very long post is typed at a quicker pace than the preset, still not pasted');
const huge = text(3200);
const slowPerChar = r.seconds / longLen;
r = await run(job(huge));
assert.equal(r.result.ok, true, JSON.stringify(r.result));
assert.deepEqual(r.posted, [huge]);
assert.ok(!r.cmds.setText && !r.cmds.paste, 'nothing is pasted');
const hugeLen = graphemes(huge);
assert.ok(r.seconds / hugeLen < slowPerChar * 0.85, `quicker per character (${(r.seconds / hugeLen).toFixed(3)} s against ${slowPerChar.toFixed(3)} s)`);
assert.ok(r.seconds > 200, `but still not instant (${Math.round(r.seconds)} s)`);

step('the speed the web app picked is used: fast is quicker than slow');
const slow = await run(job(text(300), { typingSpeed: 'slow' }));
const fast = await run(job(text(300), { typingSpeed: 'fast' }));
assert.ok(slow.seconds > fast.seconds * 1.5, `slow ${Math.round(slow.seconds)} s, fast ${Math.round(fast.seconds)} s`);

step('typing switched off in the web app is the one thing that pastes, and the log says why');
r = await run(job(long, { typing: false }));
assert.equal(r.result.ok, true);
assert.ok(r.cmds.setText >= 1 && !r.cmds.insert, 'pasted at once');
assert.ok(r.logs.some((m) => m.includes(`วางข้อความ ${longLen} ตัวอักษรทีเดียว`) && m.includes('จำลองการพิมพ์')));

step('a job that asks for a screenshot sends one with its result (and only then)');
chrome.permissions.contains = async () => true; // the capture permission granted when pairing
const PICTURE = 'data:image/jpeg;base64,/9j/4AAQ';
chrome.tabs.captureVisibleTab = async () => PICTURE;
r = await run({ ...job(text(120)), shot: true });
assert.equal(r.result.ok, true);
assert.equal(r.result.shot, PICTURE);
r = await run(job(text(120)));
assert.equal(r.result.shot, null, 'no picture when the web did not ask for one');

console.log('\nAll typing checks passed');
process.exit(0);
