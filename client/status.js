// The extension's own page: status only. Everything is set up in the AutoPost web app (campaigns,
// groups, posts, timing, Telegram, start/stop, pairing and unbinding); this page shows what this
// browser is doing and asks for the one click Chrome requires when the web app pairs it.
import { migrateSettings, activeGroups, usablePosts, campaignProblem, fmtDateTime, fmtDuration } from './lib/shared.js';

const $ = (sel) => document.querySelector(sel);
const CAPTURE_PERM = { origins: ['<all_urls>'] };

let data = { settings: migrateSettings(null), state: {}, logs: [], cloud: {}, online: {} };
// Set when this page has just paired the browser: the page then says "connected" and nothing else.
let justPaired = false;

function bg(cmd, extra = {}) {
  return chrome.runtime.sendMessage({ target: 'fbap-bg', cmd, ...extra });
}

function show(el, on) {
  el.hidden = !on;
}

function facts(el, rows) {
  el.textContent = '';
  for (const [k, v] of rows) {
    if (v === null || v === undefined || v === '') continue;
    const dt = document.createElement('dt');
    dt.textContent = k;
    const dd = document.createElement('dd');
    dd.textContent = v;
    el.append(dt, dd);
  }
}

// ---------- pairing asked by the web app ----------

function pairRequest() {
  const m = location.hash.match(/^#pair=(.+)$/);
  if (!m) return null;
  try {
    const req = JSON.parse(decodeURIComponent(m[1]));
    return req && req.apiUrl && req.code ? req : null;
  } catch {
    return null;
  }
}

function renderPair() {
  const req = pairRequest();
  show($('#pairCard'), !!req);
  if (!req) return;
  $('#pairOrigin').textContent = req.apiUrl + (req.workspace ? ` (เวิร์กสเปซ "${req.workspace}")` : '');
  $('#pairName').textContent = req.name || 'ตั้งชื่อให้อัตโนมัติ';
  const c = data.cloud;
  const replace = $('#pairReplace');
  show(replace, !!c.enabled);
  if (c.enabled) {
    replace.textContent = `เครื่องนี้เชื่อมกับเวิร์กสเปซ "${c.workspaceName || '-'}" อยู่แล้ว การอนุญาตจะย้ายไปเชื่อมกับเว็บนี้แทน (ยกเลิกการผูกเครื่องเดิมได้ที่หน้าเว็บเดิม)`;
  }
}

function pairMessage(text) {
  const el = $('#pairMsg');
  el.textContent = text;
  el.hidden = !text;
}

$('#btnAllow').addEventListener('click', async () => {
  const req = pairRequest();
  if (!req) return;
  $('#btnAllow').disabled = true;
  $('#btnDeny').disabled = true;
  // Chrome grants this only inside a click: ask now, so screenshots work without another visit here.
  try {
    await chrome.permissions.request(CAPTURE_PERM);
  } catch {
    /* refused: notices go without screenshots */
  }
  pairMessage('กำลังเชื่อมต่อ...');
  const r = await bg('cloudPair', { apiUrl: req.apiUrl, code: req.code, name: req.name });
  if (r?.ok) {
    // No redirect to the web app: this browser may not be signed in there, or as another person. Just say it worked.
    justPaired = true;
    history.replaceState(null, '', location.pathname);
    renderAll();
  } else {
    pairMessage(`เชื่อมต่อไม่สำเร็จ: ${r?.error || 'ไม่ทราบสาเหตุ'} (สร้างรหัสใหม่จากหน้าเว็บแล้วกดเชื่อมต่ออีกครั้ง)`);
    $('#btnDeny').disabled = false;
  }
});

$('#btnCloseTab').addEventListener('click', () => {
  window.close();
});

$('#btnDeny').addEventListener('click', () => {
  history.replaceState(null, '', location.pathname);
  renderAll();
});

// ---------- connection ----------

function renderLink() {
  const c = data.cloud;
  const paired = !!c.enabled;
  const badge = $('#linkBadge');
  const err = c.lastError || c.configError || '';
  badge.className = 'badge' + (paired ? (err ? ' bad' : ' on') : '');
  badge.textContent = !paired ? 'ยังไม่ได้เชื่อมต่อ' : err ? 'มีปัญหา' : 'เชื่อมต่อแล้ว';
  show($('#unpairedCard'), !paired && !pairRequest());
  show($('#linkCard'), paired);
  if (!paired) return;
  $('#openWeb').href = `${c.apiUrl}/app/overview`;
  const job = c.lastJob;
  const resting = c.pausedUntil > Date.now();
  facts($('#linkFacts'), [
    ['เวิร์กสเปซ', c.workspaceName || '-'],
    ['ชื่อเครื่อง', c.deviceName || '-'],
    ['เว็บ', c.apiUrl],
    ['ซิงก์ล่าสุด', c.lastSyncAt ? fmtDateTime(c.lastSyncAt) : '-'],
    ['ชุดโพสต์บนเว็บ', c.configSyncAt ? `ฉบับที่ ${c.configRevision || 0} (ซิงก์ ${fmtDateTime(c.configSyncAt)})` : 'ยังไม่ได้ซิงก์'],
    ['งานโพสต์จากเว็บ', c.paused ? 'พักรับงาน (ตั้งจากหน้าเว็บ)' : resting ? `พักหลัง Facebook แจ้งเตือนถึง ${fmtDateTime(c.pausedUntil)}` : `รับงาน · ${c.groups || 0} กลุ่ม`],
    ['งานล่าสุด', job ? `${fmtDateTime(job.at)} ${job.ok ? 'สำเร็จ' : `ไม่สำเร็จ: ${job.error}`} (${job.group})` : ''],
  ]);
  const e = $('#linkError');
  show(e, !!err);
  e.textContent = err ? `⚠ ${err}` : '';
}

$('#btnSync').addEventListener('click', async () => {
  const btn = $('#btnSync');
  btn.disabled = true;
  try {
    await bg('cloudSync');
  } finally {
    btn.disabled = false;
  }
});

// ---------- run ----------

const KIND_TEXT = {
  group: 'รอโพสต์กลุ่มถัดไป',
  round: 'พักระหว่างรอบ',
  active: 'รอช่วงเวลาทำงาน',
  wait: 'รอคิว (เว้นห่างจากโพสต์ก่อนหน้า)',
  posting: 'กำลังโพสต์',
  paused: 'พักอัตโนมัติ',
  daily: 'ครบโควตาวันนี้ รอพรุ่งนี้',
  cooldown: 'รอกลุ่มที่เหลือพักครบเวลา',
};

function campaignView(c, state) {
  const cs = (state.campaigns || {})[c.id];
  const problem = campaignProblem(c);
  if (!c.enabled) return { cls: 'off', label: 'ปิดอยู่', cs, problem };
  if (state.current && state.current.campaignId === c.id) return { cls: 'busy', label: 'กำลังโพสต์', cs, problem };
  if (!state.running) return { cls: 'off', label: problem ? 'ยังไม่พร้อม' : 'พร้อม (ยังไม่เริ่ม)', cs, problem };
  if (!cs) return { cls: 'off', label: problem ? 'ยังไม่พร้อม' : 'รอเริ่ม', cs, problem };
  if (cs.finished) return { cls: 'done', label: 'ทำครบแล้ว', cs, problem };
  return { cls: 'on', label: KIND_TEXT[cs.nextKind] || 'รอ', cs, problem };
}

function campaignMeta(c, v, state) {
  const parts = [];
  if (v.cs && v.cs.round) {
    const total = (v.cs.queue || []).length;
    parts.push(`รอบ ${v.cs.round}`, `กลุ่ม ${Math.min(v.cs.pos || 0, total)}/${total}`);
  } else {
    parts.push(`${activeGroups(c).length} กลุ่ม`, `${usablePosts(c.posts).length} แบบโพสต์`);
  }
  if (state.running && c.enabled && v.cs && v.cs.nextAt && !v.cs.busy && !v.cs.finished) {
    parts.push(`ถัดไป ${fmtDateTime(v.cs.nextAt)} (อีก ${fmtDuration(v.cs.nextAt - Date.now())})`);
  }
  if (v.cs && v.cs.stats) {
    const s = v.cs.stats;
    parts.push(`สำเร็จ ${s.ok || 0} · ล้มเหลว ${s.fail || 0} · ข้าม ${s.skip || 0}`);
  }
  if (v.problem && c.enabled) parts.push(`⚠ ${v.problem}`);
  return parts.join(' · ');
}

function guardLine(state, settings) {
  const parts = [];
  const d = state.daily || {};
  const today = new Date();
  const key = `${today.getFullYear()}-${today.getMonth() + 1}-${today.getDate()}`;
  const count = d.date === key ? d.count || 0 : 0;
  const limit = settings.global.dailyMaxPosts;
  const cap = !limit ? 0 : d.date === key && d.cap && d.base === limit ? d.cap : limit;
  parts.push(cap ? `วันนี้โพสต์แล้ว ${count} / ${cap} โพสต์` : `วันนี้โพสต์แล้ว ${count} โพสต์ (ไม่จำกัดต่อวัน)`);
  if (state.pausedUntil && state.pausedUntil > Date.now()) {
    parts.push(`⏸ พักทุกชุดถึง ${fmtDateTime(state.pausedUntil)} (อีก ${fmtDuration(state.pausedUntil - Date.now())}) สาเหตุ: ${state.pauseReason || '-'}`);
  }
  return parts.join(' · ');
}

function renderRun() {
  const { settings, state, cloud } = data;
  const hasCampaigns = settings.campaigns.length > 0;
  show($('#runCard'), !!cloud.enabled || hasCampaigns);
  const badge = $('#runBadge');
  const busy = !!state.current;
  badge.className = 'badge' + (busy ? ' busy' : state.running ? ' on' : '');
  badge.textContent = busy ? 'กำลังโพสต์' : state.running ? 'ทำงานอยู่' : 'หยุด';

  const line = $('#currentLine');
  let text = '';
  if (state.current) {
    const c = settings.campaigns.find((x) => x.id === state.current.campaignId);
    const from = state.current.cloud ? 'เว็บ AutoPost → ' : c ? c.name + ' → ' : '';
    text = `${state.testing ? '[ทดสอบ] ' : ''}กำลังโพสต์: ${from}${state.current.url}`;
  } else if (state.running && state.pausedUntil > Date.now()) {
    text = `⏸ พักอัตโนมัติถึง ${fmtDateTime(state.pausedUntil)}: ${state.pauseReason || ''}`;
  }
  line.textContent = text;
  show(line, !!text);
  $('#guardLine').textContent = guardLine(state, settings);

  const box = $('#campaigns');
  box.textContent = '';
  if (!hasCampaigns) {
    box.textContent = 'ยังไม่มีชุดโพสต์ (สร้างได้ที่หน้าเว็บ > ชุดโพสต์)';
    return;
  }
  for (const c of settings.campaigns) {
    const v = campaignView(c, state);
    const row = document.createElement('div');
    row.className = 'camp';
    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = c.name || 'ไม่มีชื่อ';
    const st = document.createElement('span');
    st.className = `state ${v.cls}`;
    st.textContent = v.label;
    const meta = document.createElement('span');
    meta.className = 'meta';
    meta.textContent = campaignMeta(c, v, state);
    row.append(name, st, meta);
    box.append(row);
  }
}

async function renderCapture() {
  const t = data.settings.global.telegram;
  let missing = false;
  if (t.enabled && t.screenshot) {
    try {
      missing = !(await chrome.permissions.contains(CAPTURE_PERM));
    } catch {
      missing = false;
    }
  }
  show($('#captureWarn'), missing);
}

// The one permission Chrome only grants on a click inside the extension.
$('#btnCapture').addEventListener('click', async () => {
  try {
    await chrome.permissions.request(CAPTURE_PERM);
  } catch {
    /* refused */
  }
  renderCapture();
});

function renderLogs() {
  const logs = data.logs || [];
  show($('#logCard'), logs.length > 0);
  const ol = $('#logs');
  ol.textContent = '';
  for (const l of logs.slice(-150).reverse()) {
    const li = document.createElement('li');
    li.className = l.level;
    const t = document.createElement('time');
    t.textContent = fmtDateTime(l.t);
    const s = document.createElement('span');
    s.textContent = l.msg;
    li.append(t, s);
    ol.append(li);
  }
}

function renderLegacy() {
  const o = data.online || {};
  const note = $('#legacyNote');
  show(note, !!o.enabled);
  note.textContent = o.enabled ? `เชื่อมกับ server เดิม (ตั้งค่าออนไลน์) อยู่ด้วย: ${o.serverUrl || ''}` : '';
}

function renderAll() {
  // Right after pairing the page shows the success card and nothing else.
  if (justPaired && data.cloud.enabled) {
    for (const id of ['#pairCard', '#unpairedCard', '#linkCard', '#runCard', '#logCard', '#legacyNote']) show($(id), false);
    show($('#pairDone'), true);
    const badge = $('#linkBadge');
    badge.className = 'badge on';
    badge.textContent = 'เชื่อมต่อแล้ว';
    return;
  }
  show($('#pairDone'), false);
  renderPair();
  renderLink();
  renderRun();
  renderLogs();
  renderLegacy();
  renderCapture();
}

async function load() {
  const got = await chrome.storage.local.get(['settings', 'state', 'logs', 'cloud', 'online']);
  data = {
    settings: migrateSettings(got.settings),
    state: got.state || {},
    logs: got.logs || [],
    cloud: got.cloud || {},
    online: got.online || {},
  };
  renderAll();
}

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local') return;
  if (['settings', 'state', 'logs', 'cloud', 'online'].some((k) => changes[k])) load();
});
window.addEventListener('hashchange', renderAll);
setInterval(() => {
  if (justPaired) return;
  renderRun();
  renderLink();
}, 1000);

load();
