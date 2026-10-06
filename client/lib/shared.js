// Shared helpers used by the background service worker and the dashboard page.

// Settings shape (version 2):
// {
//   version: 2,
//   global: { focusWindow, minGapMin, telegram: { ...DEFAULT_TELEGRAM } },
//   campaigns: [{
//     id, name, enabled,
//     groups: [{ url, text, enabled }],   // text = per-group first line / {{code}}
//     posts:  [{ id, text, imageIds, imageUrls }],   // imageUrls = http(s) links fetched when the post goes out
//     config: { ...DEFAULT_CONFIG },
//   }]
// }

// Per-campaign schedule and behaviour.
export const DEFAULT_CONFIG = {
  postMode: 'random',        // 'random' | 'sequence'
  footer: '',                // text added to every post (links, contact)
  footerPosition: 'afterGreeting', // 'afterGreeting' (after "ขออนุญาต…/สวัสดี…") | 'top' (before the content) | 'end'
  // Facebook pages tagged (@mention: bold name linking to the page) where a
  // post names them. One per line: "page name | page link".
  pageTags: 'SIRI Studio Photo รับตัดต่อ รีทัช รูปติดบัตร รูปจบ สมัครงาน สมัครสอบ อัดรูป | https://www.facebook.com/KHRUSIRI',

  // Delay between groups (minutes, random inside range)
  groupDelayMin: 3,
  groupDelayMax: 8,

  // Gap between rounds (hours) + random extra minutes
  roundIntervalHours: 6,
  roundJitterMin: 30,
  maxRounds: 0,              // 0 = unlimited

  // Human-like randomness
  shuffleGroups: true,       // random group order every round
  skipChancePct: 0,          // random chance to skip a group in a round
  typingSpeed: 'normal',     // 'slow' | 'normal' | 'fast'
  typos: true,               // occasionally mistype then backspace
  maxTypeChars: 600,         // longer texts are pasted (like copy & paste)
  browseBeforePost: true,    // scroll the group feed before posting
  longBreakEvery: 5,         // random long break, on average every N posts (0 = off)
  longBreakMin: 10,          // minutes
  longBreakMax: 25,

  // Content rotation / anti-block
  recentAvoid: 10,           // never reuse a post among this group's last N posts (when possible)
  groupCooldownHours: 0,     // minimum hours between two posts in the same group (0 = off)
  shuffleImages: true,       // random order of each post's own images (lead media stays first)
  leadChancePct: 100,        // chance (%) to attach the campaign lead media to a post

  // Only post during these hours
  activeHoursEnabled: false,
  activeStart: '08:00',
  activeEnd: '22:00',
};

export const DEFAULT_TELEGRAM = {
  enabled: false,
  botToken: '',
  chatId: '',
  onSuccess: true,           // each successful post
  onFail: true,              // each failed post (then skipped)
  screenshot: true,          // attach a screenshot of the posting window
  roundSummary: true,        // list of groups posted / failed when a round ends
  onStartStop: true,         // start / stop / campaign finished
};

export const DEFAULT_GLOBAL = {
  focusWindow: true,         // bring posting window to front while posting
  minGapMin: 2,              // minimum minutes between any two posts (all campaigns)
  dailyMaxPosts: 0,          // max successful posts per day, all campaigns (0 = off)
  blockPauseHoursMin: 24,    // Facebook warning/block detected: pause everything
  blockPauseHoursMax: 48,    //   for a random time in this range, then resume
  failStreakPause: 4,        // this many failed posts in a row: pause 2-4 hours (0 = off)
  telegram: { ...DEFAULT_TELEGRAM },
};

function normalizeGlobal(g) {
  g = g && typeof g === 'object' ? g : {};
  const { telegram, ...rest } = DEFAULT_GLOBAL;
  const out = { ...sanitizeLike(rest, g), telegram: sanitizeLike(telegram, g.telegram) };
  // A Facebook warning must always pause for real (at least 1 hour).
  out.blockPauseHoursMin = Math.max(1, out.blockPauseHoursMin);
  out.blockPauseHoursMax = Math.max(out.blockPauseHoursMin, out.blockPauseHoursMax);
  return out;
}

// Placeholder in post text that receives the per-group text.
export const CODE_RE = /\{\{\s*(?:code|รหัส)\s*\}\}/gi;

export const TYPING_SPEEDS = {
  slow: { min: 140, max: 380 },
  normal: { min: 70, max: 220 },
  fast: { min: 35, max: 110 },
};

export function rand(min, max) {
  return min + Math.random() * (max - min);
}

export function randInt(min, max) {
  return Math.floor(rand(min, max + 1));
}

// Triangular distribution: values cluster near the middle of the range,
// which looks less mechanical than a flat uniform pick.
export function humanRand(min, max) {
  if (max <= min) return min;
  return min + ((Math.random() + Math.random()) / 2) * (max - min);
}

export function chance(pct) {
  return Math.random() * 100 < pct;
}

export function shuffle(arr) {
  const a = arr.slice();
  for (let i = a.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [a[i], a[j]] = [a[j], a[i]];
  }
  return a;
}

export function pick(arr) {
  return arr[Math.floor(Math.random() * arr.length)];
}

// Spintax: "{Hello|Hi|Hey} friends" -> one random option. Nested groups work.
// Only braces that contain "|" are treated as spintax.
export function spin(text) {
  const re = /\{([^{}]*\|[^{}]*)\}/;
  let out = String(text || '');
  for (let guard = 0; guard < 2000 && re.test(out); guard++) {
    out = out.replace(re, (_, body) => pick(body.split('|')));
  }
  return out;
}

// First path segments that are Facebook's own screens, not a page (mirrors FacebookGroupUrl.Reserved in the API).
const RESERVED_PAGE_SEGMENTS = new Set([
  'groups', 'pages', 'watch', 'marketplace', 'events', 'share', 'sharer', 'reel', 'reels', 'stories', 'story.php', 'photo',
  'photos', 'photo.php', 'video', 'videos', 'login', 'login.php', 'home.php', 'settings', 'help', 'policies', 'privacy', 'ads',
  'business', 'gaming', 'people', 'permalink.php', 'hashtag', 'search', 'friends', 'messages', 'notifications', 'bookmarks',
  'fundraisers', 'jobs', 'offers', 'public', 'dialog', 'plugins', 'profile.php', 'checkpoint', 'recover', 'r.php', 'l.php',
  'composer', 'feeds', 'saved', 'memories', 'campaign', 'careers', 'directory', 'legal', 'about', 'support', 'tr', 'flx',
]);

// What a Facebook address points to: { kind: 'group' | 'page', url } with a canonical address, or null. A group is
// /groups/<id-or-slug>; a page is a vanity address (facebook.com/<name>, 5+ letters, digits or dots),
// profile.php?id=<id>, /pages/<name>/<id> or /p/<name-id>.
export function facebookTarget(raw) {
  let s = String(raw || '').trim();
  if (!s) return null;
  if (!/^https?:\/\//i.test(s)) s = 'https://' + s;
  let u;
  try {
    u = new URL(s);
  } catch {
    return null;
  }
  if (!/^(?:(?:www|m|web|mbasic)\.)?(?:facebook|fb)\.com$/i.test(u.hostname)) return null;
  const path = u.pathname;
  let m = path.match(/^\/groups\/([^/?#]+)/i);
  if (m) return /[A-Za-z0-9]/.test(m[1]) && /^[A-Za-z0-9._-]+$/.test(m[1]) ? { kind: 'group', url: `https://www.facebook.com/groups/${m[1]}/` } : null;
  if (/^\/profile\.php$/i.test(path)) {
    const id = u.searchParams.get('id') || '';
    return /^\d{5,}$/.test(id) ? { kind: 'page', url: `https://www.facebook.com/profile.php?id=${id}` } : null;
  }
  m = path.match(/^\/pages\/([^/?#]+)\/(\d{5,})/i);
  if (m) return { kind: 'page', url: `https://www.facebook.com/pages/${m[1]}/${m[2]}` };
  m = path.match(/^\/p\/([A-Za-z0-9._%-]+)(?:\/|$)/i);
  if (m && /[A-Za-z0-9]/.test(m[1])) return { kind: 'page', url: `https://www.facebook.com/p/${m[1]}` };
  m = path.match(/^\/([A-Za-z0-9.]{5,})(?:\/|$)/);
  if (m && !RESERVED_PAGE_SEGMENTS.has(m[1].toLowerCase()) && /[A-Za-z]/.test(m[1])) return { kind: 'page', url: `https://www.facebook.com/${m[1]}` };
  return null;
}

// Accepts a Facebook group or page address in many shapes and returns its canonical link, or null.
export function normalizeGroupUrl(raw) {
  const t = facebookTarget(raw);
  return t ? t.url : null;
}

export function parseGroups(text) {
  const valid = [];
  const invalid = [];
  const seen = new Set();
  for (const line of String(text || '').split(/\r?\n/)) {
    const t = line.trim();
    if (!t || t.startsWith('#')) continue;
    const url = normalizeGroupUrl(t);
    if (!url) invalid.push(t);
    else if (!seen.has(url)) {
      seen.add(url);
      valid.push(url);
    }
  }
  return { valid, invalid };
}

// Reads a pasted group list:
//   หัวข้อหมวด                      -> names the groups below it ("หมวด 1", "หมวด 2"…)
//   https://…/groups/123           -> group
//   https://…/groups/123 | รหัส X1  -> group with its own text (first line / {{code}})
//   https://…/groups/123 (หมายเหตุ) -> group added switched off, note kept in its name
//   https://…/groups/123 [1/วัน]   -> at most 1 post in 24 hours in this group
//                                     ([วันละ 3], [3 ครั้ง/วัน]; [ไม่จำกัด] = no limit)
// Returns { groups: [{url, name, text, enabled, note, dailyMax}], invalid: [lines] }.
export function parseGroupList(text) {
  const groups = [];
  const invalid = [];
  const seen = new Set();
  const perCategory = new Map();
  let category = '';
  for (const raw of String(text || '').split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    const m = line.match(/^(\S*(?:facebook\.com|fb\.com)\/\S+)\s*(.*)$/i);
    if (!m) {
      // Not a link: a category heading (short text without "|").
      if (line.length <= 60 && !line.includes('|')) category = line.replace(/[:：]\s*$/, '');
      else invalid.push(line);
      continue;
    }
    const url = normalizeGroupUrl(m[1]);
    if (!url) {
      invalid.push(line);
      continue;
    }
    if (seen.has(url)) continue;
    seen.add(url);
    let rest = m[2].trim();
    let dailyMax = 0;
    const lm = rest.match(/\[\s*(?:วันละ\s*(\d+)\s*(?:ครั้ง|โพสต์)?|(\d+)\s*(?:ครั้ง|โพสต์)?\s*\/\s*วัน|ไม่จำกัด)\s*\]/);
    if (lm) {
      dailyMax = groupDailyMax(lm[1] || lm[2] || 0);
      rest = (rest.slice(0, lm.index) + ' ' + rest.slice(lm.index + lm[0].length)).trim();
    }
    let note = '';
    const nm = rest.match(/^\((.*)\)\s*(.*)$/);
    if (nm) {
      note = nm[1].trim();
      rest = nm[2].trim();
    }
    const groupText = rest.startsWith('|') ? rest.slice(1).trim() : '';
    let name = '';
    if (category) {
      const n = (perCategory.get(category) || 0) + 1;
      perCategory.set(category, n);
      name = `${category} ${n}`;
    }
    if (note) name = name ? `${name} (${note})` : `(${note})`;
    groups.push({ url, name, text: groupText, enabled: !note, note, dailyMax });
  }
  return { groups, invalid };
}

export function usablePosts(posts) {
  return (posts || []).filter(
    (p) => (p.text && p.text.trim()) || (p.imageIds && p.imageIds.length) || (p.imageUrls && p.imageUrls.length)
  );
}

function parseHM(s) {
  const [h, m] = String(s || '0:0').split(':').map(Number);
  return (h || 0) * 60 + (m || 0);
}

export function isWithinActive(date, start, end) {
  const mins = date.getHours() * 60 + date.getMinutes();
  const a = parseHM(start);
  const b = parseHM(end);
  if (a === b) return true;
  return a < b ? mins >= a && mins < b : mins >= a || mins < b;
}

// Next timestamp (ms) when posting is allowed.
export function nextActiveStart(date, start, end) {
  if (isWithinActive(date, start, end)) return date.getTime();
  const a = parseHM(start);
  const d = new Date(date);
  d.setHours(Math.floor(a / 60), a % 60, 0, 0);
  if (d.getTime() <= date.getTime()) d.setDate(d.getDate() + 1);
  return d.getTime();
}

export function fmtDateTime(ts) {
  if (!ts) return '-';
  const d = new Date(ts);
  const pad = (n) => String(n).padStart(2, '0');
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

export function fmtDuration(ms) {
  if (ms <= 0) return '0 วิ';
  const s = Math.round(ms / 1000);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  if (h) return `${h} ชม. ${m} นาที`;
  if (m) return `${m} นาที ${sec} วิ`;
  return `${sec} วิ`;
}

export function uid() {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
}

// groupUrls: groups this post may be used for ([] = every group of the campaign).
export function newPost() {
  return { id: uid(), text: '', imageIds: [], imageUrls: [], groupUrls: [] };
}

// leadImageIds: media (images/videos) attached first to every post of the campaign.
export function newCampaign(n) {
  return {
    id: uid(),
    name: `ชุดที่ ${n}`,
    enabled: true,
    groups: [],
    posts: [newPost()],
    leadImageIds: [],
    config: { ...DEFAULT_CONFIG },
  };
}

// Every stored media id a campaign uses (lead media first).
export function campaignImageIds(c) {
  return [...new Set([...(c.leadImageIds || []), ...c.posts.flatMap((p) => p.imageIds || [])])];
}

// Media attached to one post: campaign lead media, then the post's own.
// With randomize: lead media only with the campaign's leadChancePct, and the
// post's own images in a random order (lead media always stays first).
export function postMediaIds(c, post, randomize = false) {
  const cfg = c.config || {};
  let lead = c.leadImageIds || [];
  let own = post.imageIds || [];
  if (randomize) {
    if (lead.length && !chance(cfg.leadChancePct ?? 100)) lead = [];
    if (cfg.shuffleImages !== false) own = shuffle(own);
  }
  return [...new Set([...lead, ...own])];
}

// Keeps only the keys of `defaults`, each with the same type (numbers >= 0).
// Protects against hand-edited or imported files with wrong values.
export function sanitizeLike(defaults, obj) {
  const src = obj && typeof obj === 'object' ? obj : {};
  const out = {};
  for (const [k, d] of Object.entries(defaults)) {
    const v = src[k];
    if (typeof d === 'number') {
      const n = typeof v === 'string' && v.trim() !== '' ? Number(v) : v;
      out[k] = typeof n === 'number' && Number.isFinite(n) ? Math.max(0, n) : d;
    } else if (typeof d === 'boolean') {
      out[k] = typeof v === 'boolean' ? v : d;
    } else if (typeof d === 'string') {
      out[k] = typeof v === 'string' ? v : d;
    } else {
      out[k] = v === undefined ? d : v;
    }
  }
  return out;
}

// Posts allowed in one group within 24 hours (the group's rule): 0 = no limit.
export const GROUP_DAILY_MAX_LIMIT = 50;
export function groupDailyMax(v) {
  const n = Math.floor(Number(v));
  return Number.isFinite(n) && n > 0 ? Math.min(n, GROUP_DAILY_MAX_LIMIT) : 0;
}

const asArray = (v) => (Array.isArray(v) ? v : []);
const asObject = (v) => (v && typeof v === 'object' ? v : {});

function normalizeCampaign(c, i) {
  c = asObject(c);
  return {
    id: typeof c.id === 'string' && c.id ? c.id : uid(),
    name: typeof c.name === 'string' && c.name ? c.name : `ชุดที่ ${i + 1}`,
    enabled: c.enabled !== false,
    groups: asArray(c.groups).map((g) => ({
      url: String(asObject(g).url || ''),
      name: String(asObject(g).name || ''),
      text: String(asObject(g).text || ''),
      enabled: asObject(g).enabled !== false,
      dailyMax: groupDailyMax(asObject(g).dailyMax),
    })),
    posts: asArray(c.posts).map((p) => ({
      id: typeof asObject(p).id === 'string' && p.id ? p.id : uid(),
      text: String(asObject(p).text || ''),
      imageIds: asArray(asObject(p).imageIds).filter((x) => typeof x === 'string'),
      // Media kept in object storage: fetched when the post goes out instead of stored in the browser.
      imageUrls: asArray(asObject(p).imageUrls).filter((x) => typeof x === 'string' && /^https?:\/\//i.test(x)),
      // Links that cannot be read stay as they are, so the post stays limited
      // (it matches no group) instead of silently fitting every group.
      groupUrls: [
        ...new Set(
          asArray(asObject(p).groupUrls)
            .filter((u) => typeof u === 'string' && u.trim())
            .map((u) => normalizeGroupUrl(u) || u.trim())
        ),
      ],
    })),
    leadImageIds: asArray(c.leadImageIds).filter((x) => typeof x === 'string'),
    config: sanitizeConfig(c.config),
  };
}

function sanitizeConfig(raw) {
  const cfg = sanitizeLike(DEFAULT_CONFIG, raw);
  if (!['afterGreeting', 'top', 'end'].includes(cfg.footerPosition)) cfg.footerPosition = DEFAULT_CONFIG.footerPosition;
  cfg.leadChancePct = Math.min(100, cfg.leadChancePct);
  cfg.skipChancePct = Math.min(90, cfg.skipChancePct);
  cfg.recentAvoid = Math.floor(cfg.recentAvoid);
  return cfg;
}

// Reads stored settings of any version and returns the version 2 shape.
export function migrateSettings(raw) {
  raw = asObject(raw);
  if (Array.isArray(raw.campaigns)) {
    return {
      version: 2,
      global: normalizeGlobal(raw.global),
      campaigns: raw.campaigns.map(normalizeCampaign),
    };
  }
  // Version 1: one flat list of groups/posts/config.
  const campaigns = [];
  if (raw.groupsText || (raw.posts && raw.posts.length)) {
    const c = newCampaign(1);
    c.groups = parseGroups(raw.groupsText).valid.map((url) => ({ url, text: '', enabled: true }));
    if (raw.posts && raw.posts.length) c.posts = raw.posts;
    for (const k of Object.keys(DEFAULT_CONFIG)) if (k in raw) c.config[k] = raw[k];
    campaigns.push(normalizeCampaign(c, 0));
  }
  return {
    version: 2,
    global: normalizeGlobal({ focusWindow: raw.focusWindow !== false }),
    campaigns,
  };
}

// Enabled groups of a campaign with valid links, de-duplicated.
export function activeGroups(campaign) {
  const out = [];
  const seen = new Set();
  for (const g of campaign.groups || []) {
    if (g.enabled === false) continue;
    const url = normalizeGroupUrl(g.url);
    if (!url || seen.has(url)) continue;
    seen.add(url);
    out.push({ url, text: g.text || '', name: g.name || '' });
  }
  return out;
}

// Posts a group may pick from: every usable post of the campaign. Posts are not
// tied to groups (groupUrls, kept from imports where a post was first made, no
// longer limits anything).
export function postsForGroup(campaign, url) { // eslint-disable-line no-unused-vars
  return usablePosts(campaign.posts);
}

// Active groups that have no post they may use (they get skipped).
export function groupsWithoutPosts(campaign) {
  return activeGroups(campaign).filter((g) => !postsForGroup(campaign, g.url).length);
}

// Text that greets the group: "ขออนุญาต…", "สวัสดี…".
const GREETING_START_RE = /^(ขออนุญาต|สวัสดี|hello\b|hi\b)/i;
// Polite ending of a greeting phrase. "นะคะ/นะครับ/ค่ะ/ครับ" may be followed by
// more letters ("สวัสดีค่ะลูกค้า"); the short ones only count at a word end
// ("คะแนน" and "เจ้าของ" are not endings).
const PARTICLE_RE = /(นะคะ|นะครับ|ค่ะ|ครับ)|(?<!เ)(คะ|จ้า|จ้ะ)(?=[\s!.,~?]|$)/;
// Group codes that stay first: "#Jan240015", "#Kru320/01/23", "Feb230003".
// (Not product names or prices such as "iPhone15", "PS5", "2,500,000".)
const CODE_TOKEN_RE = /^(#\S*\d\S*|[A-Za-z0-9]*\/[!-~]*|[A-Za-z][A-Za-z0-9]*\d{4,}[A-Za-z0-9]*)$/;
// A brand/tag line before the greeting, e.g. "SIRISTUDIOPHOTO", "#SIRISTUDIOPHOTO".
// (Thai has no spaces, so a one-word Thai line is content, not a tag.)
const TAG_LINE_RE = /^(#\S{1,40}|[A-Za-z0-9._@&+-]{2,30})$/;

// Length of the leading group-code tokens of a line (0 when none).
function codePrefixEnd(line) {
  const re = /\S+/g;
  let end = 0;
  let m;
  while ((m = re.exec(line))) {
    if (!CODE_TOKEN_RE.test(m[0])) break;
    end = m.index + m[0].length;
  }
  return end;
}

// Group codes at the start of a post (code lines and codes leading the first
// lines), e.g. "#Jan240015 #Kru320/01/23". '' when none.
export function leadingCodes(text) {
  const codes = [];
  let seen = 0;
  for (const line of String(text || '').split(/\r?\n/)) {
    if (!line.trim()) continue;
    const end = codePrefixEnd(line);
    if (end) codes.push(...line.slice(0, end).trim().split(/\s+/));
    const rest = line.slice(end).trim();
    if (!rest) continue; // codes only
    if (++seen >= 3 || !TAG_LINE_RE.test(rest)) break;
  }
  return codes.join(' ');
}

// Group codes compare without "#" and case: "#Jan240015" = "jan240015".
export const codeKey = (token) => String(token).replace(/^#/, '').toLowerCase();

// One group code inside a word of the post: "#Jan240015", "Jan240015,",
// "รหัสสมาชิก#Jan240015". Not part of a longer code ("#Jan2400151",
// "X#Jan240015") and never inside a link.
const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&');
function codeFinder(keys) {
  const alts = [...keys].sort((a, b) => b.length - a.length).map(escapeRe).join('|');
  return new RegExp(`(^|[^A-Za-z0-9/#])#?(?:${alts})(?![A-Za-z0-9/])[.,;:!?)\\]]*`, 'gi');
}

// Takes the group codes (keys: codeKey) out of the post, wherever they are.
// Other hashtags, dates and links stay as they are. Returns { text, codes }:
// the post without the codes (lines left empty go away) and the codes found,
// in order, as written in the post.
export function stripCodes(text, keys) {
  const src = String(text || '');
  const codes = [];
  if (!keys || !keys.size) return { text: src, codes };
  const find = codeFinder(keys);
  const out = [];
  for (const line of src.split('\n')) {
    let hit = false;
    const rest = line.replace(/\S+/g, (word) => {
      if (/:\/\/|^www\./i.test(word)) return word; // a link
      return word.replace(find, (m, before) => {
        hit = true;
        codes.push(m.slice(before.length).replace(/[.,;:!?)\]]+$/, ''));
        return before;
      });
    });
    if (!hit) {
      out.push(line);
      continue;
    }
    const cr = line.endsWith('\r') ? '\r' : '';
    const kept = rest.replace(/[ \t]{2,}/g, ' ').replace(/\s+$/, '');
    if (kept.trim()) out.push((line.match(/^[ \t]*/)[0] + kept.trimStart()) + cr); // a line of codes only goes away
  }
  if (!codes.length) return { text: src, codes };
  return { text: out.join('\n').replace(/^\s*\n/, ''), codes };
}

// End of one greeting phrase starting at `from`: after the polite particle and
// the rest of that word, else at a double space, else the end of the text.
function greetingEnd(text, from) {
  const part = text.slice(from);
  const pm = PARTICLE_RE.exec(part);
  if (pm) {
    const end = pm.index + pm[0].length;
    const ws = part.slice(end).search(/\s/);
    return from + (ws < 0 ? part.length : end + ws);
  }
  const dbl = part.search(/\s{2,}/);
  return from + (dbl > 0 ? dbl : part.length);
}

// Inserts the block right after the greeting phrase, before the content.
// The greeting may follow lines of group codes or a one-word brand/tag line
// (up to 3 such lines); group codes always stay first. Without a greeting the
// block goes before the first content line.
function insertAfterGreeting(body, block) {
  const lines = body.split('\n');
  let contentLine = -1;
  let tags = 0;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!line.trim()) continue;
    const codeEnd = codePrefixEnd(line);
    const rest = line.slice(codeEnd);
    const start = rest.search(/\S/);
    if (start < 0) continue; // a line of codes only
    const text = rest.slice(start);
    if (GREETING_START_RE.test(text)) {
      // A greeting can come in parts: "สวัสดีค่ะ ขออนุญาตแอดมินนะคะ".
      let end = greetingEnd(text, 0);
      for (let guard = 0; guard < 3; guard++) {
        const next = end + text.slice(end).search(/\S|$/);
        if (next >= text.length || !GREETING_START_RE.test(text.slice(next))) break;
        end = greetingEnd(text, next);
      }
      const before = line.slice(0, codeEnd + start + end).replace(/\s+$/, '');
      const after = text.slice(end).replace(/^\s+/, '');
      return [...lines.slice(0, i), before, block, ...(after ? [after] : []), ...lines.slice(i + 1)].join('\n');
    }
    if (!codeEnd && TAG_LINE_RE.test(text) && ++tags <= 3) continue; // brand/tag line
    contentLine = i; // real content before any greeting
    break;
  }
  if (contentLine < 0) return `${body.replace(/\s+$/, '')}\n${block}`; // codes/tags only
  const line = lines[contentLine];
  const codeEnd = codePrefixEnd(line);
  const head = codeEnd ? [line.slice(0, codeEnd).replace(/\s+$/, '')] : [];
  const tail = line.slice(codeEnd).replace(/^\s+/, '');
  const top = !lines.slice(0, contentLine).some((l) => l.trim()) && !codeEnd;
  return [
    ...lines.slice(0, contentLine),
    ...head,
    block + (top ? '\n' : ''),
    tail,
    ...lines.slice(contentLine + 1),
  ].join('\n');
}

// Puts the campaign text block into the post body.
//   'end':           after a blank line at the end
//   'afterGreeting': right after "ขออนุญาต…/สวัสดี…", before the content
//   'top':           first, before the whole content (group codes stay above it)
function insertBlock(body, block, position) {
  if (!body.trim()) return block;
  if (position === 'end') return `${body.replace(/\s+$/, '')}\n\n${block}`;
  if (position === 'top') {
    const lines = body.replace(/^\s*\n/, '').split('\n');
    let i = 0;
    while (i < lines.length && lines[i].trim() && codePrefixEnd(lines[i]) === lines[i].trimEnd().length) i++; // code-only lines
    return [...lines.slice(0, i), block, '', ...lines.slice(i)].join('\n');
  }
  return insertAfterGreeting(body, block);
}

// Whitespace-insensitive "a contains b" (trailing spaces, CRLF, double spaces).
const squash = (s) => String(s || '').replace(/\r\n?/g, '\n').replace(/[ \t]+/g, ' ').replace(/ ?\n ?/g, '\n').trim();
const contains = (a, b) => squash(a).includes(squash(b));
const hasPlaceholder = (s) => new RegExp(CODE_RE.source, 'i').test(s);

// Page tags (mentions) of the pageTags setting: [{ name, query }], one page per
// line "page name | page link" (the link is only a note). name = the page's
// name exactly as on Facebook; query = what is typed after "@" to find it.
export function parsePageTags(raw) {
  const out = [];
  for (const line of String(raw || '').split(/\r?\n/)) {
    const name = line.split('|')[0].replace(/\s+/g, ' ').trim();
    if (!name) continue;
    let query = '';
    for (const w of name.split(' ')) {
      if (query && (query + ' ' + w).length > 20) break;
      query = query ? `${query} ${w}` : w;
    }
    out.push({ name, query });
  }
  return out;
}

// Splits the final post text where it names a tagged page (first place for
// each page, spacing may differ): [{ text } | { tag: { name, query } }].
// A tag always follows a space or a line start, else "@" finds nothing.
export function splitPageTags(text, raw) {
  const src = String(text || '');
  const hits = [];
  for (const tag of parsePageTags(raw)) {
    const re = new RegExp(tag.name.split(' ').map(escapeRe).join('\\s+'), 'i');
    const m = re.exec(src);
    if (m) hits.push({ start: m.index, end: m.index + m[0].length, tag });
  }
  hits.sort((a, b) => a.start - b.start);
  const out = [];
  let at = 0;
  for (const h of hits) {
    if (h.start < at) continue; // overlaps an earlier tag
    let before = src.slice(at, h.start);
    if (before && !/\s$/.test(before)) before += ' ';
    if (before) out.push({ text: before });
    out.push({ tag: h.tag });
    at = h.end;
  }
  if (at < src.length) out.push({ text: src.slice(at) });
  return out;
}

// Final text for one group: spun post text + the group's own text + the
// campaign text block (links, contact).
// "{{code}}" (or "{{รหัส}}") marks where the group text goes (in the post or in
// the block); without it the group text becomes the first line unless the post
// already contains it. A group text of codes ("#Jan240015 #Kru320/01/23") is
// always the first line, exactly once: the same codes anywhere in the post are
// taken out (also written without "#" or glued: "Kru320/01/23Feb230003").
// Empty group text adds nothing. The block is not added when the post already
// contains it (compared before and after spinning, ignoring spacing).
export function composeText(postText, groupText, footer = '', position = 'end') {
  const code = spin(groupText || '').trim();
  const tokens = code ? code.split(/\s+/) : [];
  const codeLike = tokens.length > 0 && tokens.every((t) => CODE_TOKEN_RE.test(t));
  const fill = (s) => {
    if (!hasPlaceholder(s)) return s;
    const onlyCode = new RegExp(`^\\s*${CODE_RE.source}\\s*$`, 'i');
    const kept = code ? s : s.split('\n').filter((line) => !onlyCode.test(line)).join('\n');
    return kept.replace(CODE_RE, () => code);
  };
  const rawBody = spin(postText || '').replace(/\r\n?/g, '\n');
  const rawBlock = spin(footer || '').replace(/\r\n?/g, '\n').trim();
  const placeholder = hasPlaceholder(rawBody) || hasPlaceholder(rawBlock);
  const filled = fill(rawBody);
  const codesFirst = codeLike && !placeholder;
  let text = codesFirst ? stripCodes(filled, new Set([...tokens.map(codeKey), codeKey(tokens.join('').replace(/#/g, ''))])).text : filled;
  const block = fill(rawBlock).trim();
  const already = block && (contains(filled, block) || contains(postText, footer));
  if (block && !already) text = insertBlock(text, block, position);
  if (codesFirst || (code && !placeholder && !contains(text, code))) text = text ? `${code}\n${text}` : code; // free text, e.g. "รหัส: X1", not repeated
  return text;
}

// Literal find & replace in post texts (and campaign footers).
// pairs: [[find, replace], ...]. campaignIds: null = every campaign.
// Changes the settings in place and returns the number of replacements.
export function replaceInPosts(settings, pairs, campaignIds = null) {
  let count = 0;
  const swap = (text) => {
    let out = String(text || '');
    for (const [find, repl] of pairs) {
      if (!find) continue;
      const parts = out.split(find);
      count += parts.length - 1;
      out = parts.join(repl);
    }
    return out;
  };
  for (const c of settings.campaigns) {
    if (campaignIds && !campaignIds.includes(c.id)) continue;
    for (const p of c.posts) p.text = swap(p.text);
    c.config.footer = swap(c.config.footer);
  }
  return count;
}

// Problems that stop a campaign from running ('' when ready).
export function campaignProblem(c) {
  if (!activeGroups(c).length) return 'ยังไม่มีลิงก์กลุ่มที่ถูกต้อง';
  if (!usablePosts(c.posts).length) return 'ยังไม่มีเนื้อหาโพสต์';
  if (groupsWithoutPosts(c).length === activeGroups(c).length) return 'ไม่มีโพสต์ที่ตั้งให้ใช้กับกลุ่มในชุดนี้';
  const k = c.config;
  if (!(k.groupDelayMin > 0) || k.groupDelayMax < k.groupDelayMin) return 'หน่วงระหว่างกลุ่มไม่ถูกต้อง';
  if (!(k.roundIntervalHours > 0)) return 'เว้นระหว่างรอบไม่ถูกต้อง';
  return '';
}
