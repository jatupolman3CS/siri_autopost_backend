// Shared by the end-to-end tests of the cloud mode (test-cloud.mjs, test-schedule-flow.mjs): the real
// background.js with chrome.* and the Facebook page faked, and a small client of SIRIAUTOPOST.Api.
// Importing it starts the extension's service worker; the API address is the first argument of the script.
const API = (process.argv[2] || 'http://localhost:5100').replace(/\/+$/, '');
const ADMIN = { email: process.env.ADMIN_EMAIL || 'admin@autopost.local', password: process.env.ADMIN_PASSWORD || 'admin1234' };
const GROUP = { url: 'https://www.facebook.com/groups/plantlovers', name: 'คนรักต้นไม้ (ทดสอบ)' };
const BARE = { url: 'https://www.facebook.com/groups/bareaddress', name: '' }; // added by address only

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
  postUrl: 'https://www.facebook.com/groups/plantlovers/posts/555000111/', // where the new post "is" (the bump opens it)
  canSwitch: false, // a Page you manage opened in your own profile: Facebook offers "Switch now"
  switched: 0,      // times the extension acted as the Page
  commentOpen: false,
  commented: [],    // { text, files, url }: the comments (bumps) made
};
const contentHandlers = {
  check: () => ({ ok: true, title: `${GROUP.name} | Facebook`, login: !page.loggedIn, checkpoint: false, canSwitch: page.canSwitch }),
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
  switchProfile: () => ((page.switched += 1), (page.canSwitch = false), { ok: true, switched: true }),
  postUrl: () => ({ ok: true, url: page.postUrl }),
  openCommentBox: () => ((page.commentOpen = true), (page.text = ''), (page.attached = []), { ok: true }),
  commentState: () => ({ ok: true, open: page.commentOpen, empty: page.text === '', uploading: false, media: page.attached.length, blocked: false }),
  submitComment: () => {
    page.commented.push({ text: page.text, files: page.attached.map((f) => f.name), url: tabUrls.at(-1) });
    page.commentOpen = false;
    page.text = '';
    page.attached = [];
    return { ok: true };
  },
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

export function setToken(t) {
  token = t;
}

export {
  API, ADMIN, GROUP, BARE, data, local, listeners, alarms, page, tabUrls, bg, api, step, sleep, realSetTimeout,
  migrateSettings, newCampaign,
};
