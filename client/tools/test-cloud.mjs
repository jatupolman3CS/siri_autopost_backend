// End-to-end test of the cloud mode: runs the real background.js (chrome.* and the Facebook
// page faked) against a running SIRIAUTOPOST.Api, and posts jobs scheduled in the web app.
//   node tools/test-cloud.mjs [apiUrl]          (default http://localhost:5100)
// Signs up a throwaway user on that API; it stays there with its workspace. A new account is on Free, so the
// platform admin (ADMIN_EMAIL / ADMIN_PASSWORD, default the Development account) grants it Pro.
import assert from 'node:assert/strict';

import {
  API, ADMIN, GROUP, BARE, data, local, listeners, alarms, page, tabUrls, bg, api, setToken, step, sleep, realSetTimeout,
  migrateSettings, newCampaign,
} from './cloud-harness.mjs';

async function schedule(wsId, accountId, extra = {}) {
  const r = await api('POST', `/api/workspaces/${wsId}/posts/schedule`, {
    content: 'ต้นไม้มาใหม่ ทักแชทได้เลย',
    startAt: new Date(Date.now() + 1500).toISOString(),
    useDelay: false,
    repeat: 'none',
    targets: [{ accountId, groups: [GROUP.name] }],
    ...extra,
  });
  assert.equal(r.status, 200, r.text);
  await sleep(2000); // due now
}

async function postsOf(wsId, accountId) {
  const from = new Date(Date.now() - 3600000).toISOString();
  const to = new Date(Date.now() + 3600000).toISOString();
  const r = await api('GET', `/api/workspaces/${wsId}/posts?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
  return r.json.filter((p) => p.accountId === accountId);
}

// ---------- test ----------

step('owner signs up (granted Pro by the admin: human-like settings can be changed)');
const email = `cloud${Date.now()}@test.co`;
let r = await api('POST', '/api/auth/signup', { email, password: 'password123' });
assert.equal(r.status, 200, r.text);
assert.equal(r.json.user.plan, 'free', 'a new account starts on Free');
setToken(r.json.token);
const ownerToken = r.json.token;
const ownerId = r.json.user.id;
r = await api('POST', '/api/auth/login', ADMIN);
assert.equal(r.status, 200, `admin login: ${r.text}`);
setToken(r.json.token);
r = await api('PUT', `/api/admin/customers/${ownerId}/plan`, { plan: 'pro' });
assert.equal(r.status, 200, r.text);
setToken(ownerToken);
const ws = (await api('GET', '/api/workspaces')).json[0];

step('extension has one campaign with two groups (one added by its address only)');
const settings = migrateSettings(undefined);
const camp = settings.campaigns[0] || newCampaign(1);
camp.groups = [GROUP, BARE].map((g) => ({ url: g.url, name: g.name, enabled: true, text: '' }));
settings.campaigns = [camp];
await local.set({ settings });

step('the web app\'s "connect this Chrome" address opens the approval page; other addresses do not');
for (const fn of listeners.updated) fn(5, { url: 'https://evil.example/somewhere#ap-pair=1&code=XXXX-XXXX' });
for (const fn of listeners.updated) fn(5, { url: `${API}/connect-extension#ap-pair=1&code=ABCD-EFGH&name=${encodeURIComponent('คอมร้าน')}` });
await sleep(50);
assert.equal(tabUrls.length, 1, tabUrls.join(' '));
assert.match(tabUrls[0], /status\.html#pair=/);
const req = JSON.parse(decodeURIComponent(tabUrls[0].split('#pair=')[1]));
assert.deepEqual(req, { apiUrl: API, code: 'ABCD-EFGH', name: 'คอมร้าน', workspace: '' });

step('pairing: wrong code, then the code from the web app');
r = await bg('cloudPair', { apiUrl: API, code: 'ZZZZ-ZZZZ', name: 'คอมทดสอบ' });
assert.equal(r.ok, false);
assert.match(r.error, /รหัสจับคู่/);
const code = (await api('POST', `/api/workspaces/${ws.id}/devices/pairing`)).json.code;
r = await bg('cloudPair', { apiUrl: API, code: code.toLowerCase(), name: 'คอมทดสอบ' });
assert.equal(r.ok, true, r.error);
assert.ok(alarms.has('fbap-cloud'), 'cloud alarm set');
const cloud = data.get('cloud');
assert.equal(cloud.workspaceName, ws.name);
assert.equal(cloud.groups, 2);

step('the web app sees the device and its Facebook account with the group');
const devices = (await api('GET', `/api/workspaces/${ws.id}/devices`)).json;
assert.equal(devices.length, 1);
assert.equal(devices[0].name, 'คอมทดสอบ');
assert.equal(devices[0].online, true);
const account = (await api('GET', `/api/workspaces/${ws.id}/accounts`)).json.find((a) => a.id === devices[0].accountId);
assert.equal(account.connected, true);
assert.deepEqual(account.groups, [GROUP.name, 'groups/bareaddress']);

step('the web app has this browser\'s campaigns (uploaded on pairing)');
const devBase = `/api/workspaces/${ws.id}/devices/${devices[0].id}`;
let webCfg = (await api('GET', `${devBase}/config`)).json;
assert.equal(webCfg.revision, 1);
assert.equal(webCfg.hasContent, true);
assert.equal(webCfg.updatedByDevice, true);
assert.equal(webCfg.settings.campaigns[0].groups[0].url, GROUP.url);
assert.equal(data.get('cloud').configRevision, 1);

step('an edit in the web app (with a new image) reaches the browser');
const dot = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';
r = await api('PUT', `/api/workspaces/${ws.id}/extension-images/webimg1`, { name: 'web.png', type: 'image/png', data: dot });
assert.equal(r.status, 204, r.text);
const edited = structuredClone(webCfg.settings);
edited.campaigns[0].name = 'ชุดจากเว็บ';
edited.campaigns[0].posts = [{ id: 'wp1', text: 'แก้จากเว็บ {{code}}', imageIds: ['webimg1'], groupUrls: [] }];
r = await api('PUT', `${devBase}/config`, { settings: edited, baseRevision: 1 });
assert.equal(r.status, 200, r.text);
r = await bg('cloudConfig', { mode: 'auto' });
assert.equal(r.ok, true, r.error);
assert.equal(data.get('settings').campaigns[0].name, 'ชุดจากเว็บ');
assert.equal(data.get('img:webimg1').data, dot);
assert.ok(data.get('cloudPulled'), 'dashboard told to reload');
assert.equal(data.get('cloud').configRevision, 2);

step('an edit in the browser goes up; state, log and a command from the web come back');
const s2 = data.get('settings');
s2.campaigns[0].config.groupDelayMin = 4;
await local.set({ settings: s2 });
r = await bg('cloudConfig', { mode: 'auto' });
assert.equal(r.ok, true, r.error);
webCfg = (await api('GET', `${devBase}/config`)).json;
assert.equal(webCfg.revision, 3);
assert.equal(webCfg.settings.campaigns[0].config.groupDelayMin, 4);
const cmd = (await api('POST', `${devBase}/commands`, { cmd: 'clearLogs' })).json;
assert.equal(cmd.status, 'pending');
r = await bg('cloudConfig', { mode: 'auto' });
assert.equal(r.ok, true, r.error);
// The web's held sync may have been cut short after the server handed the command over; the next sync
// hands it out again and the extension runs it once.
let cmdDone;
for (let i = 0; i < 40; i++) {
  cmdDone = (await api('GET', `${devBase}/commands/${cmd.id}`)).json;
  if (cmdDone.status === 'done') break;
  r = await bg('cloudConfig', { mode: 'auto' });
  assert.equal(r.ok, true, r.error);
  await sleep(50);
}
assert.equal(cmdDone.status, 'done');
assert.equal(cmdDone.result.ok, true);
const live = (await api('GET', `${devBase}/live`)).json;
assert.equal(live.online, true);
assert.equal(live.revision, 3);
assert.ok(live.state && typeof live.state === 'object', 'state reported');
assert.ok(live.logs.some((l) => l.msg.includes('โหลดการตั้งค่าจากเว็บแล้ว')), 'log lines reported');

step('the workspace event stream recorded all of it, in order');
const events = (await api('GET', `/api/workspaces/${ws.id}/events?after=0`)).json;
const types = events.events.map((e) => e.type);
for (const t of ['device.paired', 'device.config', 'device.state', 'device.log', 'device.command'])
  assert.ok(types.includes(t), `event ${t} recorded (got ${types.join(', ')})`);
assert.ok(events.events.every((e, i) => i === 0 || e.seq > events.events[i - 1].seq), 'seq ascending');
assert.equal(events.head, events.events[events.events.length - 1].seq);
// Two syncs that overlap may both hand the command over (the device runs it once), so "sent" can repeat.
const cmdEvents = events.events
  .filter((e) => e.type === 'device.command' && e.payload.id === cmd.id)
  .map((e) => e.payload.status)
  .filter((st, i, all) => st !== all[i - 1]);
assert.deepEqual(cmdEvents, ['pending', 'sent', 'done'], 'the command went pending -> sent -> done on the stream');

step('a sync held open (wait) returns as soon as the web app sends a command');
{
  const c = data.get('cloud');
  const t0 = Date.now();
  const held = fetch(`${API}/api/device/sync`, {
    method: 'POST',
    headers: { 'X-Device-Key': c.deviceKey, 'Content-Type': 'application/json' },
    body: JSON.stringify({ version: '2.2.0', takeCommands: true, wait: true }),
  }).then((r) => r.json());
  await new Promise((r) => setTimeout(r, 500));
  const sent = (await api('POST', `${devBase}/commands`, { cmd: 'syncNow' })).json;
  const got = await held;
  assert.ok(Date.now() - t0 < 10000, 'answered well before the 25 s wait');
  // The background listener of background.js holds a sync open too; whichever wakes first takes the command.
  const status = (await api('GET', `${devBase}/commands/${sent.id}`)).json.status;
  assert.ok(got.commands.length === 1 || ['sent', 'done'].includes(status), `command taken at once (held: ${got.commands.length}, status: ${status})`);
  if (got.commands.length === 1) {
    assert.equal(got.commands[0].id, sent.id);
    r = await fetch(`${API}/api/device/commands/${sent.id}/result`, {
      method: 'POST',
      headers: { 'X-Device-Key': c.deviceKey, 'Content-Type': 'application/json' },
      body: JSON.stringify({ result: { ok: true } }),
    });
    assert.equal(r.status, 204);
  }
}

step('faster anti-ban for the test: no typing, no browsing, 1-2 minute gap');
const engine = (await api('GET', `/api/workspaces/${ws.id}/engine`)).json;
r = await api('PUT', `/api/workspaces/${ws.id}/engine/anti-ban`, { ...engine.antiBan, min: 1, max: 2, typing: false, scroll: false });
assert.equal(r.status, 200, r.text);

step('Facebook logged out: the post fails and the account needs a new login');
page.loggedIn = false;
await schedule(ws.id, account.id);
r = await bg('cloudSync');
assert.equal(r.ok, true, r.error);
assert.equal(r.posted, true);
let mine = await postsOf(ws.id, account.id);
assert.equal(mine[0].status, 'failed');
assert.equal(mine[0].failureCode, 'session');
let acc = (await api('GET', `/api/workspaces/${ws.id}/accounts`)).json.find((a) => a.id === account.id);
assert.equal(acc.health, 'relogin');
assert.equal(page.posted.length, 0);

step('signed in again: a post with an image goes out');
page.loggedIn = true;
await api('POST', `/api/workspaces/${ws.id}/accounts/${account.id}/reconnect`);
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
const form = new FormData();
form.append('file', new Blob([png], { type: 'image/png' }), 'ต้นไม้.png');
const media = (await api('POST', `/api/workspaces/${ws.id}/media`, undefined, { form })).json;
await schedule(ws.id, account.id, { mediaIds: [media.id] });
r = await bg('cloudSync');
assert.equal(r.ok, true, r.error);
assert.equal(r.posted, true);
assert.equal(r.result, true, JSON.stringify(data.get('cloud').lastJob));
assert.equal(page.posted.length, 1);
assert.equal(page.posted[0].text, 'ต้นไม้มาใหม่ ทักแชทได้เลย');
assert.deepEqual(page.posted[0].files, ['ต้นไม้']);
assert.equal([...data.keys()].filter((k) => k.startsWith('cloudimg:')).length, 0, 'media removed after posting');
mine = await postsOf(ws.id, account.id);
assert.ok(mine.some((p) => p.status === 'success'));
assert.equal(data.get('cloud').lastJob.ok, true);

step('a name Facebook shows later does not rename a group the web app already has');
{
  // Queued posts name their group, so the name the web app knows stays until the campaign itself names it.
  const st = data.get('state');
  st.groupNames = { ...(st.groupNames || {}), [BARE.url + '/']: 'ชื่อที่เห็นบน Facebook' };
  await local.set({ state: st });
  r = await bg('cloudSync');
  assert.equal(r.ok, true, r.error);
  const names = (await api('GET', `/api/workspaces/${ws.id}/accounts`)).json.find((a) => a.id === account.id).groups;
  assert.deepEqual(names, [GROUP.name, 'groups/bareaddress']);
}

step('the anti-ban gap holds the next post back');
await schedule(ws.id, account.id);
r = await bg('cloudSync');
assert.equal(r.posted, false);

step('paused in the web app: no posts are taken');
r = await api('PUT', `/api/workspaces/${ws.id}/devices/${devices[0].id}`, { jobsPaused: true });
assert.equal(r.status, 200, r.text);
r = await bg('cloudSync');
assert.equal(r.posted, false);
assert.equal(data.get('cloud').paused, true);
await api('PUT', `/api/workspaces/${ws.id}/devices/${devices[0].id}`, { jobsPaused: false });
await bg('cloudSync');
assert.equal(data.get('cloud').paused, false);

step('unbound in the web app: the extension reports it');
await api('DELETE', `/api/workspaces/${ws.id}/devices/${devices[0].id}`);
r = await bg('cloudSync');
assert.equal(r.ok, false);
assert.match(r.error, /ยกเลิกการผูก/);
assert.equal(data.get('cloud').enabled, false, 'back to "not paired" by itself');
assert.equal(alarms.has('fbap-cloud'), false);
assert.ok(data.get('settings').campaigns.length, 'the campaigns stay in the browser');

console.log('\nผ่านทุกข้อ ✔');
process.exit(0);
