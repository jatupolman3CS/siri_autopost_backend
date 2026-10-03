// End-to-end test of the cloud mode: runs the real background.js (chrome.* and the Facebook
// page faked) against a running SIRIAUTOPOST.Api, and posts jobs scheduled in the web app.
//   node tools/test-cloud.mjs [apiUrl]          (default http://localhost:5100)
// Signs up a throwaway user on that API; it stays there with its workspace.
import assert from 'node:assert/strict';

const API = (process.argv[2] || 'http://localhost:5100').replace(/\/+$/, '');
const GROUP = { url: 'https://www.facebook.com/groups/plantlovers', name: 'คนรักต้นไม้ (ทดสอบ)' };

// ---------- fake chrome ----------

const data = new Map();
const listeners = { changed: [], message: [], alarm: [], startup: [], installed: [], updated: [] };
const alarms = new Map();
const clone = (v) => (v === undefined ? undefined : JSON.parse(JSON.stringify(v)));
const noop = () => {};
const event = (arr) => ({
  addListener: (fn) => arr.push(fn),
  removeListener: (fn) => {
    const i = arr.indexOf(fn);
    if (i >= 0) arr.splice(i, 1);
  },
});

const local = {
  async get(keys) {
    const list = keys == null ? [...data.keys()] : typeof keys === 'string' ? [keys] : Array.isArray(keys) ? keys : Object.keys(keys);
    const out = {};
    for (const k of list) if (data.has(k)) out[k] = clone(data.get(k));
    return out;
  },
  async set(items) {
    const changes = {};
    for (const [k, v] of Object.entries(items)) {
      changes[k] = { oldValue: clone(data.get(k)), newValue: clone(v) };
      data.set(k, clone(v));
    }
    for (const fn of listeners.changed) fn(changes, 'local');
  },
  async remove(keys) {
    for (const k of [].concat(keys)) data.delete(k);
  },
  async getKeys() {
    return [...data.keys()];
  },
};

// The Facebook group page, as content.js would answer for it.
const page = {
  loggedIn: true,
  open: false,
  text: '',
  attached: [], // media records content.js would have loaded from storage
  posted: [],   // { text, files }
};
const contentHandlers = {
  check: () => ({ ok: true, title: `${GROUP.name} | Facebook`, login: !page.loggedIn, checkpoint: false }),
  blockCheck: () => ({ ok: true, blocked: false }),
  scroll: () => ({ ok: true }),
  scrollTop: () => ({ ok: true }),
  openComposer: () => ((page.open = true), (page.text = ''), { ok: true }),
  insert: (m) => ((page.text += m.text), { ok: true }),
  newline: () => ((page.text += '\n'), { ok: true }),
  backspace: () => ((page.text = Array.from(page.text).slice(0, -1).join('')), { ok: true }),
  setText: (m) => ((page.text = m.text), { ok: true }),
  paste: (m) => ((page.text += m.text), { ok: true }),
  getText: () => ({ ok: true, text: page.text }),
  attachImages: async (m) => {
    const keys = m.imageIds.map((id) => (String(id).includes(':') ? id : 'img:' + id));
    page.attached = keys.map((k) => data.get(k)).filter(Boolean);
    return page.attached.length ? { ok: true, method: 'input' } : { ok: false, error: 'ไม่พบไฟล์รูปในที่เก็บข้อมูล' };
  },
  postState: () => ({ ok: true, open: page.open, postEnabled: true, hasNext: false, uploading: false, blocked: false }),
  clickPost: () => {
    page.posted.push({ text: page.text, files: page.attached.map((f) => f.name) });
    page.open = false;
    page.attached = [];
    return { ok: true };
  },
  discard: () => ((page.open = false), { ok: true }),
};

let windowId = 1;
const tabUrls = []; // addresses background.js sent tabs to
globalThis.chrome = {
  storage: { local, onChanged: event(listeners.changed) },
  runtime: {
    getManifest: () => ({ version: 'test' }),
    getURL: (p) => 'file:///nonexistent/' + p,
    onMessage: event(listeners.message),
    onStartup: event(listeners.startup),
    onInstalled: event(listeners.installed),
    getPlatformInfo: async () => ({}),
  },
  alarms: {
    create: async (name, info) => alarms.set(name, info),
    get: async (name) => (alarms.has(name) ? { name, ...alarms.get(name) } : undefined),
    getAll: async () => [...alarms.keys()].map((name) => ({ name })),
    clear: async (name) => alarms.delete(name),
    onAlarm: event(listeners.alarm),
  },
  action: { onClicked: event([]) },
  tabs: {
    query: async () => [],
    create: async () => ({}),
    get: async (id) => ({ id, windowId }),
    update: async (id, props = {}) => {
      if (props.url) tabUrls.push(props.url);
      // A navigation: loading, then complete.
      setTimeout(() => {
        for (const fn of [...listeners.updated]) fn(id, { status: 'loading' });
        for (const fn of [...listeners.updated]) fn(id, { status: 'complete' });
      }, 10);
      return { id };
    },
    remove: async () => {},
    onUpdated: event(listeners.updated),
    sendMessage: async (_tabId, msg) => {
      const h = contentHandlers[msg.cmd];
      return h ? h(msg) : { ok: false, error: 'unknown command: ' + msg.cmd };
    },
    captureVisibleTab: async () => null,
  },
  windows: {
    create: async () => ({ id: windowId, tabs: [{ id: 7 }] }),
    get: async (id) => ({ id, state: 'normal' }),
    getLastFocused: async () => ({ id: 99 }),
    update: async () => ({}),
  },
  scripting: { executeScript: async () => [] },
  permissions: { contains: async () => false },
};

// Waits are real in background.js (it imitates a person); shorten them for the test.
const realSetTimeout = globalThis.setTimeout;
globalThis.setTimeout = (fn, ms, ...a) => realSetTimeout(fn, (ms ?? 0) >= 100000 ? ms : Math.min(ms ?? 0, 50), ...a); // keeps request timeouts

const { migrateSettings, newCampaign } = await import('../lib/shared.js');
await import('../background.js');

function bg(cmd, extra = {}) {
  return new Promise((resolve) => {
    const msg = { target: 'fbap-bg', cmd, ...extra };
    for (const fn of listeners.message) fn(msg, {}, resolve);
  });
}

// ---------- web app client (the shop owner) ----------

let token = '';
async function api(method, path, body, { form = null } = {}) {
  const res = await fetch(API + path, {
    method,
    headers: {
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...(body === undefined || form ? {} : { 'Content-Type': 'application/json' }),
    },
    body: form ?? (body === undefined ? undefined : JSON.stringify(body)),
  });
  const text = await res.text();
  let json = null;
  try {
    json = JSON.parse(text);
  } catch {
    /* not json */
  }
  return { status: res.status, json, text };
}

const step = (s) => console.log('•', s);
const sleep = (ms) => new Promise((r) => realSetTimeout(r, ms));

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

step('owner signs up (Pro: human-like settings can be changed)');
const email = `cloud${Date.now()}@test.co`;
let r = await api('POST', '/api/auth/signup', { email, password: 'password123', plan: 'pro' });
assert.equal(r.status, 200, r.text);
token = r.json.token;
const ws = (await api('GET', '/api/workspaces')).json[0];

step('extension has one campaign with one group');
const settings = migrateSettings(undefined);
const camp = settings.campaigns[0] || newCampaign(1);
camp.groups = [{ url: GROUP.url, name: GROUP.name, enabled: true, text: '' }];
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
assert.equal(cloud.groups, 1);

step('the web app sees the device and its Facebook account with the group');
const devices = (await api('GET', `/api/workspaces/${ws.id}/devices`)).json;
assert.equal(devices.length, 1);
assert.equal(devices[0].name, 'คอมทดสอบ');
assert.equal(devices[0].online, true);
const account = (await api('GET', `/api/workspaces/${ws.id}/accounts`)).json.find((a) => a.id === devices[0].accountId);
assert.equal(account.connected, true);
assert.deepEqual(account.groups, [GROUP.name]);

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
const cmdDone = (await api('GET', `${devBase}/commands/${cmd.id}`)).json;
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
const cmdEvents = events.events.filter((e) => e.type === 'device.command' && e.payload.id === cmd.id).map((e) => e.payload.status);
assert.deepEqual(cmdEvents, ['pending', 'done'], 'the command went pending -> done on the stream');

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
