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
  hang: false, // the tab never answers (a stuck page)
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
  check: () => (page.hang ? new Promise(() => {}) : { ok: true, title: `${GROUP.name} | Facebook`, login: !page.loggedIn, checkpoint: false, canSwitch: page.canSwitch }),
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

// The LINE Official Account page (chat.line.biz), as line.js would answer for it. A test sets the fields it needs:
// who is logged in, which customers have chats, how each conversation looks (who wrote last, when, unread), whether the OA's
// auto-reply covers the box, whether the box is dead. It never holds message text of a customer: only what the extension sent.
const lineUser = (n) => 'U' + String(n).padStart(32, '0'); // "U" + 32 characters, like a LINE user id
const LINE_BOT = 'Ubbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const linePage = {
  loggedIn: true,
  hang: false,         // the tab never answers
  botId: LINE_BOT,
  oaName: 'ร้านทดสอบ LINE',
  accounts: [],        // offered on the OA chooser when no OA is picked: [{ botId, name }]
  chooser: false,      // chat.line.biz/ (no OA in the address) shows the chooser instead of an OA
  chats: [],           // { userId, name, labels, lastMessageAt, unread, hidden } (hidden = not in the list until it is searched)
  listComplete: true,  // the scan reached the end of the list
  convo: {},           // userId -> { side: 'in' | 'out', time: 'HH:mm' | null, today: bool | null, unread: bool }
  autoReply: false,    // the OA's bot covers the box until "แชทแบบแมนนวล" is clicked
  composerDisabled: false,
  noComposer: false,
  cannotSend: false,
  blocked: false,      // a rate-limit / captcha notice is on the page
  failedCount: 0,      // red "send failed" markers in the conversation
  headerName: null,    // overrides the header (a wrong customer)
  open: null,          // userId of the open chat
  target: 'composer',  // what insert/newline/... type into: 'composer' or 'search'
  search: '',
  text: '',
  attached: [],
  sent: [],            // { userId, text, files }: what was sent, in order
  opened: [],          // every chat opened (clicked or by address), in order
  switched: [],        // chats put in manual mode
  manual: new Set(),
  failNextSend: false, // the next send leaves a red marker and the text in the box
  inserts: 0,          // keystroke-sized pieces typed into the box (a pasted text counts in setTexts)
  setTexts: 0,
};
const lineHost = (url) => {
  try {
    return new URL(url).hostname === 'chat.line.biz';
  } catch {
    return false;
  }
};
const lineChat = () => linePage.chats.find((c) => c.userId === linePage.open);
const lineConvo = (id) => ({ side: 'in', time: null, today: false, unread: false, ...(linePage.convo[id] || {}) });
const lineCovered = () => linePage.open != null && linePage.autoReply && !linePage.manual.has(linePage.open);
const lineEdit = (fn) => ((linePage.target === 'search' ? (linePage.search = fn(linePage.search)) : (linePage.text = fn(linePage.text))), { ok: true });
const lineAtChooser = () => {
  try {
    return linePage.chooser && new URL(tabUrls.at(-1)).pathname === '/';
  } catch {
    return false;
  }
};
const lineHandlers = {
  ping: () => ({ ok: true }),
  check: () =>
    linePage.hang
      ? new Promise(() => {})
      : {
          ok: true, title: 'LINE Official Account Manager', login: !linePage.loggedIn, checkpoint: false, canSwitch: false,
          botId: linePage.loggedIn && !lineAtChooser() ? linePage.botId : '', oaName: linePage.loggedIn && !lineAtChooser() ? linePage.oaName : '',
          chatOpen: linePage.open != null, ready: linePage.loggedIn && !lineAtChooser(), accounts: lineAtChooser() ? linePage.accounts.length : 0,
        },
  lineListAccounts: () => ({ ok: true, accounts: linePage.accounts }),
  lineListChats: () => ({
    ok: true, done: true, rounds: 1, note: '', complete: linePage.listComplete,
    count: linePage.chats.filter((c) => !c.hidden).length,
    chats: linePage.chats.filter((c) => !c.hidden).map(({ userId, name, labels, lastMessageAt, unread }) => ({ userId, name, labels: labels || [], lastMessageAt: lastMessageAt || null, unread: !!unread })),
  }),
  lineClickRow: (m) => {
    if (linePage.open === m.userId) return { ok: true, already: true, found: true, unread: false };
    const c = linePage.chats.find((x) => x.userId === m.userId);
    const shown = c && (!c.hidden || (!c.noSearch && linePage.search && c.name.includes(linePage.search))); // hidden: only a search finds it
    if (!shown) return { ok: true, notFound: true, found: false };
    const unread = lineConvo(c.userId).unread;
    if (m.click === false) return { ok: true, found: true, unread };
    linePage.open = c.userId;
    linePage.opened.push(c.userId);
    linePage.text = '';
    linePage.attached = [];
    if (linePage.convo[c.userId]) linePage.convo[c.userId].unread = false; // opening a chat reads it
    return { ok: true, found: true, unread };
  },
  lineFocusSearch: () => ((linePage.target = 'search'), (linePage.search = ''), { ok: true }),
  lineFocusComposer: () => ((linePage.target = 'composer'), { ok: true }),
  discard: (m) => {
    if (m.search) linePage.search = '';
    if (m.composer) {
      linePage.text = '';
      linePage.attached = [];
    }
    linePage.target = 'composer';
    return { ok: true };
  },
  lineChatState: () => {
    const id = linePage.open;
    const cv = id ? lineConvo(id) : lineConvo('-');
    const covered = lineCovered();
    const mine = linePage.sent.filter((s) => s.userId === id).length;
    return {
      ok: true, open: id != null, userId: id || '', headerName: id ? (linePage.headerName ?? lineChat()?.name ?? '') : '',
      composerFound: id != null && !linePage.noComposer, composerReady: id != null && !linePage.composerDisabled && !covered,
      composerEmpty: linePage.text === '', composerDisabled: linePage.composerDisabled, overlay: covered, manualButton: covered, autoReplyBar: covered,
      cannotSend: linePage.cannotSend, bubbleCount: 3 + mine, lastBubbleSide: cv.side, lastBubbleTime: cv.time, lastBubbleToday: cv.today,
      unreadInList: cv.unread, outgoingCount: 1 + mine, incomingCount: 2,
      sendEnabled: (linePage.text !== '' || linePage.attached.length > 0) && !linePage.composerDisabled, uploading: false,
      sendFailed: linePage.failedCount > 0, failedCount: linePage.failedCount, blocked: linePage.blocked,
    };
  },
  lineManualSwitch: () => {
    if (!lineCovered()) return { ok: true, clicked: false };
    linePage.manual.add(linePage.open);
    linePage.switched.push(linePage.open);
    return { ok: true, clicked: true };
  },
  lineSend: (m) => {
    if (m.expectUserId && m.expectUserId !== linePage.open) return { ok: false, error: 'แชทที่เปิดไม่ตรงกับลูกค้าในงาน ยกเลิกการส่ง' };
    if (lineCovered() || (linePage.text === '' && !linePage.attached.length)) return { ok: false, error: 'ปุ่ม "ส่ง" ยังกดไม่ได้' };
    if (linePage.failNextSend) {
      linePage.failNextSend = false;
      linePage.failedCount += 1;
      return { ok: true };
    }
    linePage.sent.push({ userId: linePage.open, text: linePage.text, files: linePage.attached.map((f) => f.name) });
    linePage.text = '';
    linePage.attached = [];
    return { ok: true };
  },
  insert: (m) => (linePage.target === 'composer' && linePage.inserts++, lineEdit((t) => t + m.text)),
  paste: (m) => lineEdit((t) => t + m.text),
  newline: () => lineEdit((t) => t + '\n'),
  backspace: () => lineEdit((t) => Array.from(t).slice(0, -1).join('')),
  setText: (m) => (linePage.target === 'composer' && linePage.setTexts++, lineEdit(() => m.text)),
  getText: () => ({ ok: true, open: true, text: linePage.target === 'search' ? linePage.search : linePage.text }),
  attachImages: async (m) => {
    const keys = m.imageIds.map((id) => (String(id).includes(':') ? id : 'img:' + id));
    linePage.attached = keys.map((k) => data.get(k)).filter(Boolean);
    return linePage.attached.length ? { ok: true, method: 'input' } : { ok: false, error: 'ไม่พบไฟล์รูปในที่เก็บข้อมูล' };
  },
  blockCheck: () => ({ ok: true, blocked: linePage.blocked, text: linePage.blocked ? 'Too many requests' : '' }),
  scroll: () => ({ ok: true }),
  scrollTop: () => ({ ok: true }),
  scrollBottom: () => ({ ok: true }),
};
// A navigation to a chat's address opens that chat (the fallback when a chat is not in the list).
function lineNavigated(url) {
  const m = /\/chat\/([A-Za-z0-9_-]{10,64})/.exec(String(url));
  if (!m || !lineHost(url)) return;
  linePage.open = m[1];
  linePage.opened.push(m[1]);
  linePage.text = '';
  linePage.attached = [];
}

const granted = new Set(); // host permissions the person gave (chrome.permissions.contains answers from it)
function grantPermission(origin) {
  granted.add(origin);
}

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
    get: async (id) => ({ id, windowId, url: tabUrls.at(-1) }),
    update: async (id, props = {}) => {
      if (props.url) {
        tabUrls.push(props.url);
        lineNavigated(props.url);
      }
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
      // The one worker tab shows whatever it was last sent to: chat.line.biz answers like line.js, anything else like content.js.
      const h = (lineHost(tabUrls.at(-1)) ? lineHandlers : contentHandlers)[msg.cmd];
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
  permissions: { contains: async (q) => (q.origins || []).every((o) => granted.has(o) || granted.has('<all_urls>')) },
};

// Waits are real in background.js (it imitates a person); shorten them for the test.
const realSetTimeout = globalThis.setTimeout;
globalThis.setTimeout = (fn, ms, ...a) => realSetTimeout(fn, (ms ?? 0) >= 100000 ? ms : Math.min(ms ?? 0, 50), ...a); // keeps request timeouts

const { migrateSettings, newCampaign } = await import('../lib/shared.js');
const background = await import('../background.js');

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
  migrateSettings, newCampaign, background, linePage, lineUser, LINE_BOT, grantPermission,
};
