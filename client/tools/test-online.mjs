// End-to-end test of the online mode: runs the real background.js with a fake
// chrome.* API against a running server (backend/SIRI.AUTOPOST.Server).
//   node tools/test-online.mjs [serverUrl] [adminUser] [adminPassword]
// Uses a throwaway config + device on that server and deletes them at the end.
import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';

const SERVER = process.argv[2] || 'http://localhost:5080';
const USER = process.argv[3] || 'admin';
const PASS = process.argv[4] || process.env.FBAP_ADMIN_PASSWORD || '';

// ---------- fake chrome ----------

const data = new Map();
const listeners = { changed: [], message: [], alarm: [], startup: [], installed: [] };
const alarms = new Map();
const clone = (v) => (v === undefined ? undefined : JSON.parse(JSON.stringify(v)));

function fireChanged(changes) {
  for (const fn of listeners.changed) fn(changes, 'local');
}

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
    fireChanged(changes);
  },
  async remove(keys) {
    for (const k of [].concat(keys)) data.delete(k);
  },
  async getKeys() {
    return [...data.keys()];
  },
};

const noop = () => {};
const event = (arr) => ({ addListener: (fn) => arr.push(fn), removeListener: noop });

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
  tabs: { query: async () => [], create: async () => ({}), update: async () => ({}), onUpdated: { addListener: () => {} } },
  windows: { update: async () => ({}) },
  permissions: { contains: async () => false },
};

await import('../background.js');

function bg(cmd, extra = {}) {
  return new Promise((resolve) => {
    const msg = { target: 'fbap-bg', cmd, ...extra };
    for (const fn of listeners.message) fn(msg, {}, resolve);
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------- admin client ----------

let cookie = '';
async function admin(method, path, body) {
  const res = await fetch(SERVER + path, {
    method,
    headers: { ...(cookie ? { Cookie: cookie } : {}), ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
    redirect: 'manual',
  });
  const set = res.headers.get('set-cookie');
  if (set) cookie = set.split(';')[0];
  const text = await res.text();
  let json = null;
  try {
    json = JSON.parse(text);
  } catch {
    /* not json */
  }
  return { status: res.status, json, text, headers: res.headers };
}

const step = (s) => console.log('•', s);

// ---------- test ----------

step('login');
assert.equal((await admin('POST', '/api/auth/login', { username: USER, password: 'wrong' })).status, 401);
const login = await admin('POST', '/api/auth/login', { username: USER, password: PASS });
assert.equal(login.status, 200, login.text);
assert.equal((await admin('GET', '/api/auth/me')).json.username, USER);

step('web editor page needs login and gets the shim');
const anon = await fetch(SERVER + '/app/dashboard.html?profile=x', { redirect: 'manual' });
assert.equal(anon.status, 302);
const page = await admin('GET', '/app/dashboard.html?profile=x');
assert.ok(page.text.includes('/web/shim.js') && page.text.includes('dashboard.js'));
assert.equal((await fetch(SERVER + '/app/lib/shared.js')).status, 200);
assert.equal((await fetch(SERVER + '/app/config/autopost-config.json')).status, 404, 'config folder must not be served');

step('create config + device');
const prof = (await admin('POST', '/api/profiles', { name: 'TEST online ' + Date.now() })).json;
const dev = (await admin('POST', '/api/devices', { name: 'TEST device', profileId: prof.id })).json;
assert.ok(dev.key.startsWith('dk_'));

step('CORS preflight for the extension');
const pre = await fetch(SERVER + '/api/device/heartbeat', {
  method: 'OPTIONS',
  headers: { Origin: 'chrome-extension://abc', 'Access-Control-Request-Method': 'POST', 'Access-Control-Request-Headers': 'content-type,x-device-key' },
});
assert.equal(pre.headers.get('access-control-allow-origin'), '*');

step('seed this "browser" with the folder config');
const { parseBackup } = await import('../lib/backup.js');
const parsed = parseBackup(await readFile(new URL('../config/autopost-config.json', import.meta.url), 'utf8'));
const seedImages = Object.entries(parsed.images).slice(0, 3);
const settings = clone(parsed.settings);
// Keep the test small: first campaign, its first 5 posts, only the seeded images.
settings.campaigns = settings.campaigns.slice(0, 1);
const c0 = settings.campaigns[0];
c0.posts = c0.posts.slice(0, 5);
const keepIds = new Set(seedImages.map(([id]) => id));
c0.leadImageIds = (c0.leadImageIds || []).filter((id) => keepIds.has(id));
for (const p of c0.posts) p.imageIds = p.imageIds.filter((id) => keepIds.has(id));
c0.posts[0].imageIds = seedImages.map(([id]) => id);
settings.global.telegram.botToken = 'local-token';
await local.set({ settings, ...Object.fromEntries(seedImages.map(([id, rec]) => ['img:' + id, rec])) });
await local.set({ logs: [{ t: Date.now(), level: 'info', msg: 'log before connect' }] });

step('bad key is refused');
let r = await bg('onlineConnect', { serverUrl: SERVER, deviceKey: 'dk_wrong' });
assert.equal(r.ok, false);

step('connect: empty server config gets this browser\'s settings');
r = await bg('onlineConnect', { serverUrl: SERVER + '/', deviceKey: dev.key });
assert.equal(r.ok, true, r.error);
let srv = (await admin('GET', `/api/profiles/${prof.id}/settings`)).json;
assert.equal(srv.revision, 1);
assert.equal(srv.settings.campaigns[0].name, c0.name);
assert.equal(srv.settings.campaigns[0].posts.length, 5);
assert.equal(srv.settings.global.telegram.botToken, 'local-token');
const srvImages = (await admin('GET', `/api/profiles/${prof.id}/images`)).json;
assert.deepEqual(new Set(srvImages), keepIds);
const got = (await admin('POST', `/api/profiles/${prof.id}/images/get`, { ids: [seedImages[0][0]] })).json;
assert.equal(got[seedImages[0][0]].data, seedImages[0][1].data, 'image round trip');
let online = data.get('online');
assert.equal(online.enabled, true);
assert.equal(online.revision, 1);
assert.ok(alarms.has('fbap-sync'));

step('state and logs reach the server');
let live = (await admin('GET', `/api/devices/${dev.id}/live`)).json;
assert.equal(live.device.online, true);
assert.ok(live.logs.some((l) => l.msg === 'log before connect'));
assert.equal(live.profileRevision, 1);

step('web edit -> device pulls it');
const edited = clone(srv.settings);
edited.campaigns[0].name = 'แก้จากเว็บ';
edited.campaigns[0].posts.pop();
edited.global.telegram.botToken = '';
let put = await admin('PUT', `/api/profiles/${prof.id}/settings`, { settings: edited, baseRevision: 1 });
assert.equal(put.status, 200, put.text);
assert.equal(put.json.revision, 2);
assert.equal((await admin('PUT', `/api/profiles/${prof.id}/settings`, { settings: edited, baseRevision: 1 })).status, 409, 'stale save');
for (const fn of listeners.alarm) fn({ name: 'fbap-sync' });
await sleep(1500);
r = await bg('onlineSync', { mode: 'auto' });
assert.equal(r.ok, true, r.error);
let mine = data.get('settings');
assert.equal(mine.campaigns[0].name, 'แก้จากเว็บ');
assert.equal(mine.campaigns[0].posts.length, 4);
assert.equal(mine.global.telegram.botToken, 'local-token', 'no token on server keeps the local Telegram');
assert.ok(data.get('onlinePulled'), 'dashboards reload');
assert.equal(data.get('online').revision, 2);

step('nothing changed -> no push, no pull');
r = await bg('onlineSync', { mode: 'auto' });
assert.equal((await admin('GET', `/api/profiles/${prof.id}/revision`)).json.revision, 2);

step('local edit -> pushed to the server');
mine = clone(data.get('settings'));
mine.campaigns[0].posts[0].text = 'แก้ในเครื่องรัน';
await local.set({ settings: mine });
await sleep(5500); // PUSH_DELAY_MS
await bg('onlineSync', { mode: 'auto' });
srv = (await admin('GET', `/api/profiles/${prof.id}/settings`)).json;
assert.equal(srv.revision, 3);
assert.equal(srv.settings.campaigns[0].posts[0].text, 'แก้ในเครื่องรัน');
assert.ok(srv.updatedBy.startsWith('device:'));

step('edited on both sides -> server wins');
mine = clone(data.get('settings'));
mine.campaigns[0].name = 'local name';
data.set('settings', mine); // no change event: simulate an edit not pushed yet
const both = clone(srv.settings);
both.campaigns[0].name = 'server name';
assert.equal((await admin('PUT', `/api/profiles/${prof.id}/settings`, { settings: both, baseRevision: 3 })).status, 200);
await bg('onlineSync', { mode: 'auto' });
assert.equal(data.get('settings').campaigns[0].name, 'server name');

step('remote commands: tgFindChats / clearLogs / unknown');
const cmd1 = (await admin('POST', `/api/devices/${dev.id}/commands`, { cmd: 'tgFindChats', args: {} })).json;
const cmd2 = (await admin('POST', `/api/devices/${dev.id}/commands`, { cmd: 'clearLogs', args: {} })).json;
assert.equal((await admin('POST', `/api/devices/${dev.id}/commands`, { cmd: 'rm -rf', args: {} })).status, 400);
await bg('onlineSync', { mode: 'auto' });
const res1 = (await admin('GET', `/api/commands/${cmd1.id}`)).json;
assert.equal(res1.status, 'done');
assert.equal(res1.result.ok, false); // the test bot token is not real
const res2 = (await admin('GET', `/api/commands/${cmd2.id}`)).json;
assert.equal(res2.status, 'done');
assert.equal(res2.result.ok, true);
// clearLogs keeps only logs written after it
assert.ok((data.get('logs') || []).every((l) => !l.msg.includes('log before connect')));

step('explicit pull / push');
r = await bg('onlineSync', { mode: 'push' });
assert.equal(r.ok, true, r.error);
assert.equal((await admin('GET', `/api/profiles/${prof.id}/revision`)).json.revision, 4, 'same settings keep the revision');
r = await bg('onlineSync', { mode: 'pull' });
assert.equal(r.ok, true, r.error);

step('copy config copies images');
const copy = (await admin('POST', '/api/profiles', { name: 'TEST copy', copyFrom: prof.id })).json;
assert.equal((await admin('GET', `/api/profiles/${copy.id}/images`)).json.length, keepIds.size);

step('device switched to another config pulls it');
const other = (await admin('POST', '/api/profiles', { name: 'TEST other' })).json;
const otherSettings = clone(srv.settings);
otherSettings.campaigns[0].name = 'config อื่น';
otherSettings.campaigns[0].posts.forEach((p) => (p.imageIds = []));
otherSettings.campaigns[0].leadImageIds = [];
await admin('PUT', `/api/profiles/${other.id}/settings`, { settings: otherSettings });
await admin('PUT', `/api/devices/${dev.id}`, { name: 'TEST device', profileId: other.id });
await bg('onlineSync', { mode: 'auto' });
assert.equal(data.get('settings').campaigns[0].name, 'config อื่น');
assert.equal([...data.keys()].filter((k) => k.startsWith('img:')).length, 0, 'unused images removed');

step('disconnect');
r = await bg('onlineDisconnect');
assert.equal(r.ok, true);
assert.equal(alarms.has('fbap-sync'), false);

step('regenerated key locks out the old one');
await admin('POST', `/api/devices/${dev.id}/key`);
r = await bg('onlineConnect', { serverUrl: SERVER, deviceKey: dev.key });
assert.equal(r.ok, false);

step('clean up');
await admin('DELETE', `/api/devices/${dev.id}`);
for (const id of [prof.id, copy.id, other.id]) assert.equal((await admin('DELETE', `/api/profiles/${id}`)).status, 200);

console.log('\nผ่านทุกข้อ ✔');
process.exit(0);
