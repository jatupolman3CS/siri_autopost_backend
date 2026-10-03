import {
  DEFAULT_CONFIG,
  DEFAULT_GLOBAL,
  migrateSettings,
  newCampaign,
  newPost,
  normalizeGroupUrl,
  activeGroups,
  usablePosts,
  postsForGroup,
  groupsWithoutPosts,
  replaceInPosts,
  parseGroupList,
  groupDailyMax,
  campaignImageIds,
  postMediaIds,
  composeText,
  splitPageTags,
  campaignProblem,
  pick,
  uid,
  fmtDateTime,
  fmtDuration,
} from './lib/shared.js';
import { buildBackup, parseBackup, applyImport, summarize, imageIdsOf, BUNDLED_CONFIG_PATH } from './lib/backup.js';
import { readZip } from './lib/zip.js';
import { convertSiriExport, nameFromFile } from './lib/siri-import.js';

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

let settings = migrateSettings(null);
let state = {};
let currentId = null;
let saveTimer = null;
let testTimer = null;
const thumbCache = new Map();

const cur = () => settings.campaigns.find((c) => c.id === currentId) || null;

const shortGroup = (url) => String(url).replace('https://www.facebook.com/groups/', 'กลุ่ม ').replace(/\/$/, '');

// Display name of a group url inside a campaign.
function groupLabel(c, url) {
  const g = c.groups.find((x) => normalizeGroupUrl(x.url) === url);
  return (g && g.name.trim()) || shortGroup(url);
}

// Valid, de-duplicated groups of a campaign (enabled or not).
function validGroups(c) {
  const seen = new Set();
  const out = [];
  for (const g of c.groups) {
    const url = normalizeGroupUrl(g.url);
    if (!url || seen.has(url)) continue;
    seen.add(url);
    out.push({ url, name: g.name, enabled: g.enabled });
  }
  return out;
}

// ---------- helpers ----------

function bg(cmd, extra = {}) {
  return chrome.runtime.sendMessage({ target: 'fbap-bg', cmd, ...extra });
}

let toastTimer = null;
function toast(msg, ms = 3500) {
  const el = $('#toast');
  el.textContent = msg;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (el.hidden = true), ms);
}

function setSaveState(text) {
  $('#saveState').textContent = text;
}

function readField(el, defaults) {
  if (el.type === 'checkbox') return el.checked;
  if (el.type === 'number') {
    const v = parseFloat(el.value);
    const min = el.min !== '' && Number.isFinite(Number(el.min)) ? Number(el.min) : 0;
    return Number.isFinite(v) ? Math.max(min, v) : defaults[el.dataset.cfg || el.dataset.global];
  }
  return el.value;
}

function writeField(el, value) {
  if (el.type === 'checkbox') el.checked = !!value;
  else el.value = value ?? '';
}

function scheduleSave() {
  setSaveState('กำลังบันทึก...');
  clearTimeout(saveTimer);
  saveTimer = setTimeout(save, 500);
}

async function save() {
  clearTimeout(saveTimer);
  saveTimer = null;
  await chrome.storage.local.set({ settings });
  setSaveState('บันทึกแล้ว');
}

function rememberCurrent() {
  try {
    localStorage.setItem('fbap.current', currentId || '');
  } catch {
    /* storage blocked: not important */
  }
}

function recallCurrent() {
  try {
    return localStorage.getItem('fbap.current');
  } catch {
    return null;
  }
}

// ---------- campaign status text ----------

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

// Today's count / daily limit and automatic pause.
function renderGuard() {
  const el = $('#guardStatus');
  if (!el) return;
  const parts = [];
  const d = state.daily || {};
  const today = new Date();
  const key = `${today.getFullYear()}-${today.getMonth() + 1}-${today.getDate()}`;
  const count = d.date === key ? d.count || 0 : 0;
  const limit = settings.global.dailyMaxPosts;
  // Today's rolled limit only while it belongs to the current setting.
  const cap = !limit ? 0 : d.date === key && d.cap && d.base === limit ? d.cap : limit;
  parts.push(cap ? `วันนี้โพสต์แล้ว ${count} / ${cap} โพสต์` : `วันนี้โพสต์แล้ว ${count} โพสต์ (ไม่จำกัดต่อวัน)`);
  if (state.pausedUntil && state.pausedUntil > Date.now()) {
    parts.push(`⏸ พักทุกชุดถึง ${fmtDateTime(state.pausedUntil)} (อีก ${fmtDuration(state.pausedUntil - Date.now())}) สาเหตุ: ${state.pauseReason || '-'}`);
  }
  el.textContent = parts.join(' · ');
}

function campaignView(c) {
  const cs = (state.campaigns || {})[c.id];
  const problem = campaignProblem(c);
  if (!c.enabled) return { cls: 'off', label: 'ปิดอยู่', cs, problem };
  if (state.current && state.current.campaignId === c.id) return { cls: 'busy', label: 'กำลังโพสต์', cs, problem };
  if (!state.running) return { cls: 'off', label: problem ? 'ยังไม่พร้อม' : 'พร้อม (ยังไม่เริ่ม)', cs, problem };
  if (!cs) return { cls: 'off', label: problem ? 'ยังไม่พร้อม' : 'รอเริ่ม', cs, problem };
  if (cs.finished) return { cls: 'done', label: 'ทำครบแล้ว', cs, problem };
  return { cls: 'on', label: KIND_TEXT[cs.nextKind] || 'รอ', cs, problem };
}

function campaignMeta(c, v) {
  const parts = [];
  const groups = activeGroups(c).length;
  if (v.cs && v.cs.round) {
    const total = (v.cs.queue || []).length;
    parts.push(`รอบ ${v.cs.round}`, `กลุ่ม ${Math.min(v.cs.pos || 0, total)}/${total}`);
  } else {
    parts.push(`${groups} กลุ่ม`, `${usablePosts(c.posts).length} แบบโพสต์`);
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

// ---------- tabs ----------

function renderTabs() {
  const nav = $('#tabs');
  nav.textContent = '';
  for (const c of settings.campaigns) {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'tab' + (c.id === currentId ? ' active' : '');
    const dot = document.createElement('span');
    const v = campaignView(c);
    dot.className = 'dot' + (!c.enabled ? ' off' : v.cls === 'on' || v.cls === 'busy' ? ' on' : '');
    b.append(dot, document.createTextNode(c.name || 'ไม่มีชื่อ'));
    b.addEventListener('click', () => selectCampaign(c.id));
    nav.append(b);
  }
  const add = document.createElement('button');
  add.type = 'button';
  add.className = 'tab add';
  add.textContent = '+ เพิ่มชุด';
  add.addEventListener('click', addCampaign);
  nav.append(add);
}

function selectCampaign(id) {
  currentId = id;
  rememberCurrent();
  renderTabs();
  renderCampaign();
  renderOverview();
  refreshTest();
}

async function addCampaign() {
  const c = newCampaign(settings.campaigns.length + 1);
  settings.campaigns.push(c);
  await save();
  selectCampaign(c.id);
  $('#campName').focus();
  $('#campName').select();
}

// ---------- campaign editor ----------

function renderCampaign() {
  const c = cur();
  $('#emptyCampaign').hidden = !!c;
  $('#campaignEditor').hidden = !c;
  if (!c) return;
  $('#campName').value = c.name;
  $('#campEnabled').checked = c.enabled;
  for (const el of $$('[data-cfg]')) writeField(el, c.config[el.dataset.cfg]);
  renderGroups();
  renderLeadMedia();
  renderPosts();
  renderCampStatus();
}

function renderCampStatus() {
  const c = cur();
  if (!c) return;
  const v = campaignView(c);
  $('#campStatus').textContent = `สถานะ: ${v.label} · ${campaignMeta(c, v)}`;
  $('#btnRunNow').disabled = !state.running || !c.enabled || !!state.current;
}

$('#campName').addEventListener('input', () => {
  const c = cur();
  c.name = $('#campName').value;
  renderTabs();
  renderOverview();
  scheduleSave();
});

$('#campEnabled').addEventListener('change', () => {
  const c = cur();
  c.enabled = $('#campEnabled').checked;
  renderTabs();
  renderOverview();
  renderCampStatus();
  save();
});

for (const el of $$('[data-cfg]')) {
  const key = el.dataset.cfg;
  const onChange = () => {
    const c = cur();
    if (!c) return;
    c.config[key] = readField(el, DEFAULT_CONFIG);
    scheduleSave();
  };
  el.addEventListener('input', onChange);
  el.addEventListener('change', onChange);
}

$('#btnRunNow').addEventListener('click', async () => {
  const r = await bg('runNow', { campaignId: currentId });
  toast(r?.ok ? 'ส่งเข้าคิวโพสต์แล้ว' : r?.error || 'ทำไม่ได้');
});

// Images may be shared by several posts; stored copies are removed only when
// nothing refers to them any more (cleanupImages).
$('#btnDupCamp').addEventListener('click', async () => {
  const src = cur();
  const copy = JSON.parse(JSON.stringify(src));
  copy.id = uid();
  copy.name = `${src.name} (สำเนา)`;
  copy.enabled = false;
  for (const p of copy.posts) p.id = uid();
  settings.campaigns.splice(settings.campaigns.indexOf(src) + 1, 0, copy);
  await save();
  selectCampaign(copy.id);
  toast('ทำสำเนาแล้ว (สำเนาปิดอยู่ เปิดใช้เมื่อพร้อม)');
});

$('#btnDelCamp').addEventListener('click', async () => {
  const c = cur();
  if (!confirm(`ลบชุด "${c.name}" ทั้งกลุ่ม เนื้อหา และรูป?`)) return;
  const i = settings.campaigns.indexOf(c);
  settings.campaigns.splice(i, 1);
  await save();
  await cleanupImages();
  const next = settings.campaigns[Math.max(0, i - 1)];
  selectCampaign(next ? next.id : null);
});

// ---------- groups ----------

function renderGroups() {
  const c = cur();
  const box = $('#groupsTable');
  box.textContent = '';
  const tpl = $('#groupTpl');
  for (const g of c.groups) {
    const row = tpl.content.firstElementChild.cloneNode(true);
    row.group = g;
    const en = $('.g-enabled', row);
    const name = $('.g-name', row);
    const url = $('.g-url', row);
    const text = $('.g-text', row);
    const max = $('.g-max', row);
    en.checked = g.enabled;
    name.value = g.name || '';
    url.value = g.url;
    text.value = g.text;
    max.value = g.dailyMax || 0;
    max.addEventListener('input', () => {
      g.dailyMax = groupDailyMax(max.value);
      afterGroupsChange();
    });
    max.addEventListener('change', () => {
      max.value = g.dailyMax || 0; // show what is kept: whole number, 0..50
    });
    const mark = () => {
      row.classList.toggle('invalid', !!g.url.trim() && !normalizeGroupUrl(g.url));
      row.classList.toggle('disabled', !g.enabled);
    };
    mark();
    en.addEventListener('change', () => {
      g.enabled = en.checked;
      mark();
      afterGroupsChange();
    });
    name.addEventListener('input', () => {
      g.name = name.value;
      afterGroupsChange();
    });
    // Posts set for this row follow its link while it is edited.
    // home: the link those posts point to now. followers: the posts that
    // pointed to home when this edit started (none if another row shares it).
    // Half-typed, empty or duplicate links never move them, and every saved
    // state is consistent.
    const usedElsewhere = (u) => c.groups.some((x) => x !== g && normalizeGroupUrl(x.url) === u);
    let home = normalizeGroupUrl(g.url);
    let followers = null;
    url.addEventListener('focus', () => {
      followers = null;
    });
    url.addEventListener('input', () => {
      g.url = url.value.trim();
      if (followers === null) {
        followers = home && !usedElsewhere(home) ? c.posts.filter((p) => p.groupUrls.includes(home)) : [];
      }
      const now = normalizeGroupUrl(g.url);
      if (now && home && now !== home && !usedElsewhere(now)) {
        for (const p of followers) {
          p.groupUrls = [...new Set(p.groupUrls.map((u) => (u === home ? now : u)))];
        }
        home = now;
      } else if (now && !home && !usedElsewhere(now)) {
        home = now; // a new row got its first link
      }
      mark();
      afterGroupsChange();
    });
    text.addEventListener('input', () => {
      g.text = text.value;
      afterGroupsChange();
    });
    $('.g-del', row).addEventListener('click', () => {
      c.groups.splice(c.groups.indexOf(g), 1);
      renderGroups();
      afterGroupsChange();
    });
    // Long links: show the end (the group id) when not editing.
    const showEnd = () => (url.scrollLeft = url.scrollWidth);
    url.addEventListener('blur', showEnd);
    box.append(row);
    requestAnimationFrame(showEnd);
  }
  renderGroupCounts();
  renderGroupsInfo();
}

// "N โพสต์" next to each group: posts this group may use.
function renderGroupCounts() {
  const c = cur();
  if (!c) return;
  for (const row of $$('#groupsTable .group-row')) {
    const url = normalizeGroupUrl(row.group.url);
    const el = $('.g-count', row);
    if (!url) {
      el.textContent = '';
      el.className = 'g-count';
      continue;
    }
    const n = postsForGroup(c, url).length;
    el.textContent = `${n} โพสต์`;
    el.className = 'g-count' + (n === 0 && row.group.enabled ? ' zero' : '');
  }
  renderGroupLimits();
}

// The group's own limit: "โพสต์ได้ [1] /วัน", with today's use (last 24 hours).
function renderGroupLimits() {
  for (const row of $$('#groupsTable .group-row')) {
    const g = row.group;
    const box = $('.g-limit', row);
    const url = normalizeGroupUrl(g.url);
    const used = url ? ((state.groupPostTimes || {})[url] || []).filter((t) => Date.now() - t < 24 * 3600000).length : 0;
    box.classList.toggle('set', g.dailyMax > 0);
    box.classList.toggle('full', g.dailyMax > 0 && used >= g.dailyMax);
    box.title =
      'กฎของกลุ่ม: โพสต์ได้กี่ครั้งใน 24 ชม. (0 = ไม่จำกัด) ครบแล้วระบบข้ามกลุ่มนี้จนพ้น 24 ชม.' +
      (url ? `\nโพสต์ไปแล้วใน 24 ชม.: ${used}${g.dailyMax > 0 ? `/${g.dailyMax}` : ''} ครั้ง` : '');
  }
}

function afterGroupsChange() {
  renderGroupCounts();
  renderGroupsInfo();
  refreshPostTargets();
  renderPostFilter();
  renderOverview();
  clearTimeout(testTimer);
  testTimer = setTimeout(refreshTest, 400);
  scheduleSave();
}

function renderGroupsInfo() {
  const c = cur();
  const info = $('#groupsInfo');
  info.textContent = '';
  const active = activeGroups(c);
  const bad = c.groups.filter((g) => g.url.trim() && !normalizeGroupUrl(g.url)).length;
  const seen = new Set();
  let dup = 0;
  for (const g of c.groups) {
    const u = normalizeGroupUrl(g.url);
    if (!u) continue;
    if (seen.has(u)) dup++;
    seen.add(u);
  }
  const withText = active.filter((g) => g.text.trim()).length;
  const ok = document.createElement('span');
  ok.className = 'good';
  ok.textContent = `ใช้งาน ${active.length} กลุ่ม`;
  info.append(ok, document.createTextNode(` · มีข้อความเฉพาะกลุ่ม ${withText} กลุ่ม`));
  if (bad || dup) {
    const b = document.createElement('div');
    b.className = 'bad';
    b.textContent = [bad ? `ลิงก์ไม่ถูกต้อง ${bad} แถว (กรอบแดง)` : '', dup ? `ลิงก์ซ้ำ ${dup} แถว (ใช้แค่แถวแรก)` : '']
      .filter(Boolean)
      .join(' · ');
    info.append(b);
  }
  const empty = groupsWithoutPosts(c);
  if (empty.length && usablePosts(c.posts).length) {
    const w = document.createElement('div');
    w.className = 'bad';
    const names = empty.slice(0, 5).map((g) => g.name || shortGroup(g.url)).join(', ');
    w.textContent = `กลุ่มที่ยังไม่มีโพสต์ (จะถูกข้าม) ${empty.length} กลุ่ม: ${names}${empty.length > 5 ? ' ...' : ''}`;
    info.append(w);
  }
}

$('#btnAddGroup').addEventListener('click', () => {
  cur().groups.push({ url: '', name: '', text: '', enabled: true, dailyMax: 0 });
  renderGroups();
  const inputs = $$('.g-url');
  inputs[inputs.length - 1].focus();
  afterGroupsChange();
});

// Accepts category headings, "url | group text" and "url (note)" lines
// (a note adds the group switched off, e.g. waiting for admin approval).
$('#btnBulkAdd').addEventListener('click', () => {
  const c = cur();
  const existing = new Map(c.groups.map((g) => [normalizeGroupUrl(g.url), g]).filter(([u]) => u));
  const { groups, invalid } = parseGroupList($('#bulkGroups').value);
  let on = 0;
  let off = 0;
  let dup = 0;
  let recoded = 0;
  for (const g of groups) {
    const old = existing.get(g.url);
    if (old) {
      // Already in the set: a new group text (e.g. a new code) replaces the old one.
      dup++;
      if (g.text && g.text !== old.text) {
        old.text = g.text;
        recoded++;
      }
      continue;
    }
    existing.set(g.url, g);
    c.groups.push({ url: g.url, name: g.name, text: g.text, enabled: g.enabled, dailyMax: g.dailyMax || 0 });
    if (g.enabled) on++;
    else off++;
  }
  $('#bulkGroups').value = invalid.join('\n');
  renderGroups();
  afterGroupsChange();
  toast(
    `เพิ่ม ${on + off} กลุ่ม${off ? ` (ปิดไว้ ${off} กลุ่มที่มีหมายเหตุ)` : ''}${dup ? ` · ซ้ำ ${dup}` : ''}${recoded ? ` (เปลี่ยนข้อความเฉพาะกลุ่ม ${recoded})` : ''}` +
      `${invalid.length ? ` · บรรทัดที่อ่านไม่ได้ ${invalid.length} (เหลือไว้ในช่อง)` : ''}`,
    6000
  );
});

// ---------- posts ----------

function readAsDataURL(file) {
  return new Promise((resolve, reject) => {
    const fr = new FileReader();
    fr.onload = () => resolve(fr.result);
    fr.onerror = () => reject(fr.error);
    fr.readAsDataURL(file);
  });
}

function loadImage(src) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = reject;
    img.src = src;
  });
}

const MAX_VIDEO_BYTES = 200 * 1024 * 1024;

// Large photos are downscaled so storage stays small; Facebook resizes anyway.
// Videos are stored as they are.
async function processImage(file) {
  let type = file.type || 'image/jpeg';
  let name = file.name || 'image.jpg';
  if (type.startsWith('video/')) {
    if (file.size > MAX_VIDEO_BYTES) throw new Error(`วิดีโอใหญ่เกิน ${MAX_VIDEO_BYTES / 1024 / 1024} MB`);
    return { name, type, data: await readAsDataURL(file) };
  }
  let data = await readAsDataURL(file);
  if (file.size > 2.5 * 1024 * 1024 && /^image\/(jpeg|png|webp|bmp)$/.test(type)) {
    const img = await loadImage(data);
    const scale = Math.min(1, 2048 / Math.max(img.naturalWidth, img.naturalHeight));
    const canvas = document.createElement('canvas');
    canvas.width = Math.round(img.naturalWidth * scale);
    canvas.height = Math.round(img.naturalHeight * scale);
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
    data = canvas.toDataURL('image/jpeg', 0.9);
    type = 'image/jpeg';
    name = name.replace(/\.[^.]+$/, '') + '.jpg';
  }
  return { name, type, data };
}

async function thumbSrc(id) {
  if (thumbCache.has(id)) return thumbCache.get(id);
  const key = 'img:' + id;
  const rec = (await chrome.storage.local.get(key))[key];
  const src = rec ? rec.data : '';
  thumbCache.set(id, src);
  return src;
}

// Loads every thumbnail of a campaign in one storage read.
async function prefetchThumbs(c) {
  const ids = campaignImageIds(c).filter((id) => !thumbCache.has(id));
  if (!ids.length) return;
  const data = await chrome.storage.local.get(ids.map((id) => 'img:' + id));
  for (const id of ids) thumbCache.set(id, data['img:' + id] ? data['img:' + id].data : '');
}

// <img> for photos, muted <video> for videos.
function mediaEl(src) {
  if (src.startsWith('data:video/')) {
    const v = document.createElement('video');
    v.muted = true;
    v.preload = 'metadata';
    v.src = src;
    return v;
  }
  const img = document.createElement('img');
  img.alt = '';
  img.loading = 'lazy';
  img.src = src;
  return img;
}

function videoBadge(wrap, src) {
  if (!src.startsWith('data:video/')) return;
  const b = document.createElement('span');
  b.className = 'badge-video';
  b.textContent = 'วิดีโอ';
  wrap.append(b);
}

// Campaign lead media: attached first to every post, in this order.
async function renderLeadMedia() {
  const c = cur();
  const box = $('#leadMedia');
  box.textContent = '';
  if (!c) return;
  if (!c.leadImageIds.length) {
    box.textContent = 'ยังไม่มี';
    return;
  }
  for (const [i, id] of c.leadImageIds.entries()) {
    const src = await thumbSrc(id);
    const wrap = document.createElement('div');
    wrap.className = 'thumb';
    wrap.append(mediaEl(src));
    videoBadge(wrap, src);
    const del = document.createElement('button');
    del.type = 'button';
    del.title = 'เอาออก';
    del.textContent = '×';
    del.addEventListener('click', async () => {
      c.leadImageIds = c.leadImageIds.filter((x) => x !== id);
      await save();
      await cleanupImages();
      renderLeadMedia();
    });
    wrap.append(del);
    if (i > 0) {
      const left = document.createElement('button');
      left.type = 'button';
      left.className = 'move';
      left.title = 'เลื่อนขึ้นก่อน';
      left.textContent = '◀';
      left.addEventListener('click', async () => {
        const ids = c.leadImageIds;
        [ids[i - 1], ids[i]] = [ids[i], ids[i - 1]];
        await save();
        renderLeadMedia();
      });
      wrap.append(left);
    }
    box.append(wrap);
  }
}

$('#leadInput').addEventListener('change', async (ev) => {
  const input = ev.target;
  const files = [...input.files];
  input.value = '';
  const c = cur();
  if (!files.length || !c) return;
  setSaveState('กำลังเพิ่มสื่อ...');
  try {
    for (const f of files) {
      const rec = await processImage(f);
      const id = uid();
      await chrome.storage.local.set({ ['img:' + id]: rec });
      thumbCache.set(id, rec.data);
      c.leadImageIds.push(id);
    }
    await save();
  } catch (e) {
    toast('เพิ่มไฟล์ไม่สำเร็จ: ' + (e?.message || e));
  }
  renderLeadMedia();
});

async function renderImages(post, box) {
  box.textContent = '';
  for (const id of post.imageIds) {
    const wrap = document.createElement('div');
    wrap.className = 'thumb';
    const src = await thumbSrc(id);
    const img = mediaEl(src);
    videoBadge(wrap, src);
    const del = document.createElement('button');
    del.type = 'button';
    del.title = 'ลบรูป';
    del.textContent = '×';
    del.addEventListener('click', async () => {
      post.imageIds = post.imageIds.filter((x) => x !== id);
      await save();
      await cleanupImages();
      renderImages(post, box);
      scheduleCountsRefresh();
    });
    wrap.append(img, del);
    box.append(wrap);
  }
}

// Group text used for previews: any group (every post fits every group),
// preferring one that has its own text.
function sampleGroup(c, post) { // eslint-disable-line no-unused-vars
  const allowed = activeGroups(c);
  const withText = allowed.filter((g) => g.text.trim());
  return withText.length ? pick(withText) : allowed[0] || null;
}

// "ทุกกลุ่มในชุดนี้" / one group name / "N กลุ่ม"
// Pages that get tagged in this text (preview note, '' when none).
function tagNote(c, text) {
  const names = splitPageTags(text, c.config.pageTags).filter((p) => p.tag).map((p) => p.tag.name);
  return names.length ? `\n──────────\nแท็กเพจ (ตัวหนา กดไปที่เพจ): ${names.join(', ')}` : '';
}

function targetLabel(c, post) {
  if (!post.groupUrls.length) return { text: 'ทุกกลุ่มในชุดนี้', warn: false };
  const known = new Set(validGroups(c).map((g) => g.url));
  const live = post.groupUrls.filter((u) => known.has(u));
  const stale = post.groupUrls.length - live.length;
  let text = live.length === 1 ? groupLabel(c, live[0]) : `${live.length} กลุ่ม`;
  if (!live.length) text = 'กลุ่มที่ตั้งไว้ถูกลบแล้ว (โพสต์นี้จะไม่ถูกใช้)';
  else if (stale) text += ` (+ กลุ่มที่ถูกลบ ${stale})`;
  return { text, warn: !live.length };
}

function renderTargetList(c, post, listEl, labelEl) {
  listEl.textContent = '';
  const known = validGroups(c);
  const knownUrls = new Set(known.map((g) => g.url));
  const items = [
    ...known.map((g) => ({ url: g.url, label: g.name.trim() || shortGroup(g.url), stale: false, off: !g.enabled })),
    ...post.groupUrls.filter((u) => !knownUrls.has(u)).map((u) => ({ url: u, label: `${shortGroup(u)} (ถูกลบจากชุดแล้ว)`, stale: true })),
  ];
  if (!items.length) {
    listEl.textContent = 'ชุดนี้ยังไม่มีกลุ่ม';
    return;
  }
  for (const it of items) {
    const lab = document.createElement('label');
    lab.className = 'check' + (it.stale ? ' stale' : '');
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = post.groupUrls.includes(it.url);
    cb.addEventListener('change', () => {
      // Start from the post's current list (a link may have changed meanwhile).
      post.groupUrls = cb.checked
        ? [...new Set([...post.groupUrls, it.url])]
        : post.groupUrls.filter((u) => u !== it.url);
      setTargetLabel(c, post, labelEl);
      afterTargetingChange();
    });
    lab.append(cb, document.createTextNode(it.label + (it.off ? ' (ปิดอยู่)' : '')));
    listEl.append(lab);
  }
}

function setTargetLabel(c, post, labelEl) {
  const l = targetLabel(c, post);
  labelEl.textContent = l.text;
  labelEl.classList.toggle('warn', l.warn);
}

// Labels and open "ใช้กับ" lists follow group edits (name, link, delete).
function refreshPostTargets() {
  const c = cur();
  if (!c) return;
  for (const node of $$('#posts .post')) {
    const label = $('.pg-label', node);
    setTargetLabel(c, node.post, label);
    const details = $('.post-groups', node);
    if (details.open) renderTargetList(c, node.post, $('.pg-list', node), label);
  }
}

function afterTargetingChange() {
  renderGroupCounts();
  renderGroupsInfo();
  renderPostFilter();
  clearTimeout(testTimer);
  testTimer = setTimeout(refreshTest, 400);
  scheduleSave();
}

// Text/image edits change which posts are usable: refresh counts a bit later.
let countsTimer = null;
function scheduleCountsRefresh() {
  clearTimeout(countsTimer);
  countsTimer = setTimeout(() => {
    renderGroupCounts();
    renderGroupsInfo();
    renderPostFilter();
    refreshTest();
  }, 500);
}

// Filter for long post lists: all / posts usable by one group / not set.
function renderPostFilter() {
  const c = cur();
  const sel = $('#postFilter');
  const prev = sel.value;
  sel.textContent = '';
  if (!c) return;
  sel.append(new Option(`ทุกโพสต์ (${c.posts.length})`, ''));
  const untargeted = c.posts.filter((p) => !p.groupUrls.length).length;
  if (untargeted && untargeted !== c.posts.length) sel.append(new Option(`ใช้ได้ทุกกลุ่ม (${untargeted})`, '__all'));
  for (const g of validGroups(c)) {
    const n = postsForGroup(c, g.url).length;
    sel.append(new Option(`${g.name.trim() || shortGroup(g.url)} (${n})`, g.url));
  }
  if ([...sel.options].some((o) => o.value === prev)) sel.value = prev;
  applyPostFilter();
}

function postVisible(post, filter) {
  if (!filter) return true;
  if (filter === '__all') return !post.groupUrls.length;
  return !post.groupUrls.length || post.groupUrls.includes(filter);
}

function applyPostFilter() {
  const filter = $('#postFilter').value;
  let shown = 0;
  for (const node of $$('#posts .post')) {
    const vis = postVisible(node.post, filter);
    node.hidden = !vis;
    if (vis) shown++;
  }
  const total = $$('#posts .post').length;
  $('#postCount').textContent = filter ? `แสดง ${shown} จาก ${total} โพสต์` : '';
}

$('#postFilter').addEventListener('change', applyPostFilter);

let postsRender = 0;

async function renderPosts() {
  const c = cur();
  const token = ++postsRender;
  await prefetchThumbs(c);
  if (token !== postsRender || c !== cur()) return; // a newer render started
  const list = $('#posts');
  list.textContent = '';
  const tpl = $('#postTpl');
  c.posts.forEach((post, i) => {
    const node = tpl.content.firstElementChild.cloneNode(true);
    node.post = post;
    $('.post-title', node).textContent = `แบบที่ ${i + 1}`;

    const label = $('.pg-label', node);
    setTargetLabel(c, post, label);
    const details = $('.post-groups', node);
    details.addEventListener('toggle', () => {
      if (details.open) renderTargetList(c, post, $('.pg-list', node), label);
    });

    const ta = $('.post-text', node);
    ta.value = post.text || '';
    ta.addEventListener('input', () => {
      post.text = ta.value;
      scheduleSave();
      scheduleCountsRefresh();
    });

    const preview = $('.preview', node);
    $('.btn-preview', node).addEventListener('click', () => {
      const g = sampleGroup(c, post);
      preview.hidden = false;
      const text = composeText(post.text, g ? g.text : '', c.config.footer, c.config.footerPosition);
      const head = g ? `ตัวอย่างสำหรับ ${g.name || shortGroup(g.url)}` : 'ตัวอย่าง';
      preview.textContent = `${head}\n──────────\n${text || '(ไม่มีข้อความ)'}${tagNote(c, text)}`;
    });

    $('.btn-del', node).addEventListener('click', async () => {
      if (!confirm(`ลบโพสต์แบบที่ ${i + 1} ?`)) return;
      c.posts.splice(c.posts.indexOf(post), 1);
      if (!c.posts.length) c.posts.push(newPost());
      await save();
      await cleanupImages();
      renderPosts();
      afterTargetingChange();
    });

    const box = $('.images', node);
    renderImages(post, box);
    const input = $('input[type="file"]', node);
    input.addEventListener('change', async () => {
      const files = [...input.files];
      input.value = '';
      if (!files.length) return;
      setSaveState('กำลังเพิ่มรูป...');
      try {
        for (const f of files) {
          const rec = await processImage(f);
          const id = uid();
          await chrome.storage.local.set({ ['img:' + id]: rec });
          thumbCache.set(id, rec.data);
          post.imageIds.push(id);
        }
        await save();
      } catch (e) {
        toast('เพิ่มรูปไม่สำเร็จ: ' + (e?.message || e));
      }
      renderImages(post, box);
      afterTargetingChange();
    });

    list.append(node);
  });
  renderPostFilter();
}

$('#btnAddPost').addEventListener('click', async () => {
  const c = cur();
  const post = newPost();
  // While a group filter is active, the new post is for that group.
  const filter = $('#postFilter').value;
  if (filter && filter !== '__all') post.groupUrls = [filter];
  c.posts.push(post);
  await save();
  await renderPosts();
  afterTargetingChange();
  const nodes = $$('#posts .post');
  const last = nodes[nodes.length - 1];
  if (last) {
    last.scrollIntoView({ block: 'center' });
    $('.post-text', last).focus();
  }
});

// Find & replace in post texts (e.g. change a LINE id in every post).
$('#btnReplace').addEventListener('click', async () => {
  const find = $('#findText').value;
  const repl = $('#replaceText').value;
  if (!find) return toast('ใส่ข้อความที่จะค้นหาก่อน');
  const ids = $('#replaceScope').value === 'all' ? null : [currentId];
  const n = replaceInPosts(JSON.parse(JSON.stringify(settings)), [[find, repl]], ids);
  if (!n) return toast(`ไม่พบ "${find}"`);
  const where = ids ? 'ในชุดนี้' : 'ในทุกชุด';
  if (!confirm(`แทนที่ "${find}" ด้วย "${repl}" ${where} รวม ${n} จุด?`)) return;
  replaceInPosts(settings, [[find, repl]], ids);
  await save();
  renderCampaign();
  refreshTest();
  toast(`แทนที่แล้ว ${n} จุด`);
});

// Remove stored images no longer referenced by any post of any campaign.
async function cleanupImages() {
  const used = new Set(settings.campaigns.flatMap(campaignImageIds));
  const keys = chrome.storage.local.getKeys
    ? await chrome.storage.local.getKeys()
    : Object.keys(await chrome.storage.local.get(null));
  const orphan = keys.filter((k) => k.startsWith('img:') && !used.has(k.slice(4)));
  if (orphan.length) await chrome.storage.local.remove(orphan);
}

// ---------- global settings ----------

for (const el of $$('[data-global]')) {
  const key = el.dataset.global;
  const onChange = () => {
    settings.global[key] = readField(el, DEFAULT_GLOBAL);
    scheduleSave();
  };
  el.addEventListener('input', onChange);
  el.addEventListener('change', onChange);
}

function renderGlobal() {
  for (const el of $$('[data-global]')) writeField(el, settings.global[el.dataset.global]);
  for (const el of $$('[data-tg]')) writeField(el, settings.global.telegram[el.dataset.tg]);
  renderCapturePerm();
}

// ---------- telegram ----------

const CAPTURE_PERM = { origins: ['<all_urls>'] };

async function renderCapturePerm() {
  const t = settings.global.telegram;
  let missing = false;
  if (t.enabled && t.screenshot) {
    try {
      missing = !(await chrome.permissions.contains(CAPTURE_PERM));
    } catch {
      missing = false;
    }
  }
  $('#capturePerm').hidden = !missing;
}

// Must run inside a click/change handler (Chrome asks the user).
async function requestCapturePerm() {
  try {
    const granted = await chrome.permissions.request(CAPTURE_PERM);
    if (!granted) toast('ไม่ได้อนุญาตจับภาพหน้าจอ จะส่งเป็นข้อความอย่างเดียว');
  } catch (e) {
    toast('ขออนุญาตไม่สำเร็จ: ' + (e?.message || e));
  }
  renderCapturePerm();
}

for (const el of $$('[data-tg]')) {
  const key = el.dataset.tg;
  const onChange = (ev) => {
    const t = settings.global.telegram;
    t[key] = el.type === 'checkbox' ? el.checked : el.value.trim();
    scheduleSave();
    if (ev.type === 'change' && (key === 'screenshot' || key === 'enabled') && t.enabled && t.screenshot) {
      requestCapturePerm();
    } else {
      renderCapturePerm();
    }
  };
  el.addEventListener(el.type === 'checkbox' ? 'change' : 'input', onChange);
}

$('#btnCapturePerm').addEventListener('click', requestCapturePerm);

$('#btnTgTest').addEventListener('click', async () => {
  await save();
  const r = await bg('tgTest');
  toast(r?.ok ? 'ส่งข้อความทดสอบแล้ว ดูใน Telegram' : `ส่งไม่สำเร็จ: ${r?.error || ''}`);
});

$('#btnTgFind').addEventListener('click', async () => {
  await save();
  const r = await bg('tgFindChats');
  if (!r?.ok) return toast(`ดึง Chat ID ไม่สำเร็จ: ${r?.error || ''}`);
  const chats = r.chats || [];
  if (!chats.length) return toast('ยังไม่พบแชท ส่งข้อความหา bot ก่อน แล้วกดอีกครั้ง', 5000);
  let chosen = chats[0];
  if (chats.length > 1) {
    const list = chats.map((c, i) => `${i + 1}. ${c.name || '-'} (${c.type}) ${c.id}`).join('\n');
    const n = parseInt(prompt(`พบ ${chats.length} แชท เลือกหมายเลข:\n${list}`, '1'), 10);
    if (!(n >= 1 && n <= chats.length)) return;
    chosen = chats[n - 1];
  }
  settings.global.telegram.chatId = chosen.id;
  writeField($('[data-tg="chatId"]'), chosen.id);
  await save();
  toast(`ตั้ง Chat ID: ${chosen.name || ''} ${chosen.id}`);
});

// ---------- test ----------

function refreshTest() {
  const c = cur();
  const gSel = $('#testGroup');
  const pSel = $('#testPost');
  const prevG = gSel.value;
  const prevP = pSel.value;
  gSel.textContent = '';
  pSel.textContent = '';
  $('#testPreview').hidden = true;
  if (!c) {
    gSel.append(new Option('(ยังไม่มีชุด)', ''));
    return;
  }
  for (const g of activeGroups(c)) {
    const label = g.name.trim() || shortGroup(g.url);
    gSel.append(new Option(g.text.trim() ? `${label} (${g.text.trim().slice(0, 20)})` : label, g.url));
  }
  if (!gSel.options.length) gSel.append(new Option('(ยังไม่มีกลุ่ม)', ''));
  if ([...gSel.options].some((o) => o.value === prevG)) gSel.value = prevG;
  refreshTestPosts(prevP);
}

// Posts offered for the test: the ones the selected group may use.
function refreshTestPosts(keep = $('#testPost').value) {
  const c = cur();
  const pSel = $('#testPost');
  pSel.textContent = '';
  $('#testPreview').hidden = true;
  if (!c) return;
  const url = $('#testGroup').value;
  const allowed = url ? postsForGroup(c, url) : usablePosts(c.posts);
  // No post is set for this group: offer every post so one can be picked by hand.
  const offered = allowed.length ? allowed : usablePosts(c.posts);
  pSel.append(new Option(`สุ่มเนื้อหา (${allowed.length} แบบ)`, ''));
  c.posts.forEach((p, i) => {
    if (!offered.includes(p)) return;
    const head = (p.text || '').replace(/\s+/g, ' ').trim().slice(0, 30);
    const note = allowed.includes(p) ? '' : ' (ไม่ได้ตั้งให้ใช้กับกลุ่มนี้)';
    pSel.append(new Option(`แบบที่ ${i + 1}: ${head || '(รูปอย่างเดียว)'}${p.imageIds.length ? ` +${p.imageIds.length} รูป` : ''}${note}`, p.id));
  });
  if ([...pSel.options].some((o) => o.value === keep)) pSel.value = keep;
}

$('#testGroup').addEventListener('change', () => refreshTestPosts());

$('#btnPreviewTest').addEventListener('click', () => {
  const c = cur();
  if (!c) return;
  const g = activeGroups(c).find((x) => x.url === $('#testGroup').value);
  const posts = g ? postsForGroup(c, g.url) : usablePosts(c.posts);
  const post = usablePosts(c.posts).find((p) => p.id === $('#testPost').value) || pick(posts);
  const box = $('#testPreview');
  box.hidden = false;
  if (!post) {
    box.textContent = g ? '(ยังไม่มีโพสต์ที่ใช้กับกลุ่มนี้)' : '(ชุดนี้ยังไม่มีเนื้อหา)';
    return;
  }
  const media = postMediaIds(c, post).length;
  const lead = c.leadImageIds.length;
  const imgs = media ? `\n──────────\n+ แนบไฟล์ ${media} ไฟล์${lead ? ` (สื่อนำหน้า ${lead} + ของโพสต์ ${media - lead})` : ''}` : '';
  const text = composeText(post.text, g ? g.text : '', c.config.footer, c.config.footerPosition);
  box.textContent = (text || '(ไม่มีข้อความ)') + tagNote(c, text) + imgs;
});

$('#btnTest').addEventListener('click', async () => {
  const url = $('#testGroup').value;
  if (!url) return toast('ชุดนี้ยังไม่มีลิงก์กลุ่ม');
  if (!confirm(`จะโพสต์จริงลงกลุ่มนี้ 1 ครั้ง:\n${url}\n\nยืนยัน?`)) return;
  await save();
  const r = await bg('testPost', { campaignId: currentId, url, postId: $('#testPost').value || null });
  toast(r?.ok ? 'เริ่มทดสอบ ดูหน้าต่างโพสต์ที่เปิดขึ้นมา' : r?.error || 'ทดสอบไม่สำเร็จ');
});

// ---------- backup (download / upload settings) ----------

async function storageKeys() {
  return chrome.storage.local.getKeys
    ? chrome.storage.local.getKeys()
    : Object.keys(await chrome.storage.local.get(null));
}

function fileStamp() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}`;
}

const safeFileName = (s) => String(s || 'campaign').replace(/[\\/:*?"<>|\s]+/g, '_').slice(0, 40);

function downloadBlob(blob, name) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
}

$('#btnExport').addEventListener('click', async () => {
  await save();
  const onlyCurrent = $('#exportScope').value === 'current';
  if (onlyCurrent && !cur()) return toast('ยังไม่ได้เลือกชุด');
  const campaignIds = onlyCurrent ? [currentId] : null;
  const includeImages = $('#exportImages').checked;
  const images = {};
  if (includeImages) {
    const camps = campaignIds ? settings.campaigns.filter((c) => campaignIds.includes(c.id)) : settings.campaigns;
    const keys = imageIdsOf(camps).map((id) => 'img:' + id);
    const data = keys.length ? await chrome.storage.local.get(keys) : {};
    for (const k of keys) if (data[k]) images[k.slice(4)] = data[k];
  }
  const backup = buildBackup(settings, images, {
    includeImages,
    includeToken: $('#exportToken').checked,
    campaignIds,
    version: chrome.runtime.getManifest().version,
  });
  const name = `fb-autopost-${onlyCurrent ? safeFileName(cur().name) + '-' : ''}${fileStamp()}.json`;
  downloadBlob(new Blob([JSON.stringify(backup, null, 2)], { type: 'application/json' }), name);
  const sum = summarize(backup.settings, backup.images || {});
  toast(`ดาวน์โหลด ${name} (${sum.campaigns} ชุด · ${sum.groups} กลุ่ม · ${sum.images} รูป)`, 5000);
});

$('#importFile').addEventListener('change', async (ev) => {
  const input = ev.target;
  const file = input.files[0];
  input.value = '';
  if (!file) return;
  if (state.running || state.current) return toast('กด "หยุด" ก่อนนำเข้าการตั้งค่า', 4000);
  let parsed;
  try {
    parsed = parseBackup(await file.text());
  } catch (e) {
    return toast(`นำเข้าไม่ได้: ${e?.message || e}`, 5000);
  }
  const mode = $('#importMode').value;
  const sum = summarize(parsed.settings, parsed.images);
  const when = parsed.meta.exportedAt ? fmtDateTime(Date.parse(parsed.meta.exportedAt)) : '-';
  const ask = `ไฟล์: ${file.name}\nบันทึกเมื่อ: ${when}\n${sum.campaigns} ชุด · ${sum.groups} กลุ่ม · ${sum.posts} แบบโพสต์ · ${sum.images} รูป\n\n${importAction(mode)}\nยืนยัน?`;
  if (!confirm(ask)) return;
  await importParsed(parsed, mode);
});

function importAction(mode) {
  return mode === 'replace'
    ? 'แทนที่การตั้งค่าทั้งหมดในเครื่องนี้ (ชุดเดิมจะถูกลบ)'
    : 'เพิ่มเป็นชุดใหม่ ชุดเดิมยังอยู่ครบ';
}

// Writes an uploaded backup / converted export into storage and shows it.
async function importParsed(parsed, mode, extraNotes = []) {
  const sum = summarize(parsed.settings, parsed.images);
  const existing = new Set((await storageKeys()).filter((k) => k.startsWith('img:')).map((k) => k.slice(4)));
  const res = applyImport(settings, parsed, mode, existing);
  for (const [from, to] of res.copyImages) {
    const rec = (await chrome.storage.local.get('img:' + from))['img:' + from];
    if (rec) res.writeImages[to] = rec;
  }
  const writes = {};
  for (const [id, rec] of Object.entries(res.writeImages)) writes['img:' + id] = rec;
  if (Object.keys(writes).length) await chrome.storage.local.set(writes);

  settings = res.settings;
  if (!settings.campaigns.length) settings.campaigns.push(newCampaign(1));
  await save();
  thumbCache.clear();
  await cleanupImages();
  renderGlobal();
  const firstNew =
    mode === 'merge' && parsed.settings.campaigns.length
      ? settings.campaigns[settings.campaigns.length - parsed.settings.campaigns.length]
      : settings.campaigns[0];
  selectCampaign(firstNew.id);
  const notes = [
    res.missingImages ? `ไม่มีรูปในไฟล์ ${res.missingImages} รูป (ตัดออกจากโพสต์)` : '',
    parsed.badImages ? `รูปเสีย ${parsed.badImages} รูป` : '',
    ...extraNotes,
  ].filter(Boolean);
  toast(`นำเข้าแล้ว ${sum.campaigns} ชุด${notes.length ? ' · ' + notes.join(' · ') : ''}`, 7000);
}

// SIRI autopost export (.zip): posts, images, groups and which group each
// post was made in. Global settings (Telegram etc.) stay as they are.
$('#importSiri').addEventListener('change', async (ev) => {
  const input = ev.target;
  const file = input.files[0];
  input.value = '';
  if (!file) return;
  if (state.running || state.current) return toast('กด "หยุด" ก่อนนำเข้า', 4000);
  toast('กำลังอ่านไฟล์ zip...', 20000);
  let converted;
  try {
    const files = await readZip(await file.arrayBuffer());
    converted = await convertSiriExport(files, { name: nameFromFile(file.name) });
  } catch (e) {
    return toast(`นำเข้าไม่ได้: ${e?.message || e}`, 6000);
  }
  const { report } = converted;
  const mode = $('#importMode').value;
  const camps = converted.settings.campaigns.map((c) => `• ${c.name}: ${c.groups.length} กลุ่ม · ${c.posts.length} โพสต์${c.enabled ? '' : ' (ปิดไว้)'}`);
  const ask =
    `ไฟล์ SIRI: ${file.name}\n${camps.join('\n')}\nรูป ${report.images} รูป` +
    `${report.duplicateImages ? ` (รวมรูปซ้ำ ${report.duplicateImages})` : ''}` +
    `${report.missingImages ? `\nหารูปไม่เจอ ${report.missingImages} รูป` : ''}` +
    `\n\n${importAction(mode)}\nยืนยัน?`;
  if (!confirm(ask)) return toast('ยกเลิกแล้ว');
  converted.settings.global = JSON.parse(JSON.stringify(settings.global));
  const parsed = { settings: converted.settings, images: converted.images, meta: {}, badImages: 0 };
  await importParsed(parsed, mode, [
    report.missingImages ? `หารูปไม่เจอ ${report.missingImages} รูป` : '',
    report.emptyPosts ? `ข้ามโพสต์ว่าง ${report.emptyPosts}` : '',
  ]);
});

// ---------- config file in the extension folder ----------

async function readBundled() {
  try {
    const res = await fetch(chrome.runtime.getURL(BUNDLED_CONFIG_PATH), { cache: 'no-store' });
    if (!res.ok) return { missing: true };
    const text = await res.text();
    try {
      return { parsed: parseBackup(text) };
    } catch (e) {
      return { error: e.message };
    }
  } catch {
    return { missing: true };
  }
}

async function renderConfigStatus() {
  const box = $('#cfgStatus');
  const r = await readBundled();
  const { configLoaded } = await chrome.storage.local.get('configLoaded');
  const loaded = configLoaded ? ` · โหลดเข้าเครื่องนี้ล่าสุด ${fmtDateTime(configLoaded.at)}` : '';
  if (r.missing) {
    box.className = 'cfg-status none';
    box.textContent = 'ยังไม่มีไฟล์ config ในโฟลเดอร์ส่วนขยาย กด "บันทึก config ลงโฟลเดอร์ส่วนขยาย"';
  } else if (r.error) {
    box.className = 'cfg-status bad';
    box.textContent = `ไฟล์ config ในโฟลเดอร์ใช้ไม่ได้: ${r.error}`;
  } else {
    const s = summarize(r.parsed.settings, r.parsed.images);
    const when = r.parsed.meta.exportedAt ? fmtDateTime(Date.parse(r.parsed.meta.exportedAt)) : '-';
    box.className = 'cfg-status found';
    box.textContent =
      `พบไฟล์ config บันทึกเมื่อ ${when}: ${s.campaigns} ชุด · ${s.groups} กลุ่ม · ${s.posts} แบบโพสต์ · ${s.images} รูป` +
      `${r.parsed.settings.global.telegram.botToken ? ' · มี Bot Token' : ''}${r.parsed.autoStart ? ' · เริ่มทำงานอัตโนมัติ' : ''}${loaded}`;
  }
  return r;
}

// Writes the file with the save dialog (user picks the extension folder);
// falls back to a normal download when the dialog is not available.
async function saveConfigFile(text) {
  if (window.showSaveFilePicker) {
    try {
      const handle = await window.showSaveFilePicker({
        id: 'fbap-config',
        suggestedName: 'autopost-config.json',
        types: [{ description: 'FB AutoPost config', accept: { 'application/json': ['.json'] } }],
      });
      const w = await handle.createWritable();
      await w.write(text);
      await w.close();
      return { saved: true, name: handle.name };
    } catch (e) {
      if (e && e.name === 'AbortError') return { cancelled: true };
    }
  }
  downloadBlob(new Blob([text], { type: 'application/json' }), 'autopost-config.json');
  return { downloaded: true };
}

$('#btnSaveConfig').addEventListener('click', async () => {
  await save();
  const images = {};
  const keys = imageIdsOf(settings.campaigns).map((id) => 'img:' + id);
  const data = keys.length ? await chrome.storage.local.get(keys) : {};
  for (const k of keys) if (data[k]) images[k.slice(4)] = data[k];
  const backup = buildBackup(settings, images, {
    includeImages: true,
    includeToken: $('#cfgToken').checked,
    autoStart: $('#cfgAutoStart').checked,
    version: chrome.runtime.getManifest().version,
  });
  const r = await saveConfigFile(JSON.stringify(backup, null, 2));
  if (r.cancelled) return;
  if (r.downloaded) {
    renderConfigStatus();
    return toast(`ดาวน์โหลด autopost-config.json แล้ว ย้ายไฟล์ไปไว้ในโฟลเดอร์ส่วนขยาย → ${BUNDLED_CONFIG_PATH}`, 7000);
  }
  const check = await renderConfigStatus();
  if (check.parsed && check.parsed.meta.exportedAt === backup.exportedAt) {
    toast('บันทึก config ลงโฟลเดอร์ส่วนขยายแล้ว คัดลอกทั้งโฟลเดอร์ไปใช้เครื่องอื่นได้เลย', 6000);
  } else {
    toast(`บันทึก "${r.name}" แล้ว แต่ไม่ใช่ไฟล์ ${BUNDLED_CONFIG_PATH} ของโฟลเดอร์ส่วนขยาย ลองบันทึกใหม่ให้ถูกที่`, 8000);
  }
});

$('#btnLoadConfig').addEventListener('click', async () => {
  if (state.running || state.current) return toast('กด "หยุด" ก่อนโหลด config', 4000);
  const r = await renderConfigStatus();
  if (r.missing) return toast(`ไม่พบไฟล์ ${BUNDLED_CONFIG_PATH} ในโฟลเดอร์ส่วนขยาย`, 5000);
  if (r.error) return toast(`ไฟล์ config ใช้ไม่ได้: ${r.error}`, 5000);
  const s = summarize(r.parsed.settings, r.parsed.images);
  if (!confirm(`โหลด config จากโฟลเดอร์ส่วนขยาย\n${s.campaigns} ชุด · ${s.groups} กลุ่ม · ${s.posts} แบบโพสต์ · ${s.images} รูป\n\nแทนที่การตั้งค่าทั้งหมดในเครื่องนี้ ยืนยัน?`)) return;
  await save();
  const res = await bg('loadBundledConfig');
  if (!res?.ok) return toast(`โหลดไม่สำเร็จ: ${res?.error || ''}`, 5000);
  location.reload();
});

// ---------- online: config from the server ----------

const webMode = () => document.body.classList.contains('web-mode');

async function renderOnline() {
  if (webMode()) return; // the web page is the server itself
  const { online = {} } = await chrome.storage.local.get('online');
  const on = !!online.enabled;
  const err = online.lastError || '';
  const badge = $('#onlineBadge');
  badge.className = 'badge' + (on && !err ? ' on' : '');
  badge.textContent = on ? (err ? 'มีปัญหา' : 'เชื่อมต่อแล้ว') : 'ไม่ได้เชื่อมต่อ';
  $('#onlineForm').hidden = on;
  $('#onlineActions').hidden = !on;
  const box = $('#onlineStatus');
  box.hidden = !on;
  if (on) {
    box.className = 'cfg-status ' + (err ? 'bad' : 'found');
    box.textContent =
      `เครื่อง "${online.deviceName || '-'}" · config: ${online.profileName || '(server ยังไม่ได้เลือก)'}` +
      `${online.revision ? ` ฉบับที่ ${online.revision}` : ''}` +
      ` · ซิงก์ล่าสุด ${online.lastSyncAt ? fmtDateTime(online.lastSyncAt) : '-'}${err ? ` · ⚠ ${err}` : ''}`;
    $('#onlineOpen').href = online.serverUrl + '/';
  }
  if (!$('#onlineUrl').value) $('#onlineUrl').value = online.serverUrl || '';
}

async function onlineAction(btn, run, okText) {
  btn.disabled = true;
  try {
    const r = await run();
    toast(r?.ok ? okText : `ไม่สำเร็จ: ${r?.error || 'ไม่ทราบสาเหตุ'}`, 5000);
  } finally {
    btn.disabled = false;
    renderOnline();
  }
}

$('#btnOnlineConnect').addEventListener('click', async () => {
  const serverUrl = $('#onlineUrl').value.trim();
  const deviceKey = $('#onlineKey').value.trim();
  if (!serverUrl || !deviceKey) return toast('ใส่ URL server และคีย์เครื่องก่อน');
  const ask =
    'เชื่อมต่อ server?\n\nถ้า config บน server มีข้อมูลแล้ว การตั้งค่าในเครื่องนี้จะถูกแทนที่ด้วยของ server\n' +
    '(ถ้ายังว่าง จะอัปโหลดของเครื่องนี้ขึ้นไป)';
  if (!confirm(ask)) return;
  await save();
  await onlineAction($('#btnOnlineConnect'), () => bg('onlineConnect', { serverUrl, deviceKey }), 'เชื่อมต่อ server แล้ว');
  $('#onlineKey').value = '';
});

$('#btnOnlineSync').addEventListener('click', async () => {
  await save();
  await onlineAction($('#btnOnlineSync'), () => bg('onlineSync', { mode: 'auto' }), 'ซิงก์แล้ว');
});

$('#btnOnlinePull').addEventListener('click', async () => {
  if (!confirm('ดึง config จาก server มาแทนที่การตั้งค่าทั้งหมดในเครื่องนี้?')) return;
  await onlineAction($('#btnOnlinePull'), () => bg('onlineSync', { mode: 'pull' }), 'ดึง config จาก server แล้ว');
});

$('#btnOnlinePush').addEventListener('click', async () => {
  if (!confirm('เขียนทับ config บน server ด้วยการตั้งค่าของเครื่องนี้?\n(ทุกเครื่องที่ใช้ config นี้จะได้ของเครื่องนี้ไป)')) return;
  await save();
  await onlineAction($('#btnOnlinePush'), () => bg('onlineSync', { mode: 'push' }), 'อัปโหลดขึ้น server แล้ว');
});

$('#btnOnlineDisconnect').addEventListener('click', async () => {
  if (!confirm('ยกเลิกการเชื่อมต่อ server?\nการตั้งค่าในเครื่องนี้ยังอยู่ แต่จะไม่ดึง config / รับคำสั่งจากเว็บอีก')) return;
  await onlineAction($('#btnOnlineDisconnect'), () => bg('onlineDisconnect'), 'ยกเลิกการเชื่อมต่อแล้ว');
});

// ---------- cloud: posts from the SIRI AutoPost web app ----------

async function renderCloud() {
  if (webMode()) return; // pairing happens in the browser that will post
  const { cloud = {} } = await chrome.storage.local.get('cloud');
  const on = !!cloud.enabled;
  const err = cloud.lastError || '';
  const resting = cloud.pausedUntil > Date.now();
  const badge = $('#cloudBadge');
  badge.className = 'badge' + (on && !err && !cloud.paused ? ' on' : '');
  badge.textContent = !on ? 'ไม่ได้จับคู่' : err ? 'มีปัญหา' : cloud.paused || resting ? 'พักรับงาน' : 'รับงานอยู่';
  $('#cloudForm').hidden = on;
  $('#cloudActions').hidden = !on;
  $('#btnCloudPause').textContent = cloud.paused ? 'รับงานต่อ' : 'พักรับงาน';
  const box = $('#cloudStatus');
  box.hidden = !on;
  if (on) {
    const job = cloud.lastJob;
    box.className = 'cfg-status ' + (err ? 'bad' : 'found');
    box.textContent =
      `เวิร์กสเปซ "${cloud.workspaceName || '-'}" · เครื่อง "${cloud.deviceName || '-'}" · ${cloud.groups || 0} กลุ่ม` +
      ` · ซิงก์ล่าสุด ${cloud.lastSyncAt ? fmtDateTime(cloud.lastSyncAt) : '-'}` +
      (job ? ` · งานล่าสุด ${fmtDateTime(job.at)} ${job.ok ? 'สำเร็จ' : 'ไม่สำเร็จ'} (${job.group})` : '') +
      (resting ? ` · พักหลัง Facebook แจ้งเตือนถึง ${fmtDateTime(cloud.pausedUntil)}` : '') +
      (err ? ` · ⚠ ${err}` : '');
  }
  if (!$('#cloudUrl').value) $('#cloudUrl').value = cloud.apiUrl || '';
}

$('#btnCloudPair').addEventListener('click', async () => {
  const apiUrl = $('#cloudUrl').value.trim();
  const code = $('#cloudCode').value.trim();
  if (!apiUrl || !code) return toast('ใส่ URL และรหัสจับคู่ก่อน');
  await save();
  await onlineAction($('#btnCloudPair'), () => bg('cloudPair', { apiUrl, code, name: $('#cloudName').value }), 'จับคู่กับเว็บ AutoPost แล้ว');
  $('#cloudCode').value = '';
  renderCloud();
});

$('#btnCloudSync').addEventListener('click', async () => {
  await save();
  await onlineAction($('#btnCloudSync'), () => bg('cloudSync'), 'ซิงก์กับเว็บ AutoPost แล้ว');
  renderCloud();
});

$('#btnCloudPause').addEventListener('click', async () => {
  const { cloud = {} } = await chrome.storage.local.get('cloud');
  await onlineAction($('#btnCloudPause'), () => bg('cloudPause', { paused: !cloud.paused }), cloud.paused ? 'รับงานต่อแล้ว' : 'พักรับงานแล้ว');
  renderCloud();
});

$('#btnCloudUnpair').addEventListener('click', async () => {
  if (!confirm('ยกเลิกการจับคู่กับเว็บ AutoPost?\nเครื่องนี้จะไม่รับงานโพสต์จากเว็บอีก (ยกเลิกการผูกในหน้าเว็บด้วย เพื่อคืนโควตาอุปกรณ์)')) return;
  await onlineAction($('#btnCloudUnpair'), () => bg('cloudUnpair'), 'ยกเลิกการจับคู่แล้ว');
  renderCloud();
});

// ---------- overview / status ----------

function renderOverview() {
  const box = $('#overview');
  box.textContent = '';
  if (!settings.campaigns.length) {
    box.textContent = 'ยังไม่มีชุดโพสต์';
  }
  for (const c of settings.campaigns) {
    const v = campaignView(c);
    const row = document.createElement('div');
    row.className = 'ov-row' + (c.id === currentId ? ' selected' : '');
    const name = document.createElement('div');
    name.className = 'ov-name';
    name.textContent = c.name || 'ไม่มีชื่อ';
    const st = document.createElement('div');
    st.className = `ov-state ${v.cls}`;
    st.textContent = v.label;
    const meta = document.createElement('div');
    meta.className = 'ov-meta';
    meta.textContent = campaignMeta(c, v);
    row.append(name, st, meta);
    row.addEventListener('click', () => selectCampaign(c.id));
    box.append(row);
  }

  const badge = $('#statusBadge');
  const busy = !!state.current;
  badge.className = 'badge' + (busy ? ' busy' : state.running ? ' on' : '');
  badge.textContent = busy ? 'กำลังโพสต์' : state.running ? 'ทำงานอยู่' : 'หยุด';
  $('#btnStart').disabled = !!state.running;
  $('#btnStop').disabled = !state.running && !state.testing;
  $('#btnTest').disabled = busy;

  const line = $('#currentLine');
  if (state.current) {
    const c = settings.campaigns.find((x) => x.id === state.current.campaignId);
    const from = state.current.cloud ? 'เว็บ AutoPost → ' : c ? c.name + ' → ' : '';
    line.textContent = `${state.testing ? '[ทดสอบ] ' : ''}กำลังโพสต์: ${from}${state.current.url}`;
  } else if (state.running && state.pausedUntil > Date.now()) {
    line.textContent = `⏸ พักอัตโนมัติถึง ${fmtDateTime(state.pausedUntil)}: ${state.pauseReason || ''}`;
  } else {
    line.textContent = '';
  }
  renderGuard();
  renderCampStatus();
}

function renderLogs(logs) {
  const ol = $('#logs');
  ol.textContent = '';
  for (const l of logs.slice(-200)) {
    const li = document.createElement('li');
    li.className = l.level;
    const t = document.createElement('time');
    t.textContent = fmtDateTime(l.t);
    li.append(t, document.createTextNode(l.msg));
    ol.append(li);
  }
}

// ---------- global actions ----------

$('#btnStart').addEventListener('click', async () => {
  await save();
  const r = await bg('start');
  toast(r?.ok ? 'เริ่มทำงานแล้ว' : r?.error || 'เริ่มไม่สำเร็จ');
});

$('#btnStop').addEventListener('click', async () => {
  await bg('stop');
  toast('หยุดแล้ว');
});

$('#btnClearLog').addEventListener('click', () => chrome.storage.local.set({ logs: [] }));

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local') return;
  if (changes.state) {
    state = changes.state.newValue || {};
    renderOverview();
    renderTabs();
    if (cur()) renderGroupLimits();
  }
  if (changes.logs) renderLogs(changes.logs.newValue || []);
  if (changes.online) renderOnline();
  if (changes.cloud) renderCloud();
  // Settings were replaced by a config file or the server: show the new data.
  if (changes.configLoaded || changes.onlinePulled) location.reload();
});

setInterval(renderOverview, 1000);

// ---------- init ----------

(async function init() {
  const data = await chrome.storage.local.get(['settings', 'state', 'logs']);
  settings = migrateSettings(data.settings);
  if (!settings.campaigns.length) settings.campaigns.push(newCampaign(1));
  // Store the migrated (version 2) shape right away.
  if (!data.settings || !Array.isArray(data.settings.campaigns)) await save();
  state = data.state || {};
  const remembered = recallCurrent();
  currentId = settings.campaigns.some((c) => c.id === remembered) ? remembered : settings.campaigns[0].id;
  renderGlobal();
  selectCampaign(currentId);
  renderLogs(data.logs || []);
  if (!webMode()) renderConfigStatus();
  renderOnline();
  renderCloud();
  cleanupImages().catch(() => {});
})();
