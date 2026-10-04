// Settings backup file: build (download) and read/merge (upload).
// Pure functions; the dashboard does the storage and file work.
import { migrateSettings, uid, campaignImageIds } from './shared.js';

export const BACKUP_APP = 'fb-group-autopost';
export const BACKUP_FORMAT = 1;

// Config file inside the extension folder. A fresh install (or empty storage)
// loads it automatically, so copying the folder to another computer is enough.
export const BUNDLED_CONFIG_PATH = 'config/autopost-config.json';

// True when settings hold real user data (not just an empty default campaign).
export function hasContent(settings) {
  return settings.campaigns.some(
    (c) =>
      c.groups.length ||
      (c.leadImageIds || []).length ||
      c.posts.some((p) => p.text.trim() || p.imageIds.length || (p.imageUrls || []).length)
  );
}

const clone = (v) => JSON.parse(JSON.stringify(v));

// Every stored media id (images and videos) the campaigns use.
export function imageIdsOf(campaigns) {
  return [...new Set(campaigns.flatMap(campaignImageIds))];
}

// settings: version 2 settings. images: { id: { name, type, data } }.
export function buildBackup(settings, images, opts = {}) {
  const { includeImages = true, includeToken = false, campaignIds = null, version = '', autoStart = false } = opts;
  const s = clone(settings);
  if (campaignIds) s.campaigns = s.campaigns.filter((c) => campaignIds.includes(c.id));
  if (!includeToken) s.global.telegram.botToken = '';
  const out = {
    app: BACKUP_APP,
    format: BACKUP_FORMAT,
    exportedAt: new Date().toISOString(),
    extensionVersion: version,
    includesImages: !!includeImages,
    includesToken: !!includeToken,
    autoStart: !!autoStart, // start posting right after a fresh install loads this file
    settings: s,
  };
  if (includeImages) {
    out.images = {};
    for (const id of imageIdsOf(s.campaigns)) if (images[id]) out.images[id] = images[id];
  }
  return out;
}

function validImage(rec) {
  return (
    rec &&
    typeof rec === 'object' &&
    typeof rec.data === 'string' &&
    /^data:(image|video)\/[a-z0-9.+-]+;base64,/i.test(rec.data)
  );
}

// Reads an uploaded file. Accepts a backup file, or a raw settings object
// (version 1 or 2). Throws an Error with a Thai message when unusable.
export function parseBackup(text) {
  let data;
  try {
    data = JSON.parse(text);
  } catch {
    throw new Error('ไฟล์นี้ไม่ใช่ไฟล์ JSON');
  }
  if (!data || typeof data !== 'object' || Array.isArray(data)) throw new Error('รูปแบบไฟล์ไม่ถูกต้อง');
  let raw;
  let images = {};
  let meta = {};
  let autoStart = false;
  if (data.app === BACKUP_APP) {
    if (Number(data.format) > BACKUP_FORMAT) throw new Error('ไฟล์มาจากส่วนขยายเวอร์ชันใหม่กว่า ให้อัปเดตส่วนขยายก่อน');
    raw = data.settings;
    images = data.images && typeof data.images === 'object' ? data.images : {};
    meta = { exportedAt: data.exportedAt || '', version: data.extensionVersion || '' };
    autoStart = data.autoStart === true;
  } else if (Array.isArray(data.campaigns) || 'groupsText' in data) {
    raw = data;
  } else {
    throw new Error('ไม่ใช่ไฟล์ตั้งค่าของ FB AutoPost');
  }
  const settings = migrateSettings(raw);
  const clean = {};
  let badImages = 0;
  for (const [id, rec] of Object.entries(images)) {
    if (validImage(rec)) clean[id] = { name: String(rec.name || 'image.jpg'), type: String(rec.type || ''), data: rec.data };
    else badImages++;
  }
  return { settings, images: clean, meta, badImages, autoStart };
}

export function summarize(settings, images) {
  const groups = settings.campaigns.reduce((n, c) => n + c.groups.length, 0);
  const posts = settings.campaigns.reduce((n, c) => n + c.posts.length, 0);
  return { campaigns: settings.campaigns.length, groups, posts, images: Object.keys(images).length };
}

// Combines the current settings with an uploaded backup.
//   mode 'replace': the file replaces everything (when the file has no Bot
//                   Token, this computer's Telegram settings are kept).
//   mode 'merge':   the file's campaigns are added as new campaigns (new ids),
//                   current campaigns and global settings stay.
// existingImageIds: image ids already in storage.
// Returns { settings, writeImages: {id: rec}, copyImages: [[fromId, toId]], missingImages }.
export function applyImport(current, parsed, mode, existingImageIds = new Set()) {
  const has = (id) => !!parsed.images[id] || existingImageIds.has(id);
  let missingImages = 0;
  const keepAvailable = (ids) =>
    ids.filter((id) => {
      if (has(id)) return true;
      missingImages++;
      return false;
    });

  if (mode === 'replace') {
    const settings = clone(parsed.settings);
    // A file without a Bot Token carries no Telegram setup: keep this computer's.
    if (!settings.global.telegram.botToken) settings.global.telegram = clone(current.global.telegram);
    for (const c of settings.campaigns) {
      c.leadImageIds = keepAvailable(c.leadImageIds || []);
      for (const p of c.posts) p.imageIds = keepAvailable(p.imageIds);
    }
    const writeImages = {};
    for (const id of imageIdsOf(settings.campaigns)) if (parsed.images[id]) writeImages[id] = parsed.images[id];
    return { settings, writeImages, copyImages: [], missingImages };
  }

  // merge: every imported campaign/post/image gets a fresh id so nothing
  // collides with (or is shared with) the current campaigns.
  const settings = clone(current);
  const names = new Set(settings.campaigns.map((c) => c.name));
  const writeImages = {};
  const copyImages = [];
  const newIds = new Map(); // one new id per image, even when posts share it
  const remap = (oldId) => {
    if (newIds.has(oldId)) return newIds.get(oldId);
    const newId = uid();
    newIds.set(oldId, newId);
    if (parsed.images[oldId]) writeImages[newId] = parsed.images[oldId];
    else copyImages.push([oldId, newId]);
    return newId;
  };
  for (const src of parsed.settings.campaigns) {
    const c = clone(src);
    c.id = uid();
    if (names.has(c.name)) c.name = `${c.name} (นำเข้า)`;
    names.add(c.name);
    c.leadImageIds = keepAvailable(c.leadImageIds || []).map(remap);
    for (const p of c.posts) {
      p.id = uid();
      p.imageIds = keepAvailable(p.imageIds).map(remap);
    }
    settings.campaigns.push(c);
  }
  return { settings, writeImages, copyImages, missingImages };
}
