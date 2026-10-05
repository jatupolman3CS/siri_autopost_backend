// End-to-end test of the web app's posting flow (collection -> link set -> schedule) with an extension that has
// NO campaign and NO group of its own: the schedule alone must hand the extension its work, and the web app's
// "take the due posts now" command (takeJobs) must make it post at once. Runs the real background.js against a
// running SIRIAUTOPOST.Api (see cloud-harness.mjs).
//   node tools/test-schedule-flow.mjs [apiUrl]          (default http://localhost:5100)
// Signs up a throwaway user on that API; it stays there with its workspace.
import assert from 'node:assert/strict';

import { API, ADMIN, data, page, bg, api, setToken, step, sleep } from './cloud-harness.mjs';

const GROUP_URL = 'https://www.facebook.com/groups/webonly';

step('owner signs up (granted Pro by the admin)');
const email = `flow${Date.now()}@test.co`;
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

step('the extension is paired and has no group in any campaign');
const code = (await api('POST', `/api/workspaces/${ws.id}/devices/pairing`)).json.code;
r = await bg('cloudPair', { apiUrl: API, code, name: 'คอมทดสอบ' });
assert.equal(r.ok, true, r.error);
const device = (await api('GET', `/api/workspaces/${ws.id}/devices`)).json[0];
assert.equal(device.online, true);
assert.equal((data.get('settings')?.campaigns ?? []).flatMap((c) => c.groups ?? []).length, 0, 'no group set up in the extension');
const engine = (await api('GET', `/api/workspaces/${ws.id}/engine`)).json;
r = await api('PUT', `/api/workspaces/${ws.id}/engine/anti-ban`, { ...engine.antiBan, min: 1, max: 2, typing: false, scroll: false });
assert.equal(r.status, 200, r.text);

step('a collection, a link set with a group that only the web app knows, and a schedule that starts now');
r = await api('POST', `/api/workspaces/${ws.id}/collections`, { name: 'ชุดทดสอบ' });
assert.equal(r.status, 200, r.text);
const collection = r.json;
r = await api('POST', `/api/workspaces/${ws.id}/collections/${collection.id}/posts`, { text: 'สวัสดีครับ {{code}}', mediaIds: [] });
assert.equal(r.status, 200, r.text);
r = await api('POST', `/api/workspaces/${ws.id}/link-sets`, { name: 'ชุดลิงก์' });
assert.equal(r.status, 200, r.text);
const set = r.json;
r = await api('POST', `/api/workspaces/${ws.id}/link-sets/${set.id}/links`, { name: 'กลุ่มจากเว็บ', url: GROUP_URL, code: 'A1' });
assert.equal(r.status, 200, r.text);
r = await api('POST', `/api/workspaces/${ws.id}/schedules`, {
  collectionId: collection.id,
  linkSetId: set.id,
  mode: 'daily',
  times: ['09:00'],
  startNow: true,
  utcOffsetMinutes: 420,
  order: 'shuffle',
});
assert.equal(r.status, 200, r.text);
assert.ok(r.json.created >= 1, 'the opening round is queued');
await sleep(25000); // the opening round starts 2-20 s after it is made

step('"take the due posts now" reaches the extension, which posts without any campaign setup');
r = await api('POST', `/api/workspaces/${ws.id}/devices/${device.id}/commands`, { cmd: 'takeJobs' });
assert.equal(r.status, 200, r.text);
const cmd = r.json;
let done;
for (let i = 0; i < 40; i++) {
  await bg('cloudConfig', { mode: 'auto' });
  done = (await api('GET', `/api/workspaces/${ws.id}/devices/${device.id}/commands/${cmd.id}`)).json;
  if (done.status === 'done' && page.posted.length) break;
  await sleep(250);
}
assert.equal(done.status, 'done');
assert.equal(done.result.ok, true);
assert.equal(page.posted.length, 1, 'the group of the link set was posted to');
assert.equal(page.posted[0].text, 'สวัสดีครับ A1');
const live = (await api('GET', `/api/workspaces/${ws.id}/devices/${device.id}/live`)).json;
assert.ok(live.logs.some((l) => l.msg.includes(GROUP_URL)), 'the extension logged the group it posted to');

step('a link set pinned to a browser that was unbound still works through the browser paired afterwards');
r = await api('POST', `/api/workspaces/${ws.id}/link-sets`, { name: 'ชุดที่ผูกเครื่องเก่า', postAsAccountId: device.accountId });
assert.equal(r.status, 200, r.text);
const pinned = r.json;
r = await api('POST', `/api/workspaces/${ws.id}/link-sets/${pinned.id}/links`, { name: 'กลุ่มอีกกลุ่ม', url: 'https://www.facebook.com/groups/another', code: 'B2' });
assert.equal(r.status, 200, r.text);
r = await api('DELETE', `/api/workspaces/${ws.id}/devices/${device.id}`);
assert.equal(r.status, 204, r.text);
const code2 = (await api('POST', `/api/workspaces/${ws.id}/devices/pairing`)).json.code;
r = await bg('cloudPair', { apiUrl: API, code: code2, name: 'คอมเครื่องใหม่' });
assert.equal(r.ok, true, r.error);
r = await api('POST', `/api/workspaces/${ws.id}/schedules`, {
  collectionId: collection.id,
  linkSetId: pinned.id,
  mode: 'daily',
  times: ['09:00'],
  startNow: true,
  utcOffsetMinutes: 420,
  order: 'shuffle',
});
assert.equal(r.status, 200, r.text);
assert.ok(r.json.created >= 1, 'queued for the new browser');

console.log('\nผ่านทุกข้อ ✔');
process.exit(0);
