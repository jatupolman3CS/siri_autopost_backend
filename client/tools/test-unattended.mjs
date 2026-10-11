// End-to-end test of the extension running ON ITS OWN (cloud mode), the way a shop uses it: nobody presses
// "take jobs" or "reconnect" on the web, the browser is restarted, Facebook logs the account out for a while, a
// browser is unbound and paired again, and a second extension sits in the same workspace. Runs the real background.js
// against a running SIRIAUTOPOST.Api (see cloud-harness.mjs); the "alarm" is fired by hand, as Chrome would every 30 s.
//   node tools/test-unattended.mjs [apiUrl]          (default http://localhost:5100)
// Signs up throwaway users on that API; they stay there with their workspaces.
import assert from 'node:assert/strict';

import { API, ADMIN, data, listeners, alarms, page, bg, api, setToken, step, sleep, background } from './cloud-harness.mjs';

// ONLY=3,5 runs just those steps (each step makes its own workspace).
const only = (process.env.ONLY || '').split(',').filter(Boolean);
const run = (n) => only.length === 0 || only.includes(String(n));

const tuning = background.tuning ?? {};
tuning.loginProbeMs = 300; // the login probe is a few minutes apart in real life

const fireAlarm = () => {
  for (const fn of listeners.alarm) fn({ name: 'fbap-cloud' });
};
const cloud = () => data.get('cloud') || {};

async function waitFor(label, cond, { ms = 90000, every = 600, alarm = true } = {}) {
  const end = Date.now() + ms;
  for (;;) {
    if (await cond()) return;
    if (Date.now() > end) throw new Error(`timed out waiting for: ${label}`);
    if (alarm) fireAlarm();
    await sleep(every);
  }
}

let seq = 0;
async function newWorkspace(tag) {
  const email = `${tag}${Date.now()}${seq++}@test.co`;
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
  const engine = (await api('GET', `/api/workspaces/${ws.id}/engine`)).json;
  r = await api('PUT', `/api/workspaces/${ws.id}/engine/anti-ban`, { ...engine.antiBan, min: 1, max: 2, typing: false, scroll: false });
  assert.equal(r.status, 200, r.text);
  r = await api('POST', `/api/workspaces/${ws.id}/collections`, { name: 'ชุดทดสอบ' });
  assert.equal(r.status, 200, r.text);
  const collection = r.json;
  r = await api('POST', `/api/workspaces/${ws.id}/collections/${collection.id}/posts`, { text: 'สวัสดีครับ {{code}}', mediaIds: [] });
  assert.equal(r.status, 200, r.text);
  return { ws, collection };
}

const devicesOf = async (ws) => (await api('GET', `/api/workspaces/${ws.id}/devices`)).json;

// The harness' extension: pairs through the same command the status page sends.
async function pairExtension(ws, name) {
  const code = (await api('POST', `/api/workspaces/${ws.id}/devices/pairing`)).json.code;
  const r = await bg('cloudPair', { apiUrl: API, code, name });
  assert.equal(r.ok, true, r.error);
  const device = (await devicesOf(ws)).find((d) => d.id === cloud().deviceId);
  assert.ok(device, 'the new device is in the workspace');
  return device;
}

// A second extension that is only a client of the device API (it never posts: it claims and reports what it got).
async function rawExtension(ws, name) {
  const code = (await api('POST', `/api/workspaces/${ws.id}/devices/pairing`)).json.code;
  const res = await fetch(API + '/api/device/pair', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ code, name, browser: 'Chrome 1', version: 'raw' }),
  });
  const body = await res.text();
  assert.equal(res.status, 200, body);
  const p = JSON.parse(body);
  const call = async (path, body) => {
    const r = await fetch(API + path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Device-Key': p.deviceKey }, body: JSON.stringify(body ?? {}) });
    return r.status === 204 ? null : r.json();
  };
  return {
    deviceId: p.deviceId,
    accountId: p.accountId,
    heartbeat: () => call('/api/device/heartbeat', { version: 'raw' }),
    // What this extension would be handed now (the job is then left unreported: the test only asks who gets it).
    claim: async () => {
      await call('/api/device/heartbeat', { version: 'raw' });
      return call('/api/device/jobs/claim');
    },
  };
}

// A set with one group and a schedule that starts now (its one post is due 2-20 s later).
async function startNow(ws, collection, setName, groupUrl, postAsAccountId = null) {
  let r = await api('POST', `/api/workspaces/${ws.id}/link-sets`, { name: setName, postAsAccountId });
  assert.equal(r.status, 200, r.text);
  const set = r.json;
  r = await api('POST', `/api/workspaces/${ws.id}/link-sets/${set.id}/links`, { name: `กลุ่ม ${setName}`, url: groupUrl, code: 'A1' });
  assert.equal(r.status, 200, r.text);
  r = await api('POST', `/api/workspaces/${ws.id}/schedules`, {
    collectionId: collection.id, linkSetId: set.id, mode: 'daily', times: ['09:00'], startNow: true, utcOffsetMinutes: 420, order: 'shuffle',
  });
  return { set, r };
}

// ---------------------------------------------------------------------------------------------------------------
if (run(1)) {
  step('1. a schedule alone makes the extension post: only the 30-second alarm runs, nobody presses a button');
  const { ws, collection } = await newWorkspace('alarm');
  await pairExtension(ws, 'เครื่องเดียว');
  assert.ok(alarms.has('fbap-cloud'), 'the cloud alarm is set after pairing');
  const { r } = await startNow(ws, collection, 'ชุดแรก', 'https://www.facebook.com/groups/alarmonly');
  assert.equal(r.status, 200, r.text);
  page.posted.length = 0;
  await waitFor('the post goes out without any command', () => page.posted.length === 1);
  assert.equal(page.posted[0].text, 'สวัสดีครับ A1');
}

// ---------------------------------------------------------------------------------------------------------------
if (run(2)) {
  step('2. the browser starts again: the alarm Chrome forgot is set again, and the extension takes its work by itself');
  const { ws, collection } = await newWorkspace('restart');
  await pairExtension(ws, 'เครื่องรีสตาร์ท');
  alarms.clear('fbap-cloud'); // a restarted browser may have no alarm left
  assert.equal(alarms.has('fbap-cloud'), false);
  for (const fn of listeners.startup) fn();
  await waitFor('the alarm is back', () => alarms.has('fbap-cloud'), { alarm: false, ms: 5000, every: 100 });
  const { r } = await startNow(ws, collection, 'ชุดรีสตาร์ท', 'https://www.facebook.com/groups/afterrestart');
  assert.equal(r.status, 200, r.text);
  page.posted.length = 0;
  await waitFor('the post goes out after the restart', () => page.posted.length === 1);
}

// ---------------------------------------------------------------------------------------------------------------
if (run(3)) {
  step('3. Facebook asks for a login once: when the person logs in again the extension goes on by itself, no "reconnect" click');
  const { ws, collection } = await newWorkspace('login');
  const device = await pairExtension(ws, 'เครื่องล็อกอิน');
  const first = await startNow(ws, collection, 'ชุดล็อกอิน 1', 'https://www.facebook.com/groups/loginone');
  assert.equal(first.r.status, 200, first.r.text);
  const second = await startNow(ws, collection, 'ชุดล็อกอิน 2', 'https://www.facebook.com/groups/logintwo');
  assert.equal(second.r.status, 200, second.r.text);
  page.posted.length = 0;
  page.loggedIn = false;
  await waitFor('the first job fails for lack of a login', () => cloud().lastJob && cloud().lastJob.ok === false);
  const account = (await api('GET', `/api/workspaces/${ws.id}/accounts`)).json.find((a) => a.id === device.accountId);
  assert.equal(account.health, 'relogin', 'the web shows that a login is needed');
  assert.equal(page.posted.length, 0);
  page.loggedIn = true; // the person signs in to Facebook in this Chrome
  await waitFor('the other group is posted without anybody pressing "reconnect"', () => page.posted.length === 1, { ms: 60000 });
  const healed = (await api('GET', `/api/workspaces/${ws.id}/accounts`)).json.find((a) => a.id === device.accountId);
  assert.equal(healed.health, 'ok', 'the account is healthy again');
}

// ---------------------------------------------------------------------------------------------------------------
if (run(4)) {
  step('4. two extensions: a set pinned to extension 1 never goes to extension 2, not even while 1 is unbound or paired again');
  const { ws, collection } = await newWorkspace('route');
  const one = await pairExtension(ws, 'ส่วนขยาย 1');
  const two = await rawExtension(ws, 'ส่วนขยาย 2');
  const siri = await startNow(ws, collection, 'siri', 'https://www.facebook.com/groups/sirigroup', one.accountId);
  assert.equal(siri.r.status, 200, siri.r.text);
  page.posted.length = 0;
  const wrong = [];
  await waitFor(
    'extension 1 posts the siri group',
    async () => {
      const job = await two.claim();
      if (job) wrong.push(job);
      return page.posted.length === 1;
    },
  );
  assert.deepEqual(wrong, [], 'extension 2 was handed none of the siri posts');

  // Extension 1 is unbound on the web: a siri schedule made now must wait for it, not go to extension 2.
  let r = await api('DELETE', `/api/workspaces/${ws.id}/devices/${one.id}`);
  assert.equal(r.status, 204, r.text);
  const blocked = await startNow(ws, collection, 'siri 2', 'https://www.facebook.com/groups/sirigroup2', one.accountId);
  assert.equal(blocked.r.status, 422, 'a set pinned to an unbound extension is refused, not given to another one');
  assert.equal(await two.claim(), null, 'extension 2 gets nothing from it');

  // The extension notices it was unbound, says so, and keeps who it was.
  await waitFor('the extension notices it was unbound', () => cloud().enabled === false && cloud().unboundAt > 0, { ms: 40000 });
  assert.ok(cloud().prev && cloud().prev.accountId === one.accountId, 'it remembers the account it was');

  // Paired again (from the web's "add device"): it is the same extension 1 again, so the pinned set works through it.
  const back = await pairExtension(ws, 'ส่วนขยาย 1');
  assert.equal(back.accountId, one.accountId, 'the new pairing takes over the old account');
  assert.equal(cloud().unboundAt || 0, 0, 'no longer unbound');
  const again = await startNow(ws, collection, 'siri 3', 'https://www.facebook.com/groups/sirigroup3', one.accountId);
  assert.equal(again.r.status, 200, again.r.text);
  page.posted.length = 0;
  const wrong2 = [];
  await waitFor(
    'extension 1 posts after being paired again',
    async () => {
      const job = await two.claim();
      if (job) wrong2.push(job);
      return page.posted.length === 1;
    },
    { ms: 200000 }, // the engine keeps a 2-minute gap between two posts of one browser
  );
  assert.deepEqual(wrong2, [], 'extension 2 was handed none of the posts');
}

// ---------------------------------------------------------------------------------------------------------------
if (run(5)) {
  step('5. pairing a browser that is already paired replaces its old pairing instead of leaving a second, silent one');
  const { ws } = await newWorkspace('replace');
  const first = await pairExtension(ws, 'ส่วนขยายเดิม');
  const before = (await devicesOf(ws)).length;
  const second = await pairExtension(ws, 'ส่วนขยายเดิม (ใหม่)');
  const after = await devicesOf(ws);
  assert.equal(after.length, before, 'still one device for this browser');
  assert.equal(second.accountId, first.accountId, 'the account (its sets and history) stays with the browser');
  assert.ok(!after.some((d) => d.id === first.id), 'the old device is gone');
}

// ---------------------------------------------------------------------------------------------------------------
if (run(6)) {
  step('6. a job that hangs (a tab that never answers) is stopped by the deadline, and the browser takes the next job');
  const { ws, collection } = await newWorkspace('hang');
  await pairExtension(ws, 'เครื่องค้าง');
  const first = await startNow(ws, collection, 'ชุดค้าง 1', 'https://www.facebook.com/groups/hangone');
  assert.equal(first.r.status, 200, first.r.text);
  const second = await startNow(ws, collection, 'ชุดค้าง 2', 'https://www.facebook.com/groups/hangtwo');
  assert.equal(second.r.status, 200, second.r.text);
  page.posted.length = 0;
  const was = tuning.jobDeadlineMs;
  tuning.jobDeadlineMs = 1500;
  page.hang = true;
  await waitFor('the hung job is stopped and reported', () => cloud().lastJob && cloud().lastJob.ok === false && /นานเกินไป/.test(cloud().lastJob.error || ''));
  page.hang = false;
  tuning.jobDeadlineMs = was ?? 14 * 60000;
  await waitFor('the next job goes out', () => page.posted.length === 1, { ms: 60000 });
}

console.log('\nผ่านทุกข้อ ✔');
process.exit(0);
