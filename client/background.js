// Background service worker: schedules every campaign with its own
// chrome.alarms entry and drives the human-like posting flow step by step in
// one dedicated window (one post at a time across all campaigns).
import {
  TYPING_SPEEDS,
  rand,
  randInt,
  humanRand,
  chance,
  shuffle,
  pick,
  normalizeGroupUrl,
  usablePosts,
  postMediaIds,
  campaignImageIds,
  postsForGroup,
  isWithinActive,
  nextActiveStart,
  fmtDateTime,
  fmtDuration,
  migrateSettings,
  activeGroups,
  DEFAULT_CONFIG,
  composeText,
  splitPageTags,
  replaceInPosts,
  campaignProblem,
} from './lib/shared.js';
import { escHtml, sendText, sendPhoto, findChats, dataUrlToBlob } from './lib/telegram.js';
import { parseBackup, applyImport, summarize, hasContent, imageIdsOf, BUNDLED_CONFIG_PATH } from './lib/backup.js';

const ALARM_PREFIX = 'fbap:';
const MAX_LOGS = 400;

const DEFAULT_STATE = {
  running: false,
  testing: false,
  current: null,      // { campaignId, url } being posted right now
  lastPostAt: 0,      // any campaign
  tabId: null,
  windowId: null,
  campaigns: {},      // campaignId -> campaign state (see DEFAULT_CSTATE)
  groupNames: {},     // group url -> group name seen on Facebook
  groupLastPostAt: {},// group url -> time of the last successful post there
  groupPostTimes: {}, // group url -> times of the posts there in the last 24 hours
  daily: { date: '', count: 0, cap: 0, base: 0 }, // today's posts vs. daily limit
  failStreak: 0,      // failed posts in a row (any campaign)
  pausedUntil: 0,     // everything waits until then (Facebook warning etc.)
  pauseReason: '',
};

const DEFAULT_CSTATE = {
  round: 0,
  queue: [],          // group urls of the current round (already shuffled)
  pos: 0,             // index of the next group in queue
  doneInRound: 0,
  nextAt: null,
  nextKind: null,     // 'group' | 'round' | 'active' | 'wait' | 'posting'
  busy: false,        // a post of this campaign is in progress
  finished: false,    // reached max rounds / nothing to do
  postSeqBy: {},      // sequence counters ('*' or group url)
  lastPostBy: {},     // group url -> last post id used there
  lastPostId: null,
  recentBy: {},       // group url -> recent post ids used there (oldest first)
  recentAll: [],      // post ids of the campaign's latest posts, any group
  stats: { ok: 0, fail: 0, skip: 0 },
  roundLog: [],       // [{ u: url, n: name, s: 'ok'|'fail'|'skip', e: error }]
};

class Aborted extends Error {}
// Problems that hit every group (login, checkpoint): stop instead of skipping.
class Fatal extends Error {}
// Facebook shows a "blocked / slow down / restricted" notice: pause everything.
class Blocked extends Error {}

let posting = false;      // in-memory guard: one post at a time
let abortFlag = false;    // set by stop()
let keepAliveTimer = null;

// ---------- storage ----------

let chain = Promise.resolve();
function serial(fn) {
  const p = chain.then(fn, fn);
  chain = p.catch(() => {});
  return p;
}

// Ticks run one after another so two campaigns never post at the same time.
let tickChain = Promise.resolve();
function enqueue(fn) {
  const p = tickChain.then(fn, fn);
  tickChain = p.catch((e) => console.error('[FBAP]', e));
  return p;
}

async function getSettings() {
  const { settings } = await chrome.storage.local.get('settings');
  const migrated = migrateSettings(settings);
  // Persist an old (version 1) shape once so campaign ids stay stable.
  if (settings && !Array.isArray(settings.campaigns)) await chrome.storage.local.set({ settings: migrated });
  return migrated;
}

async function getState() {
  const { state } = await chrome.storage.local.get('state');
  return { ...DEFAULT_STATE, ...(state || {}), campaigns: { ...((state && state.campaigns) || {}) } };
}

function setState(patch) {
  return serial(async () => {
    const next = { ...(await getState()), ...patch };
    await chrome.storage.local.set({ state: next });
    return next;
  });
}

const cstate = (state, cid) => ({ ...DEFAULT_CSTATE, ...(state.campaigns[cid] || {}) });

// Updates one campaign's state; patch may be a function of the old value.
function setC(cid, patch) {
  return serial(async () => {
    const state = await getState();
    const old = cstate(state, cid);
    const next = { ...old, ...(typeof patch === 'function' ? patch(old) : patch) };
    state.campaigns[cid] = next;
    await chrome.storage.local.set({ state });
    return next;
  });
}

function removeC(cid) {
  return serial(async () => {
    const state = await getState();
    delete state.campaigns[cid];
    await chrome.storage.local.set({ state });
  });
}

function log(level, msg, camp = null) {
  const text = camp ? `[${camp.name}] ${msg}` : msg;
  console.log(`[FBAP] ${level}: ${text}`);
  return serial(async () => {
    const { logs = [] } = await chrome.storage.local.get('logs');
    logs.push({ t: Date.now(), level, msg: text });
    if (logs.length > MAX_LOGS) logs.splice(0, logs.length - MAX_LOGS);
    await chrome.storage.local.set({ logs });
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function keepAlive(on) {
  if (on && !keepAliveTimer) {
    keepAliveTimer = setInterval(() => chrome.runtime.getPlatformInfo().catch(() => {}), 20000);
  } else if (!on && keepAliveTimer) {
    clearInterval(keepAliveTimer);
    keepAliveTimer = null;
  }
}

// ---------- scheduling ----------

const alarmName = (cid) => ALARM_PREFIX + cid;

async function scheduleC(cid, at, kind) {
  await chrome.alarms.create(alarmName(cid), { when: Math.max(at, Date.now() + 1000) });
  await setC(cid, { nextAt: at, nextKind: kind });
}

async function clearAlarms() {
  for (const a of await chrome.alarms.getAll()) {
    if (a.name.startsWith(ALARM_PREFIX)) await chrome.alarms.clear(a.name);
  }
}

// Which post each group got last / sequence position. Kept across Stop/Start
// so a group never gets the same post again right after a restart.
const postMemory = (o) => ({
  postSeqBy: (o && o.postSeqBy) || {},
  lastPostBy: (o && o.lastPostBy) || {},
  lastPostId: (o && o.lastPostId) || null,
  recentBy: (o && o.recentBy) || {},
  recentAll: (o && o.recentAll) || [],
});

// ---------- anti-block: daily limit, group cooldown, pauses ----------

const dayKey = (d = new Date()) => `${d.getFullYear()}-${d.getMonth() + 1}-${d.getDate()}`;

// Today's counter. The limit varies a little each day (85-100% of the setting).
function todayCounter(global) {
  return serial(async () => {
    const s = await getState();
    const d = s.daily || {};
    if (d.date === dayKey() && d.base === global.dailyMaxPosts) return d;
    const base = global.dailyMaxPosts || 0;
    const cap = base > 0 ? Math.max(1, Math.round(base * rand(0.85, 1))) : 0;
    const daily = { date: dayKey(), count: d.date === dayKey() ? d.count || 0 : 0, cap, base };
    s.daily = daily;
    await chrome.storage.local.set({ state: s });
    return daily;
  });
}

// Group's own limit (its rule, e.g. 1 post a day): posts in the last 24 hours.
const DAY_MS = 24 * 3600000;
const postsIn24h = (state, url) => ((state.groupPostTimes || {})[url] || []).filter((t) => Date.now() - t < DAY_MS);
function pruneGroupTimes(times) {
  const out = {};
  for (const [url, list] of Object.entries(times || {})) {
    const recent = (Array.isArray(list) ? list : []).filter((t) => Date.now() - t < DAY_MS);
    if (recent.length) out[url] = recent;
  }
  return out;
}
// The strictest limit set for this group link in any campaign (0 = none).
function groupLimit(settings, url) {
  const limits = settings.campaigns.flatMap((c) => c.groups.filter((g) => normalizeGroupUrl(g.url) === url).map((g) => g.dailyMax || 0));
  const set = limits.filter((n) => n > 0);
  return set.length ? Math.min(...set) : 0;
}
// When the group may be posted again under its limit (0 = now).
function groupLimitUntil(settings, state, url) {
  const max = groupLimit(settings, url);
  if (!max) return 0;
  const recent = postsIn24h(state, url).sort((a, b) => a - b);
  return recent.length < max ? 0 : recent[recent.length - max] + DAY_MS;
}

// A post went out: count it for today and for the group's cooldown.
function countPost(url) {
  return serial(async () => {
    const s = await getState();
    const d = s.daily && s.daily.date === dayKey() ? s.daily : { date: dayKey(), count: 0, cap: 0, base: -1 };
    s.daily = { ...d, count: (d.count || 0) + 1 };
    s.groupLastPostAt = { ...(s.groupLastPostAt || {}), [url]: Date.now() };
    s.groupPostTimes = { ...pruneGroupTimes(s.groupPostTimes), [url]: [...postsIn24h(s, url), Date.now()] };
    s.failStreak = 0;
    await chrome.storage.local.set({ state: s });
  });
}

function countFail() {
  return serial(async () => {
    const s = await getState();
    s.failStreak = (s.failStreak || 0) + 1;
    await chrome.storage.local.set({ state: s });
    return s.failStreak;
  });
}

// Next morning: the campaign's active-hours start (or 08:00) + a few random minutes.
function nextDayStart(cfg) {
  const d = new Date();
  d.setDate(d.getDate() + 1);
  const [h, m] = String(cfg.activeHoursEnabled ? cfg.activeStart : '08:00').split(':').map(Number);
  d.setHours(h || 0, m || 0, 0, 0);
  return d.getTime() + randInt(5, 45) * 60000;
}

// A campaign that is really running: enabled, valid, launched, not finished.
// (Start skips invalid campaigns; pauses and restarts must not revive them.)
function isScheduled(c, cs) {
  return c.enabled && !campaignProblem(c) && !cs.finished && !!cs.nextKind;
}

// Pauses every running campaign for a random time, then they resume by
// themselves. A campaign already waiting longer (next round, tomorrow) keeps
// its own later time.
async function pauseAll(hoursMin, hoursMax, reason, { shot = null, detail = '' } = {}) {
  const settings = await getSettings();
  const until = Date.now() + humanRand(hoursMin, Math.max(hoursMin, hoursMax)) * 3600000;
  await setState({ pausedUntil: until, pausedAt: Date.now(), pauseReason: reason, failStreak: 0 });
  const state = await getState();
  for (const c of settings.campaigns) {
    const cs = cstate(state, c.id);
    if (!isScheduled(c, cs)) continue;
    if (cs.nextAt && cs.nextAt > until) {
      await scheduleC(c.id, cs.nextAt, cs.nextKind);
      continue;
    }
    await scheduleC(c.id, until + randInt(0, 20) * 60000, 'paused');
  }
  await log('warn', `พักทุกชุดถึง ${fmtDateTime(until)}: ${reason}${detail ? ` (${detail})` : ''}`);
  await notify(
    'alert',
    `⏸ <b>พักอัตโนมัติ</b> ถึง ${fmtDateTime(until)} แล้วทำต่อเอง\nสาเหตุ: ${escHtml(reason)}${detail ? `\n${escHtml(detail)}` : ''}`,
    shot
  );
}

// Fresh campaign state, first tick after a random short stagger.
async function launchCampaign(camp, delayMs) {
  await setC(camp.id, (o) => ({ ...DEFAULT_CSTATE, ...postMemory(o) }));
  await scheduleC(camp.id, Date.now() + delayMs, 'group');
}

// ---------- notifications ----------

let warnedNoCapture = false;

const shortUrl = (url) => String(url).replace('https://www.facebook.com/groups/', 'groups/').replace(/\/$/, '');

// "(3) ชื่อกลุ่ม | Facebook" -> "ชื่อกลุ่ม"
function cleanTitle(title) {
  return String(title || '')
    .replace(/^\(\d+\+?\)\s*/, '')
    .replace(/\s*[|·]\s*Facebook\s*$/i, '')
    .trim();
}

function groupLine(name, url) {
  return name ? `<b>${escHtml(name)}</b>\n${escHtml(url)}` : escHtml(url);
}

function rememberGroupName(url, name) {
  return serial(async () => {
    const s = await getState();
    s.groupNames = { ...(s.groupNames || {}), [url]: name };
    await chrome.storage.local.set({ state: s });
  });
}

// Sends a Telegram message when that kind of notification is switched on.
// kind: 'success' | 'fail' | 'summary' | 'system' | 'alert'
async function notify(kind, html, shot = null) {
  const { global } = await getSettings();
  const t = global.telegram;
  if (!t.enabled || !t.botToken || !t.chatId) return;
  const wanted = {
    success: t.onSuccess,
    fail: t.onFail,
    summary: t.roundSummary,
    system: t.onStartStop,
    alert: true,
  }[kind];
  if (!wanted) return;
  try {
    if (shot && t.screenshot) await sendPhoto(t.botToken, t.chatId, await dataUrlToBlob(shot), html);
    else await sendText(t.botToken, t.chatId, html);
  } catch (e) {
    await log('warn', `ส่ง Telegram ไม่สำเร็จ: ${e?.message || e}`);
  }
}

// Screenshot of the posting window, or null when disabled / not possible.
async function captureWorker() {
  const { global } = await getSettings();
  const t = global.telegram;
  if (!t.enabled || !t.screenshot) return null;
  if (!(await chrome.permissions.contains({ origins: ['<all_urls>'] }))) {
    if (!warnedNoCapture) {
      warnedNoCapture = true;
      await log('warn', 'ยังไม่ได้อนุญาตจับภาพหน้าจอ (กด "อนุญาตจับภาพหน้าจอ" ในหน้าตั้งค่า) จะส่ง Telegram เป็นข้อความอย่างเดียว');
    }
    return null;
  }
  const st = await getState();
  if (st.windowId == null) return null;
  try {
    return await chrome.tabs.captureVisibleTab(st.windowId, { format: 'jpeg', quality: 70 });
  } catch (e) {
    await log('warn', `จับภาพหน้าจอไม่ได้: ${e?.message || e}`);
    return null;
  }
}

async function sendRoundSummary(camp, cst, next) {
  const items = cst.roundLog || [];
  const ok = items.filter((x) => x.s === 'ok');
  const fail = items.filter((x) => x.s === 'fail');
  const skip = items.filter((x) => x.s === 'skip');
  const line = (x) => `• ${escHtml(x.n || shortUrl(x.u))}${x.e ? ` — ${escHtml(x.e)}` : ''}`;
  const parts = [`📊 <b>สรุปรอบที่ ${cst.round}</b> · ชุด ${escHtml(camp.name)}`];
  parts.push(`✅ สำเร็จ ${ok.length} กลุ่ม${ok.length ? '\n' + ok.map(line).join('\n') : ''}`);
  if (fail.length) parts.push(`❌ ไม่สำเร็จ ${fail.length} กลุ่ม\n${fail.map(line).join('\n')}`);
  if (skip.length) parts.push(`⏭ ข้าม ${skip.length} กลุ่ม\n${skip.map((x) => line({ ...x, e: x.e || 'สุ่มข้าม' })).join('\n')}`);
  parts.push(next.finishReason ? `🏁 ชุดนี้หยุด: ${escHtml(next.finishReason)}` : `🔄 รอบถัดไป: ${fmtDateTime(next.at)}`);
  await notify('summary', parts.join('\n\n'));
}

// ---------- control ----------

async function start() {
  if (posting) return { ok: false, error: 'กำลังโพสต์อยู่ รอให้เสร็จหรือกดหยุดก่อน' };
  const settings = await getSettings();
  const enabled = settings.campaigns.filter((c) => c.enabled);
  if (!enabled.length) return { ok: false, error: 'ยังไม่มีชุดโพสต์ที่เปิดใช้' };
  const ready = enabled.filter((c) => !campaignProblem(c));
  if (!ready.length) {
    const c = enabled[0];
    return { ok: false, error: `${c.name}: ${campaignProblem(c)}` };
  }
  abortFlag = false;
  warnedNoCapture = false;
  await clearAlarms();
  const old = await getState();
  const campaigns = {};
  for (const [cid, o] of Object.entries(old.campaigns)) campaigns[cid] = postMemory(o);
  // Pressing Start ends an automatic pause.
  await setState({ running: true, current: null, campaigns, startedAt: Date.now(), pausedUntil: 0, pauseReason: '', failStreak: 0 });
  await log('info', `เริ่มทำงาน (${ready.length} ชุด)`);
  for (const c of enabled) {
    if (campaignProblem(c)) await log('warn', `ข้ามชุดนี้: ${campaignProblem(c)}`, c);
  }
  // First campaign starts right away, the others after a random stagger.
  let delay = 2000;
  for (const c of shuffle(ready)) {
    await launchCampaign(c, delay);
    delay += randInt(40, 150) * 1000;
  }
  const list = ready.map((c) => `• ${escHtml(c.name)} (${activeGroups(c).length} กลุ่ม)`).join('\n');
  await notify('system', `▶️ <b>เริ่มทำงาน</b> ${ready.length} ชุด\n${list}`);
  return { ok: true };
}

async function stop(reason = 'ผู้ใช้กดหยุด', { silent = false } = {}) {
  abortFlag = true;
  await clearAlarms();
  const state = await getState();
  for (const cid of Object.keys(state.campaigns)) {
    state.campaigns[cid] = { ...cstate(state, cid), nextAt: null, nextKind: null };
  }
  await setState({ running: false, campaigns: state.campaigns });
  await log('warn', `หยุดทำงาน: ${reason}`);
  if (!silent) await notify('system', `⏹ <b>หยุดทำงาน</b>: ${escHtml(reason)}`);
  return { ok: true };
}

async function runNow(cid) {
  const state = await getState();
  if (!state.running) return { ok: false, error: 'ยังไม่ได้กดเริ่มทำงาน' };
  const settings = await getSettings();
  const camp = settings.campaigns.find((c) => c.id === cid);
  if (!camp || !camp.enabled) return { ok: false, error: 'ชุดนี้ปิดอยู่' };
  if (cstate(state, cid).finished) return { ok: false, error: 'ชุดนี้ครบรอบแล้ว (ปิดแล้วเปิดชุดใหม่เพื่อเริ่มใหม่)' };
  await chrome.alarms.clear(alarmName(cid));
  await setC(cid, { nextAt: null, nextKind: 'wait' });
  const clickedAt = Date.now();
  enqueue(() => tick(cid, { manual: true, clickedAt }));
  return { ok: true, queued: posting };
}

async function finishCampaign(camp, reason, { quiet = false } = {}) {
  await chrome.alarms.clear(alarmName(camp.id));
  await setC(camp.id, { finished: true, nextAt: null, nextKind: null });
  await log('info', `ชุดนี้หยุด: ${reason}`, camp);
  if (!quiet) await notify('system', `🏁 ชุด ${escHtml(camp.name)} หยุด: ${escHtml(reason)}`);
  const settings = await getSettings();
  const state = await getState();
  const left = settings.campaigns.filter((c) => c.enabled && !cstate(state, c.id).finished);
  if (!left.length) await stop('ทุกชุดทำงานครบแล้ว');
}

async function tick(cid, { manual = false, clickedAt = 0 } = {}) {
  let state = await getState();
  if (!state.running) return;
  const settings = await getSettings();
  const camp = settings.campaigns.find((c) => c.id === cid);
  if (!camp || !camp.enabled) {
    await chrome.alarms.clear(alarmName(cid));
    return;
  }
  const cfg = camp.config;
  let cs = cstate(state, cid);
  if (cs.finished) return;
  // Stale wake-up (e.g. watchdog) while a newer alarm is already set.
  if (!manual && cs.nextAt && cs.nextAt - Date.now() > 5000) return;

  if (posting) {
    // A test post is running: come back a bit later.
    await scheduleC(cid, Date.now() + randInt(45, 120) * 1000, 'wait');
    return;
  }

  // Automatic pause (Facebook warning, many failures): wait it out. "Post now"
  // may skip a pause the user could already see, never one that began after
  // the click (e.g. a block notice in the post that was running).
  if (state.pausedUntil && Date.now() < state.pausedUntil) {
    const userSawPause = manual && (state.pausedAt || 0) < clickedAt;
    if (!userSawPause) {
      if (manual) await log('warn', 'ยกเลิก "โพสต์ทันที" เพราะระบบเพิ่งพักอัตโนมัติ', camp);
      await scheduleC(cid, state.pausedUntil + randInt(0, 20) * 60000, 'paused');
      return;
    }
  }

  if (cs.busy) {
    await log('warn', `การโพสต์ก่อนหน้าถูกขัดจังหวะ ข้ามไปกลุ่มถัดไป`, camp);
    cs = await setC(cid, (o) => ({
      busy: false,
      pos: o.pos + 1,
      stats: { ...o.stats, fail: o.stats.fail + 1 },
      roundLog: [...(o.roundLog || []), { u: o.queue[o.pos], n: '', s: 'fail', e: 'ถูกขัดจังหวะ' }],
    }));
  }

  // Keep a human gap between any two posts, whatever campaign they belong to.
  const gap = (settings.global.minGapMin || 0) * 60000;
  if (!manual && state.lastPostAt && Date.now() - state.lastPostAt < gap) {
    const at = state.lastPostAt + gap + randInt(10, 120) * 1000;
    await scheduleC(cid, at, 'wait');
    return;
  }

  const now = new Date();
  if (cfg.activeHoursEnabled && !isWithinActive(now, cfg.activeStart, cfg.activeEnd)) {
    const at = nextActiveStart(now, cfg.activeStart, cfg.activeEnd) + randInt(1, 15) * 60000;
    await scheduleC(cid, at, 'active');
    await log('info', `นอกช่วงเวลาทำงาน รอถึง ${fmtDateTime(at)}`, camp);
    return;
  }

  // Daily limit for all campaigns together.
  if (!manual && settings.global.dailyMaxPosts > 0) {
    const daily = await todayCounter(settings.global);
    if (daily.count >= daily.cap) {
      const at = nextDayStart(cfg);
      await scheduleC(cid, at, 'daily');
      await log('info', `ครบโควตาวันนี้ (${daily.count}/${daily.cap} โพสต์) รอถึง ${fmtDateTime(at)}`, camp);
      return;
    }
  }

  const cooldownMs = (cfg.groupCooldownHours || 0) * 3600000;
  const lastPostIn = (u) => (state.groupLastPostAt || {})[u] || 0;
  const cooling = (u) => cooldownMs > 0 && Date.now() - lastPostIn(u) < cooldownMs;
  // The group's own rule (posts per 24 hours): applies to "post now" too.
  const limitUntil = (u) => groupLimitUntil(settings, state, u);
  const full = (u) => limitUntil(u) > Date.now();

  const groups = activeGroups(camp);
  if (cs.pos >= cs.queue.length) {
    if (cfg.maxRounds > 0 && cs.round >= cfg.maxRounds) {
      await finishCampaign(camp, `ครบ ${cfg.maxRounds} รอบแล้ว`);
      return;
    }
    if (!groups.length) {
      await finishCampaign(camp, 'ไม่มีลิงก์กลุ่มที่เปิดใช้');
      return;
    }
    // Every group still resting (posted too recently) or at its own limit:
    // start when one is ready.
    const readyAt = (u) => Math.max(!manual && cooling(u) ? lastPostIn(u) + cooldownMs : 0, limitUntil(u));
    if (groups.every((g) => readyAt(g.url) > Date.now())) {
      const at = Math.min(...groups.map((g) => readyAt(g.url))) + randInt(1, 15) * 60000;
      await scheduleC(cid, at, 'round');
      const why = groups.every((g) => full(g.url)) ? 'ทุกกลุ่มโพสต์ครบจำนวนต่อวันของกลุ่มแล้ว' : `ทุกกลุ่มยังอยู่ในช่วงพักกลุ่ม (${cfg.groupCooldownHours} ชม.) หรือครบจำนวนต่อวันของกลุ่ม`;
      await log('info', `${why} รอถึง ${fmtDateTime(at)}`, camp);
      return;
    }
    // A round always holds every group; resting groups are posted later in it.
    const urls = groups.map((g) => g.url);
    const queue = cfg.shuffleGroups ? shuffle(urls) : urls;
    cs = await setC(cid, (o) => ({
      round: o.round + 1,
      queue,
      pos: 0,
      doneInRound: 0,
      roundLog: [],
      roundStartedAt: Date.now(),
    }));
    await log('info', `เริ่มรอบที่ ${cs.round} (${queue.length} กลุ่ม${cfg.shuffleGroups ? ', สุ่มลำดับ' : ''})`, camp);
  }

  // The next group already had its posts for today (its own rule): skip it in
  // this round, it comes back in a later round.
  if (cs.queue[cs.pos] && full(cs.queue[cs.pos])) {
    const u = cs.queue[cs.pos];
    const g = groups.find((x) => x.url === u);
    const n = g ? g.name || (state.groupNames || {})[u] || '' : '';
    const why = `ครบ ${groupLimit(settings, u)} โพสต์ใน 24 ชม. ตามกฎกลุ่ม โพสต์ได้อีก ${fmtDateTime(limitUntil(u))}`;
    await log('info', `ข้ามกลุ่มนี้ในรอบนี้ (${why}): ${n ? n + ' ' : ''}${u}`, camp);
    cs = await setC(cid, (o) => ({
      pos: o.pos + 1,
      stats: { ...o.stats, skip: (o.stats.skip || 0) + 1 },
      roundLog: [...(o.roundLog || []), { u, n, s: 'skip', e: why }],
    }));
    await afterGroup(camp, cs, 'skipped');
    return;
  }

  // The next group is still resting: take a rested group from later in this
  // round instead, or wait until the first remaining group may be posted.
  if (!manual && cooling(cs.queue[cs.pos])) {
    const rest = cs.queue.slice(cs.pos);
    const j = rest.findIndex((u) => !cooling(u) && !full(u));
    if (j > 0) {
      const queue = [...cs.queue];
      [queue[cs.pos], queue[cs.pos + j]] = [queue[cs.pos + j], queue[cs.pos]];
      cs = await setC(cid, { queue });
    } else {
      const at = Math.min(...rest.filter((u) => !full(u)).map((u) => lastPostIn(u) + cooldownMs)) + randInt(1, 15) * 60000;
      await scheduleC(cid, at, 'cooldown');
      await log('info', `กลุ่มที่เหลือในรอบนี้ยังพักไม่ครบ ${cfg.groupCooldownHours} ชม. รอถึง ${fmtDateTime(at)}`, camp);
      return;
    }
  }

  const url = cs.queue[cs.pos];
  const group = groups.find((g) => g.url === url);
  if (!group) {
    await log('info', `กลุ่มถูกลบ/ปิดไปแล้ว ข้าม: ${url}`, camp);
    cs = await setC(cid, (o) => ({ pos: o.pos + 1 }));
    await afterGroup(camp, cs, 'skipped');
    return;
  }

  const known = group.name || (state.groupNames || {})[url] || '';
  const randomSkip = cfg.skipChancePct > 0 && chance(cfg.skipChancePct);
  const noPost = !postsForGroup(camp, url).length;
  if (randomSkip || noPost) {
    // Skip without opening Facebook.
    const why = noPost ? 'ไม่มีโพสต์ที่ตั้งให้ใช้กับกลุ่มนี้' : '';
    await log(noPost ? 'warn' : 'info', `${why ? why + ' ข้าม' : 'สุ่มข้ามกลุ่มนี้ในรอบนี้'}: ${known ? known + ' ' : ''}${url}`, camp);
    cs = await setC(cid, (o) => ({
      pos: o.pos + 1,
      stats: { ...o.stats, skip: (o.stats.skip || 0) + 1 },
      roundLog: [...(o.roundLog || []), { u: url, n: known, s: 'skip', e: why }],
    }));
    await afterGroup(camp, cs, 'skipped');
    return;
  }

  posting = true;
  abortFlag = false;
  keepAlive(true);
  await setState({ current: { campaignId: cid, url } });
  await setC(cid, { busy: true, nextAt: null, nextKind: 'posting' });
  // Watchdog: if the worker dies mid-post this alarm resumes the schedule.
  await chrome.alarms.create(alarmName(cid), { when: Date.now() + 15 * 60000 });
  let result;
  try {
    const post = await choosePost(camp, group);
    await log('info', `กำลังโพสต์ (${cs.pos + 1}/${cs.queue.length}) โพสต์ #${post.index}${group.text ? ' +ข้อความกลุ่ม' : ''}: ${url}`, camp);
    result = await postToGroup(url, post, cfg, settings.global);
  } catch (e) {
    result = {
      ok: false,
      error: e?.message || String(e),
      aborted: e instanceof Aborted,
      fatal: e instanceof Fatal,
      blocked: e instanceof Blocked,
      shot: e?.shot || null,
      groupName: e?.groupName || '',
    };
  } finally {
    posting = false;
    keepAlive(false);
  }
  if (result.ok) await countPost(url);

  const name = result.groupName || known;
  if (result.groupName) await rememberGroupName(url, result.groupName);
  if (result.ok) await log('success', `โพสต์สำเร็จ: ${name ? name + ' ' : ''}${url}`, camp);
  else if (result.aborted) await log('warn', `ยกเลิกโพสต์: ${url}`, camp);
  else {
    const what = result.fatal
      ? 'โพสต์ไม่สำเร็จ หยุดระบบ'
      : result.blocked
        ? 'Facebook แจ้งเตือน พักระบบ'
        : 'โพสต์ไม่สำเร็จ ข้ามกลุ่มนี้';
    await log('error', `${what}: ${name ? name + ' ' : ''}${url} - ${result.error}`, camp);
  }

  // Only real posts count for the gap between posts.
  state = await setState(result.ok ? { current: null, lastPostAt: Date.now() } : { current: null });
  const outcome = result.ok ? 'ok' : result.aborted ? 'aborted' : 'fail';
  cs = await setC(cid, (o) => ({
    busy: false,
    pos: o.pos + 1,
    doneInRound: o.doneInRound + 1,
    stats: {
      ...o.stats,
      ok: o.stats.ok + (outcome === 'ok' ? 1 : 0),
      fail: o.stats.fail + (outcome === 'fail' ? 1 : 0),
    },
    roundLog:
      outcome === 'aborted'
        ? o.roundLog || []
        : [...(o.roundLog || []), { u: url, n: name, s: outcome, e: result.ok ? '' : result.error }],
  }));
  const where = `ชุด ${escHtml(camp.name)} · รอบ ${cs.round} (กลุ่ม ${cs.pos}/${cs.queue.length})`;

  if (result.fatal) {
    // Login / checkpoint problems hit every group: stop instead of failing them all.
    await notify('alert', `⚠️ <b>ระบบหยุดอัตโนมัติ</b>\n${escHtml(result.error)}\n${groupLine(name, url)}\n${where}`, result.shot);
    await stop(`${camp.name}: ${result.error}`, { silent: true });
    return;
  }
  if (!state.running) return;

  // Should everything pause? (Facebook warning before or right after the post,
  // or too many failures in a row.)
  const g = settings.global;
  let pause = null;
  if (result.blocked || result.blockedAfter) {
    pause = {
      min: g.blockPauseHoursMin,
      max: g.blockPauseHoursMax,
      reason: result.blockedAfter ? 'Facebook แจ้งเตือนหลังโพสต์' : 'Facebook แจ้งเตือน/จำกัดการโพสต์',
      opts: { shot: result.shot, detail: `${name || url}: ${result.blockedAfter || result.error}` },
    };
  } else if (outcome === 'fail' && g.failStreakPause > 0) {
    const streak = await countFail();
    if (streak >= g.failStreakPause) pause = { min: 2, max: 4, reason: `โพสต์ไม่สำเร็จติดกัน ${streak} ครั้ง`, opts: {} };
  }

  // Normal bookkeeping first (next time, round summary), then the pause pushes
  // the next time back if it is earlier than the pause end.
  await afterGroup(camp, cs, outcome === 'ok' ? 'posted' : 'failed', async (next) => {
    const nextLine = next.finishReason
      ? ''
      : `\n⏭ ${next.roundEnded ? 'รอบถัดไป' : 'กลุ่มถัดไป'}: ${fmtDateTime(next.at)}`;
    if (result.ok) {
      await notify('success', `✅ <b>โพสต์สำเร็จ</b>\n${groupLine(name, url)}\n${where}\n🕒 ${fmtDateTime(Date.now())}${nextLine}`, result.shot);
    } else if (!result.aborted && !result.blocked) {
      await notify('fail', `❌ <b>โพสต์ไม่สำเร็จ ข้ามกลุ่มนี้</b>\n${groupLine(name, url)}\nสาเหตุ: ${escHtml(result.error)}\n${where}${nextLine}`, result.shot);
    }
  });
  if (pause && (await getState()).running) await pauseAll(pause.min, pause.max, pause.reason, pause.opts);
}

// Schedules the next step of a campaign.
// outcome: 'posted' | 'failed' | 'skipped'. Returns { at, roundEnded, finishReason }.
async function scheduleAfterGroup(camp, cs, outcome) {
  const cfg = camp.config;
  if (cs.pos < cs.queue.length) {
    let ms;
    let note = '';
    if (outcome === 'posted') {
      ms = humanRand(cfg.groupDelayMin, cfg.groupDelayMax) * 60000;
      // Random long break, on average once every N posts.
      if (cfg.longBreakEvery > 0 && Math.random() < 1 / cfg.longBreakEvery) {
        ms += humanRand(cfg.longBreakMin, cfg.longBreakMax) * 60000;
        note = ' (สุ่มพักยาว)';
      }
    } else if (outcome === 'failed') {
      // Nothing was posted: move on to the next group soon.
      ms = randInt(30, 90) * 1000;
      note = ' (ข้ามกลุ่มที่โพสต์ไม่ได้)';
    } else {
      ms = randInt(20, 60) * 1000;
    }
    const at = Date.now() + ms;
    await scheduleC(camp.id, at, 'group');
    await log('info', `กลุ่มถัดไปเวลา ${fmtDateTime(at)} (อีก ${fmtDuration(ms)})${note}`, camp);
    return { at, roundEnded: false };
  }
  if (cfg.maxRounds > 0 && cs.round >= cfg.maxRounds) {
    return { at: null, roundEnded: true, finishReason: `ครบ ${cfg.maxRounds} รอบแล้ว` };
  }
  const ms = cfg.roundIntervalHours * 3600000 + rand(0, cfg.roundJitterMin || 0) * 60000;
  const at = Date.now() + ms;
  await scheduleC(camp.id, at, 'round');
  await log('info', `จบรอบที่ ${cs.round} รอบถัดไปเวลา ${fmtDateTime(at)} (อีก ${fmtDuration(ms)})`, camp);
  return { at, roundEnded: true };
}

// Schedule, then send the per-group notice, the round summary, and finish if done.
async function afterGroup(camp, cs, outcome, groupNotice = null) {
  const next = await scheduleAfterGroup(camp, cs, outcome);
  if (groupNotice) await groupNotice(next);
  if (next.roundEnded) await sendRoundSummary(camp, cs, next);
  if (next.finishReason) await finishCampaign(camp, next.finishReason, { quiet: true });
}

// Picks the post for one group from the posts allowed for that group.
async function choosePost(camp, group, postId = null) {
  const all = usablePosts(camp.posts);
  // A post picked by hand (test post) may be any post of the campaign.
  const forced = postId ? all.find((p) => p.id === postId) : null;
  const posts = postsForGroup(camp, group.url);
  if (!forced && !posts.length) throw new Error('ไม่มีโพสต์ที่ตั้งให้ใช้กับกลุ่มนี้');
  const cs = cstate(await getState(), camp.id);
  const seqBy = { ...(cs.postSeqBy || {}) };
  const lastBy = { ...(cs.lastPostBy || {}) };
  const recentBy = { ...(cs.recentBy || {}) };
  let post = forced;
  if (!post) {
    if (camp.config.postMode === 'sequence') {
      // Shared counter when every post fits every group, else one per group.
      const key = posts.length === all.length ? '*' : group.url;
      const i = (seqBy[key] || 0) % posts.length;
      post = posts[i];
      seqBy[key] = i + 1;
    } else {
      // Random content rotation. Prefer, in this order, a post that is
      //   - not among this group's last N posts, leaving at least 2 choices
      //     (so the next rule can still pick between them),
      //   - not used in the campaign's last few posts (other groups),
      //   - not the post the previous group just got,
      //   - at least not this group's previous post.
      const lastHere = lastBy[group.url];
      const room = posts.length >= 3 ? posts.length - 2 : posts.length - 1;
      const n = Math.max(0, Math.min(camp.config.recentAvoid || 0, room));
      const mine = n > 0 ? (recentBy[group.url] || []).slice(-n) : [];
      const k = Math.min(6, posts.length - 1);
      const others = k > 0 ? (cs.recentAll || []).slice(-k) : [];
      const fresh = posts.filter((p) => !mine.includes(p.id) && p.id !== lastHere);
      const tiers = [
        fresh.filter((p) => !others.includes(p.id)),
        fresh.filter((p) => p.id !== cs.lastPostId),
        fresh,
        posts.filter((p) => p.id !== lastHere && p.id !== cs.lastPostId),
        posts.filter((p) => p.id !== lastHere),
        posts,
      ];
      post = pick(tiers.find((t) => t.length));
    }
  }
  lastBy[group.url] = post.id;
  recentBy[group.url] = [...(recentBy[group.url] || []), post.id].slice(-50);
  await setC(camp.id, (o) => ({
    postSeqBy: seqBy,
    lastPostBy: lastBy,
    lastPostId: post.id,
    recentBy,
    recentAll: [...(o.recentAll || []), post.id].slice(-12),
  }));
  return {
    id: post.id,
    index: all.indexOf(post) + 1,
    text: composeText(post.text, group.text, camp.config.footer, camp.config.footerPosition),
    // Lead media first (by chance %), the post's own images in random order.
    imageIds: postMediaIds(camp, post, true),
    imageUrls: shuffle(post.imageUrls || []),
  };
}

// React to dashboard edits while running: start newly enabled campaigns,
// stop disabled/deleted ones.
async function syncCampaigns(oldRaw, newRaw) {
  const state = await getState();
  if (!state.running) return;
  // A version 1 -> 2 migration is not a user edit.
  if (!oldRaw || !Array.isArray(oldRaw.campaigns)) return;
  const before = new Map(migrateSettings(oldRaw).campaigns.map((c) => [c.id, c]));
  const after = migrateSettings(newRaw).campaigns;
  for (const c of after) {
    const prev = before.get(c.id);
    if (c.enabled && !(prev && prev.enabled)) {
      const problem = campaignProblem(c);
      if (problem) {
        await log('warn', `เปิดชุดแล้วแต่ยังเริ่มไม่ได้: ${problem}`, c);
        continue;
      }
      await launchCampaign(c, randInt(20, 90) * 1000);
      await log('info', 'เปิดใช้ชุดนี้ระหว่างทำงาน จะเริ่มในไม่กี่นาที', c);
    } else if (!c.enabled && prev && prev.enabled) {
      await chrome.alarms.clear(alarmName(c.id));
      await setC(c.id, { nextAt: null, nextKind: null });
      await log('info', 'ปิดชุดนี้แล้ว', c);
    }
  }
  const ids = new Set(after.map((c) => c.id));
  for (const id of before.keys()) {
    if (!ids.has(id)) {
      await chrome.alarms.clear(alarmName(id));
      await removeC(id);
    }
  }
}

// ---------- posting flow ----------

function tabCmd(tabId, cmd, args = {}, timeout = 30000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`หน้าเว็บไม่ตอบสนอง (${cmd})`)), timeout);
    chrome.tabs.sendMessage(tabId, { target: 'fbap-content', cmd, ...args }).then(
      (res) => {
        clearTimeout(timer);
        if (res) resolve(res);
        else reject(new Error(`ไม่มีการตอบกลับ (${cmd})`));
      },
      (err) => {
        clearTimeout(timer);
        reject(err);
      }
    );
  });
}

async function tabCmdOk(tabId, cmd, args, timeout) {
  const r = await tabCmd(tabId, cmd, args, timeout);
  if (!r.ok) throw new Error(r.error || `${cmd} ไม่สำเร็จ`);
  return r;
}

function navigateAndWait(tabId, url, timeout) {
  return new Promise((resolve) => {
    let started = false;
    const timer = setTimeout(() => done(false), timeout);
    function listener(id, info) {
      if (id !== tabId) return;
      if (info.status === 'loading') started = true;
      if (info.status === 'complete' && started) done(true);
    }
    function done(v) {
      clearTimeout(timer);
      chrome.tabs.onUpdated.removeListener(listener);
      resolve(v);
    }
    chrome.tabs.onUpdated.addListener(listener);
    chrome.tabs.update(tabId, { url }).catch(() => done(false));
  });
}

async function createWorkerWindow(focus) {
  const win = await chrome.windows.create({
    url: 'about:blank',
    focused: !!focus,
    type: 'normal',
    width: randInt(1150, 1300),
    height: randInt(820, 940),
  });
  const tab = win.tabs[0];
  await setState({ tabId: tab.id, windowId: win.id });
  return tab;
}

// Reuses one dedicated window/tab for posting so the user's own tabs are never touched.
async function openWorkerTab(url, focus) {
  const st = await getState();
  let tab = null;
  if (st.tabId != null) {
    try {
      tab = await chrome.tabs.get(st.tabId);
      if (tab.windowId !== st.windowId) tab = null;
    } catch {
      tab = null;
    }
  }
  if (!tab) tab = await createWorkerWindow(focus);
  else if (focus) {
    const win = await chrome.windows.get(tab.windowId);
    await chrome.windows.update(tab.windowId, win.state === 'minimized' ? { state: 'normal', focused: true } : { focused: true });
    await chrome.tabs.update(tab.id, { active: true });
  }
  if (await navigateAndWait(tab.id, url, 60000)) return tab.id;

  // Page got stuck (e.g. "leave site?" prompt): start over in a fresh window.
  try {
    await chrome.tabs.remove(tab.id);
  } catch {
    /* already gone */
  }
  tab = await createWorkerWindow(focus);
  if (!(await navigateAndWait(tab.id, url, 60000))) throw new Error('โหลดหน้ากลุ่มไม่สำเร็จ');
  return tab.id;
}

const segmenter = new Intl.Segmenter(undefined, { granularity: 'grapheme' });
const THAI_TYPO = 'กขคงจชซดตถทนบปผพฟมยรลวสหอเแาิีุู่้';
const LATIN_TYPO = 'abcdefghijklmnopqrstuvwxyz';

// Types text grapheme by grapheme with bursts, pauses and corrected typos.
// Line breaks never use the Enter key (see content.js newline).
async function typeHuman(tabId, text, speed, typos, check) {
  const g = Array.from(segmenter.segment(text), (s) => s.segment);
  const tempo = rand(0.8, 1.25); // each session types a bit differently
  let i = 0;
  while (i < g.length) {
    check();
    if (g[i] === '\n') {
      await tabCmdOk(tabId, 'newline');
      i++;
      await sleep(rand(speed.min * 2, speed.max * 5) * tempo);
      continue;
    }
    const burst = randInt(1, 4);
    let chunk = '';
    let n = 0;
    while (i < g.length && n < burst && g[i] !== '\n') {
      chunk += g[i++];
      n++;
    }
    if (typos && Math.random() < 0.035) {
      const first = chunk[0];
      const pool = /[฀-๿]/.test(first) ? THAI_TYPO : /[a-z]/i.test(first) ? LATIN_TYPO : null;
      if (pool) {
        await tabCmdOk(tabId, 'insert', { text: pool[randInt(0, pool.length - 1)] });
        await sleep(rand(250, 800) * tempo); // notice the mistake
        await tabCmdOk(tabId, 'backspace');
        await sleep(rand(120, 450) * tempo);
      }
    }
    await tabCmdOk(tabId, 'insert', { text: chunk });
    let d = rand(speed.min, speed.max) * n * tempo;
    if (/[\s.,!?ๆฯ]$/.test(chunk) && Math.random() < 0.18) d += rand(300, 1400); // short think
    if (Math.random() < 0.015) d += rand(1500, 4500); // longer pause
    await sleep(d);
  }
}

// Tags a Facebook page: types "@" + the start of its name, then picks the page
// from the suggestions (shows as the bold page name linking to the page).
// Page not suggested: the name is written as plain text instead.
async function typeTag(tabId, tag, speed, check) {
  const typed = `@${tag.query}`;
  await typeHuman(tabId, typed, speed, false, check);
  await sleep(rand(1200, 2500)); // let the suggestions load
  check();
  let r = null;
  try {
    r = await tabCmd(tabId, 'pickTag', { name: tag.name }, 20000);
  } catch {
    r = null;
  }
  if (r && r.picked) {
    await log('info', `แท็กเพจ ${tag.name}`);
    return;
  }
  if (r && r.clicked) {
    await log('warn', `กดเลือกเพจ ${tag.name} แล้ว แต่ไม่แน่ใจว่าแท็กติด`);
    return;
  }
  await log('warn', `ไม่พบเพจ ${tag.name} ในรายการแท็ก ใส่เป็นข้อความธรรมดาแทน`);
  for (let i = Array.from(segmenter.segment(typed)).length; i > 0; i--) {
    await tabCmdOk(tabId, 'backspace');
    await sleep(rand(60, 180));
  }
  await typeHuman(tabId, tag.name, speed, false, check);
}

const norm = (s) => String(s || '').normalize('NFC').replace(/[\s​-‏⁠﻿]+/g, '');

function textClose(actual, expected) {
  const a = norm(actual);
  const e = norm(expected);
  if (a === e) return true;
  // Facebook may turn emoticons/links into widgets; accept small differences.
  return e.length > 0 && a.slice(0, 8) === e.slice(0, 8) && Math.abs(a.length - e.length) <= Math.max(3, e.length * 0.08);
}

async function pollState(tabId, pred, timeout, check) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    if (check) check();
    let s = null;
    try {
      s = await tabCmd(tabId, 'postState', {}, 15000);
    } catch {
      s = null;
    }
    if (s && pred(s)) return s;
    await sleep(1500);
  }
  return null;
}

// Facebook's own "blocked / slow down" notice on the page (null when none).
async function blockNotice(tabId) {
  try {
    const r = await tabCmd(tabId, 'blockCheck', {}, 15000);
    return r && r.blocked ? r.text || 'Facebook แจ้งเตือน' : null;
  } catch {
    return null;
  }
}

async function assertNotBlocked(tabId) {
  const text = await blockNotice(tabId);
  if (text) throw new Blocked(`Facebook แจ้งเตือน: ${text}`);
}

async function postToGroup(url, post, cfg, global) {
  const check = () => {
    if (abortFlag) throw new Aborted('ถูกหยุดโดยผู้ใช้');
  };
  const speed = TYPING_SPEEDS[cfg.typingSpeed] || TYPING_SPEEDS.normal;
  let prevWindowId = null;
  let tabId = null;
  let groupName = '';
  let sent = false;
  let urlKeys = [];
  try {
    if (global.focusWindow) {
      try {
        prevWindowId = (await chrome.windows.getLastFocused()).id;
      } catch {
        prevWindowId = null;
      }
    }
    tabId = await openWorkerTab(url, global.focusWindow);
    check();
    await sleep(rand(2500, 6000)); // look at the page
    await chrome.scripting.executeScript({ target: { tabId }, files: ['content.js'] });
    const page = await tabCmd(tabId, 'check');
    groupName = cleanTitle(page.title);
    if (page.login) throw new Fatal('ยังไม่ได้ล็อกอิน Facebook ใน Chrome นี้');
    if (page.checkpoint) throw new Fatal('Facebook ขอยืนยันตัวตน (checkpoint) - ให้เข้าไปยืนยันเองก่อน');
    await assertNotBlocked(tabId);
    check();

    if (cfg.browseBeforePost) {
      const n = randInt(1, 4);
      for (let i = 0; i < n; i++) {
        await tabCmd(tabId, 'scroll', { dy: randInt(250, 900) });
        await sleep(rand(1200, 4500));
        check();
      }
      if (Math.random() < 0.4) {
        await tabCmd(tabId, 'scroll', { dy: -randInt(100, 400) });
        await sleep(rand(800, 2000));
      }
      await tabCmd(tabId, 'scrollTop');
      await sleep(rand(1000, 2500));
      check();
    }

    await tabCmdOk(tabId, 'openComposer', {}, 60000);
    await sleep(rand(1000, 2800));
    check();

    if (post.text) {
      const len = Array.from(segmenter.segment(post.text)).length;
      const paste = cfg.maxTypeChars > 0 && len > cfg.maxTypeChars;
      const parts = splitPageTags(post.text, cfg.pageTags);
      if (paste) await sleep(rand(800, 2000));
      if (!parts.some((p) => p.tag)) {
        if (paste) await tabCmdOk(tabId, 'setText', { text: post.text });
        else await typeHuman(tabId, post.text, speed, cfg.typos, check);
      } else {
        for (const part of parts) {
          check();
          if (part.tag) {
            await typeTag(tabId, part.tag, speed, check);
            await sleep(rand(400, 1200));
          } else if (paste) {
            await tabCmdOk(tabId, 'paste', { text: part.text });
            await sleep(rand(300, 900));
          } else {
            await typeHuman(tabId, part.text, speed, cfg.typos, check);
          }
        }
      }
      await sleep(rand(600, 1500));
      let r = await tabCmd(tabId, 'getText');
      if (!textClose(r.text, post.text)) {
        await log('warn', 'ข้อความในช่องโพสต์ไม่ตรง แก้โดยวางข้อความใหม่ทั้งหมด');
        await tabCmdOk(tabId, 'setText', { text: post.text });
        await sleep(rand(800, 1500));
        r = await tabCmd(tabId, 'getText');
        if (!textClose(r.text, post.text)) throw new Error('ใส่ข้อความในช่องโพสต์ไม่สำเร็จ');
      }
    }

    // Media kept in object storage (imageUrls) is downloaded now and removed again once attached.
    urlKeys = await fetchUrlMedia(post.imageUrls || []);
    const mediaIds = [...post.imageIds, ...urlKeys];
    if (mediaIds.length) {
      check();
      await sleep(rand(800, 2200));
      const r = await tabCmdOk(tabId, 'attachImages', { imageIds: mediaIds }, 240000);
      await log('info', `แนบรูป ${mediaIds.length} รูป (${r.method})`);
      const ready = await pollState(tabId, (s) => s.open && (s.postEnabled || s.hasNext) && !s.uploading, 600000, check);
      if (!ready) throw new Error('อัปโหลดรูปไม่เสร็จภายในเวลา');
    }

    check();
    await sleep(rand(1500, 4500)); // re-read before posting
    check();
    await tabCmdOk(tabId, 'clickPost', {}, 45000);
    // Wait for the composer to close; a warning shown meanwhile stops us.
    // A warning that is gone on the second look does not count as closed.
    const end = Date.now() + 90000;
    let closed = false;
    while (Date.now() < end) {
      const s = await pollState(tabId, (x) => !x.open || x.blocked, end - Date.now(), null);
      if (!s) break;
      if (!s.open) {
        closed = true;
        break;
      }
      await assertNotBlocked(tabId);
      await sleep(1500);
    }
    if (!closed) throw new Error('กดโพสต์แล้วแต่หน้าต่างไม่ปิด (อาจโพสต์ไม่สำเร็จ)');
    sent = true; // the post went out: later problems never turn it into a failure
    await sleep(rand(2000, 5000));
    // A warning can also pop up right after the post went out: the post
    // still counts, and the caller pauses everything.
    const blockedAfter = await blockNotice(tabId);
    // The new post (or the "pending approval" notice) shows near the top.
    await tabCmd(tabId, 'scrollTop').catch(() => {});
    await sleep(rand(1200, 2500));
    return { ok: true, groupName, shot: await captureWorker(), blockedAfter: blockedAfter ? `Facebook แจ้งเตือน: ${blockedAfter}` : '' };
  } catch (err) {
    let e = err;
    if (sent) return { ok: true, groupName, shot: null, blockedAfter: '' };
    // Any failure may really be Facebook refusing to let us post.
    if (tabId != null && !(e instanceof Aborted) && !(e instanceof Blocked) && !(e instanceof Fatal)) {
      const text = await blockNotice(tabId);
      if (text) e = new Blocked(`Facebook แจ้งเตือน: ${text}`);
    }
    if (e && typeof e === 'object' && !(e instanceof Aborted)) {
      e.groupName = groupName;
      if (tabId != null) e.shot = await captureWorker();
    }
    if (tabId != null) {
      try {
        await tabCmd(tabId, 'discard', {}, 15000);
      } catch {
        /* ignore */
      }
    }
    throw e;
  } finally {
    if (urlKeys.length) await chrome.storage.local.remove(urlKeys).catch(() => {});
    if (prevWindowId != null) {
      const st = await getState();
      if (prevWindowId !== st.windowId) chrome.windows.update(prevWindowId, { focused: true }).catch(() => {});
    }
  }
}

// Downloads media links (a post's imageUrls) into temporary storage keys that content.js can attach.
// The caller removes the keys again. Needs the site to allow the extension (CORS or the optional host permission).
async function fetchUrlMedia(urls) {
  const keys = [];
  for (let i = 0; i < urls.length; i++) {
    const res = await fetch(urls[i], { cache: 'no-store' });
    if (!res.ok) throw new Error(`โหลดรูปไม่สำเร็จ (${res.status}): ${urls[i]}`);
    const blob = await res.blob();
    const type = blob.type || res.headers.get('content-type') || 'image/jpeg';
    const key = `urlimg:${Date.now()}_${i}`;
    keys.push(key);
    await chrome.storage.local.set({ [key]: { name: decodeURIComponent(urls[i].split('/').pop() || `image_${i + 1}`), type, data: await dataUrlOf(blob, type) } });
  }
  return keys;
}

async function testPost(cid, url, postId) {
  if (posting) return { ok: false, error: 'กำลังโพสต์อยู่ รอให้เสร็จก่อน' };
  const settings = await getSettings();
  const camp = settings.campaigns.find((c) => c.id === cid);
  if (!camp) return { ok: false, error: 'ไม่พบชุดโพสต์' };
  const target = normalizeGroupUrl(url);
  if (!target) return { ok: false, error: 'ลิงก์กลุ่มไม่ถูกต้อง' };
  if (!usablePosts(camp.posts).length) return { ok: false, error: 'ชุดนี้ยังไม่มีเนื้อหาโพสต์' };
  const handPicked = postId && usablePosts(camp.posts).some((p) => p.id === postId);
  if (!handPicked && !postsForGroup(camp, target).length) {
    return { ok: false, error: 'ไม่มีโพสต์ที่ตั้งให้ใช้กับกลุ่มนี้: เลือกแบบโพสต์เอง หรือติ๊กกลุ่มนี้ในช่อง "ใช้กับ" ของโพสต์' };
  }
  const group = activeGroups(camp).find((g) => g.url === target) || { url: target, text: '', name: '' };
  const until = groupLimitUntil(settings, await getState(), target);
  if (until > Date.now()) {
    return { ok: false, error: `กลุ่มนี้โพสต์ครบ ${groupLimit(settings, target)} ครั้งใน 24 ชม. ตามกฎกลุ่มแล้ว โพสต์ได้อีก ${fmtDateTime(until)}` };
  }
  enqueue(async () => {
    posting = true;
    abortFlag = false;
    keepAlive(true);
    await setState({ testing: true, current: { campaignId: cid, url: target } });
    try {
      const post = await choosePost(camp, group, postId);
      await log('info', `[ทดสอบ] กำลังโพสต์ #${post.index}: ${target}`, camp);
      const r = await postToGroup(target, post, camp.config, settings.global);
      if (r.groupName) await rememberGroupName(target, r.groupName);
      await setState({ lastPostAt: Date.now() });
      await countPost(target); // test posts are real posts: they count too
      await log('success', `[ทดสอบ] โพสต์สำเร็จ: ${target}`, camp);
      await notify('success', `🧪 <b>[ทดสอบ] โพสต์สำเร็จ</b>\n${groupLine(r.groupName, target)}\nชุด ${escHtml(camp.name)}`, r.shot);
      if (r.blockedAfter && (await getState()).running) {
        const g = settings.global;
        await pauseAll(g.blockPauseHoursMin, g.blockPauseHoursMax, 'Facebook แจ้งเตือนหลังโพสต์ (ตอนทดสอบ)', {
          shot: r.shot,
          detail: r.blockedAfter,
        });
      }
    } catch (e) {
      await log(e instanceof Aborted ? 'warn' : 'error', `[ทดสอบ] ไม่สำเร็จ: ${e?.message || e}`, camp);
      if (e instanceof Blocked && (await getState()).running) {
        const g = settings.global;
        await pauseAll(g.blockPauseHoursMin, g.blockPauseHoursMax, 'Facebook แจ้งเตือน/จำกัดการโพสต์ (ตอนทดสอบ)', {
          shot: e.shot,
          detail: e.message,
        });
      } else if (!(e instanceof Aborted)) {
        await notify(
          'fail',
          `🧪 <b>[ทดสอบ] โพสต์ไม่สำเร็จ</b>\n${groupLine(e?.groupName, target)}\nสาเหตุ: ${escHtml(e?.message || e)}`,
          e?.shot
        );
      }
    } finally {
      posting = false;
      keepAlive(false);
      await setState({ testing: false, current: null });
    }
  });
  return { ok: true };
}

// ---------- config file in the extension folder ----------

async function readBundledConfig() {
  try {
    const res = await fetch(chrome.runtime.getURL(BUNDLED_CONFIG_PATH), { cache: 'no-store' });
    return res.ok ? await res.text() : null;
  } catch {
    return null; // file not there
  }
}

async function storageKeys() {
  return chrome.storage.local.getKeys
    ? chrome.storage.local.getKeys()
    : Object.keys(await chrome.storage.local.get(null));
}

// Loads config/autopost-config.json (posts, images, groups, all settings).
// auto = true: only when this browser has no data yet, i.e. a fresh install
// or the folder was copied to another computer.
async function loadBundledConfig({ auto = false } = {}) {
  const text = await readBundledConfig();
  if (!text) return { ok: false, missing: true, error: `ไม่พบไฟล์ ${BUNDLED_CONFIG_PATH} ในโฟลเดอร์ส่วนขยาย` };
  const { settings: raw } = await chrome.storage.local.get('settings');
  const current = migrateSettings(raw);
  if (auto && hasContent(current)) return { ok: false, skipped: true };
  const state = await getState();
  if (state.running || posting) return { ok: false, error: 'กด "หยุด" ก่อนโหลด config' };
  let parsed;
  try {
    parsed = parseBackup(text);
  } catch (e) {
    await log('error', `ไฟล์ ${BUNDLED_CONFIG_PATH} ใช้ไม่ได้: ${e.message}`);
    return { ok: false, error: e.message };
  }
  const keys = await storageKeys();
  const existing = new Set(keys.filter((k) => k.startsWith('img:')).map((k) => k.slice(4)));
  const res = applyImport(current, parsed, 'replace', existing);
  const writes = {};
  for (const [id, rec] of Object.entries(res.writeImages)) writes['img:' + id] = rec;
  await chrome.storage.local.set({
    ...writes,
    settings: res.settings,
    configLoaded: { at: Date.now(), exportedAt: parsed.meta.exportedAt || '', auto },
  });
  // Drop stored images the new settings no longer use.
  const used = new Set(res.settings.campaigns.flatMap(campaignImageIds));
  const orphan = keys.filter((k) => k.startsWith('img:') && !used.has(k.slice(4)));
  if (orphan.length) await chrome.storage.local.remove(orphan);

  const sum = summarize(res.settings, parsed.images);
  await log(
    'success',
    `โหลด config จากโฟลเดอร์ส่วนขยายแล้ว: ${sum.campaigns} ชุด · ${sum.groups} กลุ่ม · ${sum.posts} แบบโพสต์ · ${sum.images} รูป` +
      (res.missingImages ? ` (ไม่มีรูปในไฟล์ ${res.missingImages} รูป)` : '')
  );
  if (auto && parsed.autoStart) {
    const r = await start();
    if (!r.ok) await log('warn', `เริ่มทำงานอัตโนมัติไม่ได้: ${r.error}`);
  }
  return { ok: true, summary: sum, missingImages: res.missingImages };
}

async function tgTest() {
  const { telegram: t } = (await getSettings()).global;
  if (!t.botToken || !t.chatId) return { ok: false, error: 'ใส่ Bot Token และ Chat ID ก่อน' };
  try {
    await sendText(t.botToken, t.chatId, `🔔 <b>ทดสอบแจ้งเตือน</b>\nFB AutoPost เชื่อมต่อ Telegram สำเร็จ\n🕒 ${fmtDateTime(Date.now())}`);
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e?.message || String(e) };
  }
}

async function tgFindChats() {
  const { telegram: t } = (await getSettings()).global;
  if (!t.botToken) return { ok: false, error: 'ใส่ Bot Token ก่อน' };
  try {
    return { ok: true, chats: await findChats(t.botToken) };
  } catch (e) {
    return { ok: false, error: e?.message || String(e) };
  }
}

// ---------- online: config from the server ----------
// The computer signs in to the server (backend/SIRI.AUTOPOST.Server) with a device
// key, pulls the config chosen for it there, pushes edits made in this
// browser, reports state and logs, and runs commands sent from the web page.

const SYNC_ALARM = 'fbap-sync';
const SYNC_PERIOD_MIN = 0.5;
const PUSH_DELAY_MS = 4000;

const DEFAULT_ONLINE = {
  enabled: false,
  serverUrl: '',
  deviceKey: '',
  deviceName: '',
  profileId: null,   // server config this browser's settings came from
  profileName: '',
  revision: 0,       // server revision of the settings this browser has
  syncedHash: '',    // hash of the settings at that revision (finds local edits)
  lastLogT: 0,       // newest log already sent
  lastSyncAt: 0,
  lastError: '',
};

let lastStateSent = ''; // state is sent only when it changed
let pushTimer = null;

async function getOnline() {
  const { online } = await chrome.storage.local.get('online');
  return { ...DEFAULT_ONLINE, ...(online || {}) };
}

let onlineChain = Promise.resolve();
function setOnline(patch) {
  const p = onlineChain.then(async () => {
    const next = { ...(await getOnline()), ...patch };
    await chrome.storage.local.set({ online: next });
    return next;
  });
  onlineChain = p.catch(() => {});
  return p;
}

const normServer = (url) => String(url || '').trim().replace(/\/+$/, '');

async function sha256(text) {
  const buf = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

// Settings as the extension uses them, plus their hash.
async function localSettings() {
  const { settings: raw } = await chrome.storage.local.get('settings');
  const settings = migrateSettings(raw);
  return { settings, hash: await sha256(JSON.stringify(settings)) };
}

async function serverApi(o, method, path, body) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), 120000);
  let res;
  try {
    res = await fetch(normServer(o.serverUrl) + path, {
      method,
      headers: { 'X-Device-Key': o.deviceKey, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: ctrl.signal,
      cache: 'no-store',
    });
  } catch (e) {
    throw new Error(e?.name === 'AbortError' ? 'server ไม่ตอบ (หมดเวลา)' : `ติดต่อ server ไม่ได้ (${e?.message || e})`);
  } finally {
    clearTimeout(timer);
  }
  let data = null;
  try {
    data = await res.json();
  } catch {
    /* empty body */
  }
  if (!res.ok) {
    const err = new Error((data && data.error) || `server ตอบกลับ ${res.status}`);
    err.status = res.status;
    throw err;
  }
  return data;
}

async function heartbeat(o, takeCommands) {
  const { state, logs = [] } = await chrome.storage.local.get(['state', 'logs']);
  const stateJson = JSON.stringify(state || {});
  const fresh = logs.filter((l) => l.t > o.lastLogT).slice(-300);
  const body = { version: chrome.runtime.getManifest().version, takeCommands, logs: fresh };
  if (stateJson !== lastStateSent) body.state = state || {};
  const res = await serverApi(o, 'POST', '/api/device/heartbeat', body);
  lastStateSent = stateJson;
  if (fresh.length) await setOnline({ lastLogT: fresh[fresh.length - 1].t });
  return res;
}

// Replaces this browser's settings with the server config.
async function pullConfig(o) {
  const cfg = await serverApi(o, 'GET', '/api/device/config');
  if (!cfg.settings) {
    // Nothing on the server yet: keep ours, just remember where we are.
    const { hash } = await localSettings();
    await setOnline({ profileId: cfg.profileId, profileName: cfg.name, revision: cfg.revision, syncedHash: hash });
    return;
  }
  const incoming = migrateSettings(cfg.settings);
  const existing = new Set((await storageKeys()).filter((k) => k.startsWith('img:')).map((k) => k.slice(4)));
  let notFound = 0;
  for (const id of imageIdsOf(incoming.campaigns)) {
    if (existing.has(id)) continue;
    try {
      const rec = await serverApi(o, 'GET', `/api/device/images/${encodeURIComponent(id)}`);
      await chrome.storage.local.set({ ['img:' + id]: rec });
      existing.add(id);
    } catch (e) {
      if (e.status !== 404) throw e;
      notFound++;
    }
  }
  const { settings: current } = await localSettings();
  // Same rule as a config file: no Bot Token on the server = keep this computer's Telegram.
  const res = applyImport(current, { settings: incoming, images: {} }, 'replace', existing);
  await chrome.storage.local.set({ settings: res.settings, onlinePulled: { at: Date.now(), revision: cfg.revision } });
  const used = new Set(res.settings.campaigns.flatMap(campaignImageIds));
  const orphan = (await storageKeys()).filter((k) => k.startsWith('img:') && !used.has(k.slice(4)));
  if (orphan.length) await chrome.storage.local.remove(orphan);
  const { hash } = await localSettings();
  await setOnline({ profileId: cfg.profileId, profileName: cfg.name, revision: cfg.revision, syncedHash: hash });
  const sum = summarize(res.settings, {});
  await log(
    'success',
    `โหลด config "${cfg.name}" จาก server แล้ว (ฉบับที่ ${cfg.revision}): ${sum.campaigns} ชุด · ${sum.groups} กลุ่ม · ${sum.posts} แบบโพสต์` +
      (notFound ? ` (ไม่มีรูปบน server ${notFound} รูป)` : '')
  );
}

// Sends this browser's settings (and the images the server lacks) up.
// baseRevision null = overwrite whatever the server has.
async function pushConfig(o, profile, local, baseRevision, { quiet = false } = {}) {
  const ids = imageIdsOf(local.settings.campaigns);
  if (ids.length) {
    const { missing } = await serverApi(o, 'POST', '/api/device/images/missing', { ids });
    for (const id of missing) {
      const rec = (await chrome.storage.local.get('img:' + id))['img:' + id];
      if (rec) await serverApi(o, 'PUT', `/api/device/images/${encodeURIComponent(id)}`, rec);
    }
  }
  try {
    const r = await serverApi(o, 'PUT', '/api/device/config', { settings: local.settings, baseRevision });
    await setOnline({ profileId: profile.id, profileName: profile.name, revision: r.revision, syncedHash: local.hash });
    if (!quiet) await log('success', `อัปโหลดการตั้งค่าของเครื่องนี้ขึ้น server แล้ว (config "${profile.name}")`);
  } catch (e) {
    if (e.status !== 409) throw e;
    await log('warn', 'config บน server ถูกแก้ไปก่อน: ใช้ของ server แทนการแก้ในเครื่องนี้');
    await pullConfig(o);
  }
}

// mode 'auto': pull server changes, push local edits.
// mode 'pull' / 'push': replace one side with the other.
async function syncConfig(o, profile, mode) {
  if (!profile) return;
  const local = await localSettings();
  const sameProfile = o.profileId === profile.id;
  const localEdited = sameProfile && !!o.syncedHash && local.hash !== o.syncedHash;
  const serverChanged = !sameProfile || profile.revision !== o.revision;

  if (mode === 'push') return pushConfig(o, profile, local, null);
  if (mode === 'pull') return pullConfig(o);
  // First sync with an empty server config: upload what this computer has.
  if ((!sameProfile || !o.revision) && !profile.hasContent && hasContent(local.settings)) {
    return pushConfig(o, profile, local, profile.revision);
  }
  if (serverChanged) {
    if (localEdited) await log('warn', 'config ถูกแก้ทั้งในเครื่องนี้และบน server: ใช้ของ server');
    return pullConfig(o);
  }
  if (localEdited) return pushConfig(o, profile, local, o.revision, { quiet: true });
}

const remoteCommands = {
  start: () => start(),
  stop: () => stop('สั่งหยุดจากหน้าเว็บ'),
  runNow: (a) => runNow(a.campaignId),
  testPost: (a) => testPost(a.campaignId, a.url, a.postId),
  tgTest: () => tgTest(),
  tgFindChats: () => tgFindChats(),
  clearLogs: async () => {
    await chrome.storage.local.set({ logs: [] });
    return { ok: true };
  },
  syncNow: async () => ({ ok: true }),
};

// Runs commands sent from a web page; report(id, result) sends each result back.
// The server hands a command out again until its result is reported (the sync that carried it may have been
// cut short after the server handed it over). So every id is run once: the ids and the results are kept here,
// and a command that comes again is not run again, its result is only reported again.
const CMD_LEDGER_KEY = 'cmdLedger';
const CMD_LEDGER_KEEP_MS = 60 * 60 * 1000; // the server gives up on a command after 10 minutes
let cmdLedgerChain = Promise.resolve();

function cmdLedger(update) {
  const run = cmdLedgerChain.then(async () => {
    const { [CMD_LEDGER_KEY]: stored } = await chrome.storage.local.get([CMD_LEDGER_KEY]);
    const ledger = stored || {};
    const out = update(ledger);
    const cutoff = Date.now() - CMD_LEDGER_KEEP_MS;
    for (const id of Object.keys(ledger)) if (ledger[id].at < cutoff) delete ledger[id];
    await chrome.storage.local.set({ [CMD_LEDGER_KEY]: ledger });
    return out;
  });
  cmdLedgerChain = run.catch(() => {});
  return run;
}

async function runRemoteCommands(list, report) {
  for (const c of list) {
    const known = await cmdLedger((l) => {
      if (!l[c.id]) l[c.id] = { at: Date.now() }; // before running: a long command must not start twice
      return l[c.id];
    });
    if (known.seen) {
      // Handed out again: report an answer we have, or leave it to the run that is still going.
      if (known.result !== undefined) {
        try {
          await report(c.id, known.result);
        } catch (e) {
          console.warn('[FBAP] command result', e);
        }
      }
      continue;
    }
    await cmdLedger((l) => { l[c.id].seen = true; });
    const fn = remoteCommands[c.cmd];
    let result;
    try {
      result = fn ? await fn(c.args || {}) : { ok: false, error: `ไม่รู้จักคำสั่ง ${c.cmd}` };
    } catch (e) {
      result = { ok: false, error: e?.message || String(e) };
    }
    if (!['clearLogs', 'syncNow'].includes(c.cmd)) {
      await log('info', `รับคำสั่งจากหน้าเว็บ: ${c.cmd}${result && result.ok === false ? ` (ไม่สำเร็จ: ${result.error})` : ''}`);
    }
    const answer = result ?? { ok: true };
    await cmdLedger((l) => { l[c.id].result = answer; });
    try {
      await report(c.id, answer);
    } catch (e) {
      console.warn('[FBAP] command result', e);
    }
  }
  return list.length;
}

async function doSync({ mode = 'auto' } = {}) {
  let o = await getOnline();
  if (!o.enabled || !o.serverUrl || !o.deviceKey) return { ok: false, error: 'ยังไม่ได้เชื่อมต่อ server' };
  try {
    const hb = await heartbeat(o, true);
    await syncConfig(await getOnline(), hb.profile, mode);
    o = await getOnline();
    // Report the new state right away so the web page sees it.
    const report = (id, result) => serverApi(o, 'POST', `/api/device/commands/${id}/result`, { result });
    if (await runRemoteCommands(hb.commands || [], report)) await heartbeat(await getOnline(), false);
    await setOnline({
      deviceName: hb.device.name,
      profileName: hb.profile ? hb.profile.name : '',
      lastSyncAt: Date.now(),
      lastError: hb.profile ? '' : 'server ยังไม่ได้เลือก config ให้เครื่องนี้',
    });
    return { ok: true };
  } catch (e) {
    const msg = e?.message || String(e);
    const prev = (await getOnline()).lastError;
    await setOnline({ lastError: msg });
    if (msg !== prev) await log('warn', `ซิงก์กับ server ไม่สำเร็จ: ${msg}`);
    return { ok: false, error: msg };
  }
}

// One sync at a time; requests made while one waits join it.
let syncChain = Promise.resolve();
let syncQueued = null;
function syncOnline(opts = {}) {
  if (syncQueued) {
    if (opts.mode) syncQueued.opts.mode = opts.mode;
    return syncQueued.promise;
  }
  const entry = { opts: { ...opts } };
  entry.promise = syncChain.then(() => {
    syncQueued = null;
    return doSync(entry.opts);
  });
  syncQueued = entry;
  syncChain = entry.promise.catch(() => {});
  return entry.promise;
}

async function ensureSyncAlarm() {
  const o = await getOnline();
  if (!o.enabled) return chrome.alarms.clear(SYNC_ALARM);
  if (!(await chrome.alarms.get(SYNC_ALARM))) {
    await chrome.alarms.create(SYNC_ALARM, { periodInMinutes: SYNC_PERIOD_MIN, delayInMinutes: SYNC_PERIOD_MIN });
  }
}

async function onlineConnect({ serverUrl, deviceKey }) {
  const url = normServer(serverUrl);
  if (!/^https?:\/\/[^/\s]+/i.test(url)) return { ok: false, error: 'URL server ต้องขึ้นต้นด้วย http:// หรือ https://' };
  const key = String(deviceKey || '').trim();
  if (!key) return { ok: false, error: 'ใส่คีย์เครื่องก่อน' };
  let hb;
  try {
    hb = await serverApi({ serverUrl: url, deviceKey: key }, 'POST', '/api/device/heartbeat', {
      version: chrome.runtime.getManifest().version,
      takeCommands: false,
    });
  } catch (e) {
    return { ok: false, error: e.message };
  }
  const old = await getOnline();
  const same = old.serverUrl === url && old.deviceKey === key;
  await setOnline({
    enabled: true,
    serverUrl: url,
    deviceKey: key,
    deviceName: hb.device.name,
    profileName: hb.profile ? hb.profile.name : '',
    lastError: '',
    ...(same ? {} : { profileId: null, revision: 0, syncedHash: '', lastLogT: 0 }),
  });
  lastStateSent = '';
  await ensureSyncAlarm();
  await log('info', `เชื่อมต่อ server แล้ว: ${url} (เครื่อง "${hb.device.name}")`);
  return syncOnline();
}

async function onlineDisconnect() {
  await setOnline({ enabled: false, lastError: '' });
  await ensureSyncAlarm();
  await log('info', 'ยกเลิกการเชื่อมต่อ server (การตั้งค่าในเครื่องนี้ยังอยู่)');
  return { ok: true };
}

// Local edits reach the server a few seconds after the last change.
function schedulePush() {
  clearTimeout(pushTimer);
  pushTimer = setTimeout(async () => {
    if ((await getOnline()).enabled) syncOnline();
  }, PUSH_DELAY_MS);
}

// ---------- cloud: posts scheduled in the SIRI AutoPost web app ----------
// The browser pairs with a workspace of SIRIAUTOPOST.Api (a code from Team & workspaces),
// then every 30 seconds: heartbeat, send the groups set up here (they become the groups of
// this browser's Facebook account in the web app), and take one due post to publish with
// the same human-like flow as the campaigns. Independent of Start/Stop and of the legacy
// server connection above.

const CLOUD_ALARM = 'fbap-cloud';
const CLOUD_PERIOD_MIN = 0.5;
const CLOUD_IMG = 'cloudimg:'; // media of a cloud post; removed after posting

const DEFAULT_CLOUD = {
  enabled: false,
  apiUrl: '',
  deviceKey: '',
  deviceId: '',
  deviceName: '',
  workspaceName: '',
  paused: false,      // paused in the web app: take no posts (heartbeats and settings go on)
  pausedUntil: 0,     // Facebook showed a warning: take no posts until then
  groupsHash: '',     // groups last sent to the web app
  groupNames: {},     // url -> name as last sent (a name the web app knows is kept: queued posts use it)
  groups: 0,
  lastSyncAt: 0,
  lastError: '',
  lastJob: null,      // { at, group, ok, error }
  // The campaigns of this browser, shown and edited in the web app (cloudConfigSync).
  configRevision: 0,  // web revision of the settings this browser has
  configHash: '',     // hash of the settings at that revision (finds local edits)
  lastLogT: 0,        // newest log line already sent
  configSyncAt: 0,
  configError: '',
};

async function getCloud() {
  const { cloud } = await chrome.storage.local.get('cloud');
  return { ...DEFAULT_CLOUD, ...(cloud || {}) };
}

let cloudChain = Promise.resolve();
function setCloud(patch) {
  const p = cloudChain.then(async () => {
    const next = { ...(await getCloud()), ...patch };
    await chrome.storage.local.set({ cloud: next });
    return next;
  });
  cloudChain = p.catch(() => {});
  return p;
}

// fetch with the device key; errors carry the API's Thai message (ProblemDetails title).
// signal: an AbortController of the caller's; aborting it ends the call with err.aborted = true.
async function cloudApi(c, method, path, body, { blob = false, signal = null } = {}) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), 120000);
  const onOuter = () => ctrl.abort();
  if (signal) signal.aborted ? ctrl.abort() : signal.addEventListener('abort', onOuter, { once: true });
  let res;
  try {
    res = await fetch(normServer(c.apiUrl) + path, {
      method,
      headers: {
        ...(c.deviceKey ? { 'X-Device-Key': c.deviceKey } : {}),
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: ctrl.signal,
      cache: 'no-store',
    });
  } catch (e) {
    const byCaller = !!signal?.aborted;
    const err = new Error(byCaller ? 'ยกเลิกการรอ' : e?.name === 'AbortError' ? 'เว็บ AutoPost ไม่ตอบ (หมดเวลา)' : `ติดต่อเว็บ AutoPost ไม่ได้ (${e?.message || e})`);
    err.aborted = byCaller;
    throw err;
  } finally {
    clearTimeout(timer);
    if (signal) signal.removeEventListener('abort', onOuter);
  }
  if (res.ok && blob) return res.blob();
  let data = null;
  try {
    data = res.status === 204 ? null : await res.json();
  } catch {
    /* empty body */
  }
  if (!res.ok) {
    // A 400 carries the per-field messages in `errors`; the title alone is only "invalid data".
    const first = data && data.errors && Object.values(data.errors).flat().find((m) => typeof m === 'string');
    const title = data && (data.title || data.error);
    const msg = res.status === 401
      ? 'เครื่องนี้ถูกยกเลิกการผูกแล้ว จับคู่ใหม่ด้วยรหัสจากหน้าเว็บ'
      : (title && first && first !== title ? `${title}: ${first}` : title || first) || `เว็บ AutoPost ตอบกลับ ${res.status}`;
    const err = new Error(msg);
    err.status = res.status;
    throw err;
  }
  return data;
}

// Most groups one browser can send (the web app's limit, MaxGroups in the API).
const CLOUD_MAX_GROUPS = 5000;

// Groups of every campaign (enabled ones). A name is the one written in the campaign, else the one the
// web app already has for that address (queued posts name their group, so a name must not change under
// them when Facebook's own name is learned after the first post), else the one seen on Facebook, else
// the address. Names must be unique in the web app, so a repeated name gets the group's address added.
async function cloudGroupList(known = {}) {
  const settings = await getSettings();
  const state = await getState();
  const out = [];
  const urls = new Set();
  const names = new Set();
  for (const camp of settings.campaigns) {
    for (const g of activeGroups(camp)) {
      if (urls.has(g.url)) continue;
      urls.add(g.url);
      let name = ((g.name || '').trim() || known[g.url] || (state.groupNames || {})[g.url] || '').trim() || shortUrl(g.url);
      if (names.has(name)) name = `${name} (${shortUrl(g.url)})`;
      names.add(name);
      out.push({ name, url: g.url });
    }
  }
  if (out.length > CLOUD_MAX_GROUPS) {
    await log('warn', `[เว็บ AutoPost] มี ${out.length} กลุ่ม ส่งให้เว็บได้สูงสุด ${CLOUD_MAX_GROUPS} กลุ่ม ส่วนที่เกินจะไม่ถูกส่ง`);
    out.length = CLOUD_MAX_GROUPS;
  }
  return out;
}

// Same record shape as the campaign images: { name, type, data: data URL }.
async function dataUrlOf(blob, type) {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let bin = '';
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return `data:${type || blob.type || 'application/octet-stream'};base64,${btoa(bin)}`;
}

// Posts one job from the web app and reports how it went.
async function runCloudJob(c, job) {
  const global = (await getSettings()).global;
  const ab = job.antiBan || {};
  const cfg = {
    ...DEFAULT_CONFIG,
    pageTags: '',
    typingSpeed: 'normal',
    typos: ab.typing !== false,
    maxTypeChars: ab.typing === false ? 1 : DEFAULT_CONFIG.maxTypeChars, // typing off = paste
    browseBeforePost: ab.scroll !== false,
  };
  const keys = [];
  posting = true;
  abortFlag = false;
  keepAlive(true);
  await setState({ current: { campaignId: null, url: job.groupUrl, cloud: true } });
  let result;
  try {
    for (const m of job.media || []) {
      const blob = await cloudApi(c, 'GET', `/api/device/media/${m.id}`, undefined, { blob: true });
      const key = CLOUD_IMG + m.id;
      const type = m.contentType || blob.type;
      await chrome.storage.local.set({ [key]: { name: m.name, type, data: await dataUrlOf(blob, type) } });
      keys.push(key);
    }
    await log('info', `[เว็บ AutoPost] กำลังโพสต์ลงกลุ่ม ${job.groupName}: ${job.groupUrl}`);
    result = await postToGroup(job.groupUrl, { text: job.content, imageIds: keys }, cfg, global);
  } catch (e) {
    result = {
      ok: false,
      error: e?.message || String(e),
      aborted: e instanceof Aborted,
      fatal: e instanceof Fatal,
      blocked: e instanceof Blocked,
      shot: e?.shot || null,
      groupName: e?.groupName || '',
    };
  } finally {
    posting = false;
    keepAlive(false);
    if (keys.length) await chrome.storage.local.remove(keys);
    await setState({ current: null });
  }

  if (result.ok) {
    await countPost(job.groupUrl);
    await setState({ lastPostAt: Date.now() });
    if (result.groupName) await rememberGroupName(job.groupUrl, result.groupName);
    await log('success', `[เว็บ AutoPost] โพสต์สำเร็จ: ${job.groupName} ${job.groupUrl}`);
  } else {
    await log('error', `[เว็บ AutoPost] โพสต์ไม่สำเร็จ: ${job.groupName} - ${result.error}`);
  }
  try {
    await cloudApi(c, 'POST', `/api/device/jobs/${job.postId}/result`, {
      ok: !!result.ok,
      awaitingApproval: false,
      needsLogin: !!result.fatal,
      blocked: !!(result.blocked || result.blockedAfter),
      error: result.ok ? (result.blockedAfter || null) : result.error,
    });
  } catch (e) {
    await log('warn', `[เว็บ AutoPost] ส่งผลการโพสต์ไม่สำเร็จ: ${e.message}`);
  }
  const g = global;
  const patch = { lastJob: { at: Date.now(), group: job.groupName, ok: !!result.ok, error: result.ok ? '' : result.error } };
  if (result.blocked || result.blockedAfter) {
    // Same rest as the campaigns get after a Facebook warning.
    patch.pausedUntil = Date.now() + rand(g.blockPauseHoursMin || 2, g.blockPauseHoursMax || 4) * 3600000;
    await log('warn', `[เว็บ AutoPost] Facebook แจ้งเตือน พักรับงานถึง ${fmtDateTime(patch.pausedUntil)}`);
  }
  await setCloud(patch);
  if (result.ok) {
    await notify('success', `✅ <b>โพสต์สำเร็จ (เว็บ AutoPost)</b>\n${groupLine(result.groupName || job.groupName, job.groupUrl)}\n🕒 ${fmtDateTime(Date.now())}`, result.shot);
  } else if (!result.aborted) {
    await notify('fail', `❌ <b>โพสต์ไม่สำเร็จ (เว็บ AutoPost)</b>\n${groupLine(job.groupName, job.groupUrl)}\nสาเหตุ: ${escHtml(result.error)}`, result.shot);
  }
  return result;
}

// One round: heartbeat, groups, and at most one post. Runs in the tick chain, so it never
// overlaps a campaign post.
async function cloudTick({ claim = true } = {}) {
  let c = await getCloud();
  if (!c.enabled || !c.apiUrl || !c.deviceKey) return { ok: false, error: 'ยังไม่ได้จับคู่กับเว็บ AutoPost' };
  try {
    const hb = await cloudApi(c, 'POST', '/api/device/heartbeat', { version: chrome.runtime.getManifest().version });
    const groups = await cloudGroupList(c.groupNames || {});
    const hash = await sha256(JSON.stringify(groups));
    let count = hb.groups;
    let groupNames = c.groupNames || {};
    if (hash !== c.groupsHash || count !== groups.length) {
      count = await cloudApi(c, 'PUT', '/api/device/groups', { groups });
      groupNames = Object.fromEntries(groups.map((g) => [g.url, g.name]));
      await log('info', `[เว็บ AutoPost] ส่งรายชื่อกลุ่ม ${count} กลุ่มไปที่เว็บแล้ว`);
    }
    c = await setCloud({
      deviceName: hb.deviceName,
      workspaceName: hb.workspaceName,
      paused: !!hb.jobsPaused,
      groupsHash: hash,
      groupNames,
      groups: count,
      lastSyncAt: Date.now(),
      lastError: '',
    });
    const state = await getState();
    const resting = c.pausedUntil > Date.now() || (state.pausedUntil && Date.now() < state.pausedUntil);
    if (!claim || c.paused || resting || !hb.online || posting) return { ok: true, posted: false };
    const job = await cloudApi(c, 'POST', '/api/device/jobs/claim');
    if (!job) return { ok: true, posted: false };
    const r = await runCloudJob(c, job);
    return { ok: true, posted: true, result: !!r.ok };
  } catch (e) {
    const msg = e?.message || String(e);
    if (e?.status === 401) return cloudForget(msg);
    const prev = (await getCloud()).lastError;
    await setCloud({ lastError: msg });
    if (msg !== prev) await log('warn', `[เว็บ AutoPost] ${msg}`);
    return { ok: false, error: msg };
  }
}

// Unbound in the web app (the key no longer works): back to "not paired". The campaigns stay in this
// browser and go up again when it is paired with a workspace.
async function cloudForget(msg) {
  if ((await getCloud()).enabled) {
    await setCloud({ ...DEFAULT_CLOUD });
    await ensureCloudAlarm();
    await log('warn', `[เว็บ AutoPost] ${msg}`);
  }
  return { ok: false, error: msg };
}

async function ensureCloudAlarm() {
  const c = await getCloud();
  if (!c.enabled) return chrome.alarms.clear(CLOUD_ALARM);
  if (!(await chrome.alarms.get(CLOUD_ALARM))) {
    await chrome.alarms.create(CLOUD_ALARM, { periodInMinutes: CLOUD_PERIOD_MIN, delayInMinutes: CLOUD_PERIOD_MIN });
  }
  cloudListen();
}

// ---------- live: a sync held open so the web app's buttons arrive at once ----------
//
// cloudListen loops on cloudConfigSync({ wait: true }): the server holds each call until the web app sends
// a command (or ~25 s pass), so Start/Stop on the web reach this browser within a second instead of on the
// next 30-second alarm. Every round also reports state and new log lines. Other syncs (an edit to push, a
// button, the alarm) call cloudWake() to cut the open call short and run at once. The loop is only a
// shortcut: the alarm still fires every 30 s and restarts it after the service worker was put to sleep.

let cloudWaitCtrl = null;  // AbortController of the sync call held open right now
let cloudListening = false;
let cloudListenKeep = null;
let cloudListenFailures = 0;

function cloudWake() {
  if (cloudWaitCtrl) cloudWaitCtrl.abort();
}

function cloudListen() {
  if (cloudListening) return;
  cloudListening = true;
  // MV3 ends an idle worker after 30 s; an extension API call now and then keeps it up while a call is open.
  cloudListenKeep = setInterval(() => chrome.runtime.getPlatformInfo().catch(() => {}), 20000);
  (async () => {
    try {
      while (true) {
        const c = await getCloud();
        if (!c.enabled || !c.apiUrl || !c.deviceKey) return;
        const r = await cloudConfigSync({ wait: true });
        if (r.ok) {
          cloudListenFailures = 0;
          continue;
        }
        // The web is unreachable (or unbound: cloud is off now): back off up to a minute, the alarm covers the rest.
        const delay = Math.min(60000, 5000 * 2 ** Math.min(cloudListenFailures++, 4)) * (0.7 + Math.random() * 0.6);
        await new Promise((r) => setTimeout(r, delay));
      }
    } finally {
      clearInterval(cloudListenKeep);
      cloudListenKeep = null;
      cloudListening = false;
    }
  })();
}

// "Pair": trade the code from the web app for this browser's device key.
async function cloudPair({ apiUrl, code, name }) {
  const url = normServer(apiUrl);
  if (!/^https?:\/\/[^/\s]+/i.test(url)) return { ok: false, error: 'URL ต้องขึ้นต้นด้วย http:// หรือ https://' };
  if (!String(code || '').trim()) return { ok: false, error: 'ใส่รหัสจับคู่จากหน้าเว็บก่อน' };
  const ua = (globalThis.navigator && navigator.userAgent) || '';
  const browser = (ua.match(/(Edg|OPR|Chrome)\/(\d+)/) || []).slice(1).join(' ').replace('Edg', 'Edge').replace('OPR', 'Opera') || 'Chrome';
  let pair;
  try {
    pair = await cloudApi({ apiUrl: url }, 'POST', '/api/device/pair', {
      code: String(code).trim(),
      name: String(name || '').trim() || `${browser} ${new Date().toLocaleDateString('th-TH')}`,
      browser,
      version: chrome.runtime.getManifest().version,
    });
  } catch (e) {
    return { ok: false, error: e.message };
  }
  await setCloud({
    ...DEFAULT_CLOUD,
    enabled: true,
    apiUrl: url,
    deviceKey: pair.deviceKey,
    deviceId: pair.deviceId,
    deviceName: pair.deviceName,
    workspaceName: pair.workspaceName,
  });
  await ensureCloudAlarm();
  await log('success', `[เว็บ AutoPost] จับคู่กับเวิร์กสเปซ "${pair.workspaceName}" แล้ว (เครื่อง "${pair.deviceName}")`);
  cloudStateSent = '';
  const tick = await enqueue(() => cloudTick({ claim: false }));
  // Upload this browser's campaigns (or take the web's) right away.
  await cloudConfigSync();
  return tick;
}

async function cloudUnpair() {
  await setCloud({ ...DEFAULT_CLOUD });
  await ensureCloudAlarm();
  await log('info', '[เว็บ AutoPost] ยกเลิกการจับคู่ในเครื่องนี้แล้ว (ยกเลิกการผูกในหน้าเว็บด้วย เพื่อคืนโควตาอุปกรณ์)');
  return { ok: true };
}

// ---------- pairing started from the web app ----------
// The web app opens <its origin>/connect-extension#ap-pair=1&code=...&name=... in this browser. The
// extension sees that address (tabs permission), and sends the tab to its own status page, which asks
// the person to allow it (one click, which also grants the screenshot permission Chrome only gives on
// a click). The API is always the origin of that page: another site cannot point the pairing elsewhere.

const CONNECT_PATH = '/connect-extension';

function connectRequest(rawUrl) {
  let u;
  try {
    u = new URL(rawUrl);
  } catch {
    return null;
  }
  if (!/^https?:$/.test(u.protocol) || u.pathname.replace(/\/+$/, '') !== CONNECT_PATH) return null;
  const p = new URLSearchParams(u.hash.slice(1));
  const code = (p.get('code') || '').trim();
  if (p.get('ap-pair') !== '1' || !code) return null;
  return { apiUrl: u.origin, code, name: (p.get('name') || '').trim(), workspace: (p.get('ws') || '').trim() };
}

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  const req = changeInfo.url ? connectRequest(changeInfo.url) : null;
  if (!req) return;
  const page = chrome.runtime.getURL('status.html') + '#pair=' + encodeURIComponent(JSON.stringify(req));
  chrome.tabs.update(tabId, { url: page }).catch(() => {});
});

// ---------- cloud: this browser's campaigns in the SIRI AutoPost web app ----------
// The web app shows and edits the same settings as this page (campaigns, groups, posts, media,
// timing, Telegram), plus the run state and log, and sends the buttons (start, stop, test post...)
// as commands. Same rules as the legacy online mode: the first sync with an empty web config uploads
// what this browser has, later the side that changed wins, and when both changed the web wins.
// Runs outside the post queue, so a "stop" from the web arrives while a post is going on.

let cloudStateSent = ''; // state is sent only when it changed

// State and new log lines out; the web revision and waiting commands in.
// wait: the server holds the call (up to ~25 s) until the web app sends a command, so a button on the web
// reaches this browser at once. cloudWake() cuts the wait short; the answer is then { aborted: true }.
async function cloudReport(c, takeCommands, { wait = false } = {}) {
  const { state, logs = [] } = await chrome.storage.local.get(['state', 'logs']);
  const stateJson = JSON.stringify(state || {});
  const fresh = logs.filter((l) => l.t > c.lastLogT).slice(-300);
  const body = { version: chrome.runtime.getManifest().version, takeCommands, logs: fresh };
  if (stateJson !== cloudStateSent) body.state = state || {};
  let res;
  if (wait) {
    const ctrl = new AbortController();
    cloudWaitCtrl = ctrl;
    try {
      res = await cloudApi(c, 'POST', '/api/device/sync', { ...body, wait: true }, { signal: ctrl.signal });
    } catch (e) {
      if (e?.aborted) return { aborted: true, commands: [] };
      throw e;
    } finally {
      if (cloudWaitCtrl === ctrl) cloudWaitCtrl = null;
    }
  } else {
    res = await cloudApi(c, 'POST', '/api/device/sync', body);
  }
  cloudStateSent = stateJson;
  if (fresh.length) await setCloud({ lastLogT: fresh[fresh.length - 1].t });
  return res;
}

// Replaces this browser's settings with the web's.
async function cloudPullConfig(c) {
  const cfg = await cloudApi(c, 'GET', '/api/device/config');
  if (!cfg.settings) {
    // Nothing on the web yet: keep ours, just remember where we are.
    const { hash } = await localSettings();
    await setCloud({ configRevision: cfg.revision, configHash: hash });
    return;
  }
  const incoming = migrateSettings(cfg.settings);
  const existing = new Set((await storageKeys()).filter((k) => k.startsWith('img:')).map((k) => k.slice(4)));
  let notFound = 0;
  for (const id of imageIdsOf(incoming.campaigns)) {
    if (existing.has(id)) continue;
    try {
      const rec = await cloudApi(c, 'GET', `/api/device/images/${encodeURIComponent(id)}`);
      await chrome.storage.local.set({ ['img:' + id]: rec });
      existing.add(id);
    } catch (e) {
      if (e.status !== 404) throw e;
      notFound++;
    }
  }
  const { settings: current } = await localSettings();
  // Same rule as a config file: no Bot Token on the web = keep this computer's Telegram.
  const res = applyImport(current, { settings: incoming, images: {} }, 'replace', existing);
  await chrome.storage.local.set({ settings: res.settings, cloudPulled: { at: Date.now(), revision: cfg.revision } });
  const used = new Set(res.settings.campaigns.flatMap(campaignImageIds));
  const orphan = (await storageKeys()).filter((k) => k.startsWith('img:') && !used.has(k.slice(4)));
  if (orphan.length) await chrome.storage.local.remove(orphan);
  const { hash } = await localSettings();
  await setCloud({ configRevision: cfg.revision, configHash: hash });
  const sum = summarize(res.settings, {});
  await log(
    'success',
    `[เว็บ AutoPost] โหลดการตั้งค่าจากเว็บแล้ว (ฉบับที่ ${cfg.revision}): ${sum.campaigns} ชุด · ${sum.groups} กลุ่ม · ${sum.posts} แบบโพสต์` +
      (notFound ? ` (ไม่มีรูปบนเว็บ ${notFound} รูป)` : '')
  );
}

// Sends this browser's settings (and the media the web lacks) up. baseRevision null = overwrite.
async function cloudPushConfig(c, local, baseRevision, { quiet = false } = {}) {
  const ids = imageIdsOf(local.settings.campaigns);
  if (ids.length) {
    const { missing } = await cloudApi(c, 'POST', '/api/device/images/missing', { ids });
    for (const id of missing) {
      const rec = (await chrome.storage.local.get('img:' + id))['img:' + id];
      if (rec) await cloudApi(c, 'PUT', `/api/device/images/${encodeURIComponent(id)}`, rec);
    }
  }
  try {
    const r = await cloudApi(c, 'PUT', '/api/device/config', { settings: local.settings, baseRevision });
    await setCloud({ configRevision: r.revision, configHash: local.hash });
    if (!quiet) await log('success', `[เว็บ AutoPost] อัปโหลดการตั้งค่าของเครื่องนี้ขึ้นเว็บแล้ว (ฉบับที่ ${r.revision})`);
  } catch (e) {
    if (e.status !== 409) throw e;
    await log('warn', '[เว็บ AutoPost] การตั้งค่าบนเว็บถูกแก้ไปก่อน: ใช้ของเว็บแทนการแก้ในเครื่องนี้');
    await cloudPullConfig(c);
  }
}

// mode 'auto': pull web changes, push local edits. 'pull' / 'push': replace one side with the other.
async function cloudSyncSettings(c, server, mode) {
  const local = await localSettings();
  if (mode === 'push') return cloudPushConfig(c, local, null);
  if (mode === 'pull') return cloudPullConfig(c);
  // First sync with an empty web config: upload what this browser has.
  if (!c.configRevision && !server.hasContent && hasContent(local.settings)) {
    return cloudPushConfig(c, local, server.revision);
  }
  const localEdited = !!c.configHash && local.hash !== c.configHash;
  if (server.revision !== c.configRevision || !c.configHash) {
    if (localEdited) await log('warn', '[เว็บ AutoPost] การตั้งค่าถูกแก้ทั้งในเครื่องนี้และบนเว็บ: ใช้ของเว็บ');
    return cloudPullConfig(c);
  }
  if (localEdited) return cloudPushConfig(c, local, c.configRevision, { quiet: true });
}

async function doCloudConfigSync({ mode = 'auto', wait = false } = {}) {
  const c = await getCloud();
  if (!c.enabled || !c.apiUrl || !c.deviceKey) return { ok: false, error: 'ยังไม่ได้จับคู่กับเว็บ AutoPost' };
  try {
    const s = await cloudReport(c, true, { wait });
    if (s.aborted) return { ok: true, aborted: true }; // woken up: the sync queued behind this one takes over
    // The commands came with the answer: a settings sync that fails must not make them vanish. They run after
    // the sync, so a button pressed right after an edit on the web finds the edit already here.
    let syncError = null;
    try {
      await cloudSyncSettings(await getCloud(), s, mode);
    } catch (e) {
      if (e?.status === 401) throw e;
      syncError = e;
    }
    const report = (id, result) => cloudApi(c, 'POST', `/api/device/commands/${id}/result`, { result });
    // Report the new state right away so the web page sees it.
    if (await runRemoteCommands(s.commands || [], report)) await cloudReport(await getCloud(), false);
    if (syncError) throw syncError;
    await setCloud({ configSyncAt: Date.now(), configError: '' });
    return { ok: true };
  } catch (e) {
    const msg = e?.status === 404 ? 'เว็บ AutoPost เวอร์ชันนี้ยังไม่รองรับการแก้ชุดโพสต์บนเว็บ' : e?.message || String(e);
    if (e?.status === 401) return cloudForget(msg);
    const prev = (await getCloud()).configError;
    await setCloud({ configError: msg });
    if (msg !== prev) await log('warn', `[เว็บ AutoPost] ซิงก์การตั้งค่าไม่สำเร็จ: ${msg}`);
    return { ok: false, error: msg };
  }
}

// One sync at a time; requests made while one waits join it.
let cloudConfigChain = Promise.resolve();
let cloudConfigQueued = null;
function cloudConfigSync(opts = {}) {
  // A sync that must run now (an edit, a button, the alarm) cuts a waiting one short so it is not stuck behind it.
  if (!opts.wait) cloudWake();
  if (cloudConfigQueued) {
    if (opts.mode) cloudConfigQueued.opts.mode = opts.mode;
    if (!opts.wait) cloudConfigQueued.opts.wait = false;
    return cloudConfigQueued.promise;
  }
  const entry = { opts: { ...opts } };
  entry.promise = cloudConfigChain.then(() => {
    cloudConfigQueued = null;
    return doCloudConfigSync(entry.opts);
  });
  cloudConfigQueued = entry;
  cloudConfigChain = entry.promise.catch(() => {});
  return entry.promise;
}

// Local edits reach the web a few seconds after the last change.
let cloudPushTimer = null;
function scheduleCloudPush() {
  clearTimeout(cloudPushTimer);
  cloudPushTimer = setTimeout(async () => {
    if ((await getCloud()).enabled) cloudConfigSync();
  }, PUSH_DELAY_MS);
}

// ---------- wiring ----------

async function openDashboard() {
  const url = chrome.runtime.getURL('status.html');
  const [tab] = await chrome.tabs.query({ url });
  if (tab) {
    await chrome.tabs.update(tab.id, { active: true });
    await chrome.windows.update(tab.windowId, { focused: true });
  } else {
    await chrome.tabs.create({ url });
  }
}

chrome.action.onClicked.addListener(() => {
  openDashboard();
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === SYNC_ALARM) {
    syncOnline();
    return;
  }
  if (alarm.name === CLOUD_ALARM) {
    enqueue(() => cloudTick());
    // The listener reports state and log on every round; when it is not running (the worker was just
    // woken), do one plain sync and start it again.
    if (!cloudListening) cloudConfigSync();
    cloudListen();
    return;
  }
  if (alarm.name.startsWith(ALARM_PREFIX)) {
    const cid = alarm.name.slice(ALARM_PREFIX.length);
    enqueue(() => tick(cid));
  }
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes.settings) {
    enqueue(() => syncCampaigns(changes.settings.oldValue, changes.settings.newValue));
    schedulePush();
    scheduleCloudPush();
  }
});

async function recover({ freshBrowser }) {
  const patch = { testing: false, current: null };
  // Tab ids are reused after a browser restart, never trust the old ones.
  if (freshBrowser) Object.assign(patch, { tabId: null, windowId: null });
  await chrome.alarms.clear('fbap-tick'); // version 1 alarm
  const state = await setState(patch);
  if (!state.running) return;
  const settings = await getSettings();
  let resumed = 0;
  for (const c of settings.campaigns) {
    const cs = cstate(state, c.id);
    if (!isScheduled(c, cs)) continue; // never revive a campaign Start skipped
    if (await chrome.alarms.get(alarmName(c.id))) continue;
    // Keep the planned time (next round, long break); only overdue ones run soon.
    const at = Math.max(cs.nextAt || 0, Date.now() + randInt(60, 180) * 1000);
    await scheduleC(c.id, at, cs.nextKind);
    resumed++;
  }
  if (resumed) await log('info', `กู้คืนตารางเวลา ${resumed} ชุด หลังเปิดเบราว์เซอร์/อัปเดตส่วนขยาย`);
}

// One-time text fixes of the saved posts (and campaign footers), each applied
// once per id when the extension starts or is updated.
const DATA_FIXES = [
  {
    id: 'line-siristudiophoto',
    note: 'ไลน์เก่า @970wfiou / lin.ee → @siristudiophoto',
    pairs: [
      ['https://lin.ee/c2I3lh4', 'https://line.me/R/ti/p/@siristudiophoto'],
      ['@970wfiou', '@siristudiophoto'],
      ['@siristuiophoto', '@siristudiophoto'],
    ],
  },
  {
    id: 'line-teach-pp',
    note: 'teach_pp → @siristudiophoto',
    pairs: [
      ['@teach_pp', '@siristudiophoto'],
      ['teach_pp', '@siristudiophoto'],
    ],
  },
  {
    id: 'footer-order-online',
    note: 'หัวโพสต์: สั่งรูปออนไลน์ / ดูผลงาน / จองคิว',
    footer: [
      'สั่งรูปออนไลน์ได้ด้วยตัวเอง ไม่ต้องไปร้าน : https://www.siristudiophoto.com',
      'ดูผลงาน : https://www.siristudiophoto.com/index/portfolio',
      'สั่งงาน สอบถาม จองคิว คลิก https://lin.ee/c2I3lh4',
    ].join('\n'),
  },
];

async function applyDataFixes() {
  const { settings: raw, dataFixes = [] } = await chrome.storage.local.get(['settings', 'dataFixes']);
  const todo = DATA_FIXES.filter((f) => !dataFixes.includes(f.id));
  if (!todo.length) return;
  const done = [...dataFixes, ...todo.map((f) => f.id)];
  if (!raw) return chrome.storage.local.set({ dataFixes: done }); // fresh install: nothing old
  const settings = migrateSettings(raw);
  for (const f of todo) {
    if (f.footer) {
      // Same header on top of every post of every campaign.
      for (const c of settings.campaigns) {
        c.config.footer = f.footer;
        c.config.footerPosition = 'top';
      }
      await log('info', `ตั้งหัวโพสต์ ${settings.campaigns.length} ชุด (${f.note})`);
    }
    const n = replaceInPosts(settings, f.pairs || []);
    if (n) await log('info', `แก้ข้อความในโพสต์ ${n} จุด (${f.note})`);
  }
  await chrome.storage.local.set({ settings, dataFixes: done });
}

chrome.runtime.onStartup.addListener(() => {
  recover({ freshBrowser: true });
  enqueue(() => applyDataFixes());
  enqueue(() => loadBundledConfig({ auto: true }));
  ensureSyncAlarm().then(() => syncOnline());
  ensureCloudAlarm();
});
chrome.runtime.onInstalled.addListener(() => {
  recover({ freshBrowser: false });
  enqueue(() => applyDataFixes());
  enqueue(() => loadBundledConfig({ auto: true }));
  ensureSyncAlarm().then(() => syncOnline());
  ensureCloudAlarm();
});

const commands = {
  start: () => start(),
  stop: () => stop(),
  runNow: (m) => runNow(m.campaignId),
  testPost: (m) => testPost(m.campaignId, m.url, m.postId),
  tgTest: () => tgTest(),
  tgFindChats: () => tgFindChats(),
  loadBundledConfig: () => enqueue(() => loadBundledConfig({ auto: false })),
  onlineConnect: (m) => onlineConnect(m),
  onlineDisconnect: () => onlineDisconnect(),
  onlineSync: (m) => syncOnline({ mode: m.mode || 'auto' }),
  cloudPair: (m) => cloudPair(m),
  cloudUnpair: () => cloudUnpair(),
  // Settings first (so new groups reach the web), then one job.
  cloudSync: async () => {
    const cfg = await cloudConfigSync();
    if (!cfg.ok && !(await getCloud()).enabled) return cfg; // unbound in the web app
    return enqueue(() => cloudTick());
  },
  cloudConfig: (m) => cloudConfigSync({ mode: m.mode || 'auto' }),
};

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (!msg || msg.target !== 'fbap-bg') return false;
  const fn = commands[msg.cmd];
  if (!fn) {
    sendResponse({ ok: false, error: 'unknown command' });
    return false;
  }
  Promise.resolve()
    .then(() => fn(msg))
    .then(sendResponse, (e) => sendResponse({ ok: false, error: e?.message || String(e) }));
  return true;
});
