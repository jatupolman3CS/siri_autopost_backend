// Converts a "SIRI autopost export" zip (data.json + images/ + groups_urls.txt)
// into this extension's settings: posts, images, groups and the link
// between each post and the group it was posted in.
import { DEFAULT_CONFIG, migrateSettings, normalizeGroupUrl } from './shared.js';

const MIME = {
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  png: 'image/png',
  webp: 'image/webp',
  gif: 'image/gif',
  bmp: 'image/bmp',
};

export function bytesToBase64(bytes) {
  let bin = '';
  for (let i = 0; i < bytes.length; i += 0x8000) {
    bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  }
  return btoa(bin);
}

async function sha1Hex(bytes) {
  // Node 18 has no global WebCrypto; extension pages and Node 19+ do.
  // A variable specifier: browser bundlers (Vite, Angular) must not try to resolve a Node module.
  const cryptoModule = 'node:crypto';
  const subtle = globalThis.crypto?.subtle || (await import(/* @vite-ignore */ cryptoModule)).webcrypto.subtle;
  const digest = await subtle.digest('SHA-1', bytes);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

const findDataJson = (files) => [...files.keys()].find((n) => /(^|\/)data\.json$/i.test(n)) || null;

export function isSiriExport(files) {
  return !!findDataJson(files);
}

// files: Map(path -> Uint8Array) from readZip().
// Returns { settings, images, report }:
//   settings: version 2 settings with up to two campaigns
//     1. "<name> · โพสต์ในกลุ่ม": every group + the posts made in groups,
//        each post limited to the group it came from (groupUrls)
//     2. "<name> · โพสต์ในเพจ (ยังไม่ผูกกลุ่ม)": page posts, switched off
//   images:   { id: { name, type, data } } identical files stored once
export async function convertSiriExport(files, { name = 'SIRI' } = {}) {
  const dataPath = findDataJson(files);
  if (!dataPath) throw new Error('ไม่พบ data.json ในไฟล์ zip (ไม่ใช่ไฟล์ SIRI export)');
  const root = dataPath.slice(0, dataPath.length - 'data.json'.length);
  let data;
  try {
    data = JSON.parse(new TextDecoder().decode(files.get(dataPath)));
  } catch {
    throw new Error('data.json เสีย อ่านไม่ได้');
  }
  if (!data || !Array.isArray(data.posts)) throw new Error('data.json ไม่มีรายการ posts');

  // Groups: data.groups, then groups_urls.txt, then any group seen in posts.
  const groups = [];
  const byUrl = new Map();
  const addGroup = (rawUrl, groupName) => {
    const url = normalizeGroupUrl(rawUrl);
    if (!url) return null;
    let g = byUrl.get(url);
    if (!g) {
      g = { url, name: '', text: '', enabled: true };
      byUrl.set(url, g);
      groups.push(g);
    }
    if (groupName && !g.name) g.name = String(groupName).trim();
    return url;
  };
  for (const g of Array.isArray(data.groups) ? data.groups : []) {
    if (g && typeof g === 'object') addGroup(g.url, g.name);
  }
  const txt = files.get(root + 'groups_urls.txt');
  if (txt) for (const line of new TextDecoder().decode(txt).split(/\r?\n/)) addGroup(line.trim());

  // Images, stored once per identical file.
  const images = {};
  const idByHash = new Map();
  let missingImages = 0;
  let duplicateImages = 0;
  const loadImage = async (path) => {
    const rel = String(path || '').replace(/\\/g, '/').replace(/^\.?\//, '');
    const key = files.has(root + rel) ? root + rel : rel;
    const bytes = files.get(key);
    if (!bytes || !bytes.length) {
      missingImages++;
      return null;
    }
    const hash = await sha1Hex(bytes);
    if (idByHash.has(hash)) {
      duplicateImages++;
      return idByHash.get(hash);
    }
    const ext = (key.split('.').pop() || '').toLowerCase();
    const type = MIME[ext] || 'image/jpeg';
    const id = 'siri-' + hash.slice(0, 20);
    images[id] = { name: key.split('/').pop(), type, data: `data:${type};base64,${bytesToBase64(bytes)}` };
    idByHash.set(hash, id);
    return id;
  };

  const groupPosts = [];
  const pagePosts = [];
  const usedIds = new Set();
  let emptyPosts = 0;
  for (const [i, p] of data.posts.entries()) {
    if (!p || typeof p !== 'object') continue;
    const imageIds = [];
    for (const path of Array.isArray(p.images) ? p.images : []) {
      const id = await loadImage(path);
      if (id && !imageIds.includes(id)) imageIds.push(id);
    }
    const text = typeof p.message === 'string' ? p.message : '';
    if (!text.trim() && !imageIds.length) {
      emptyPosts++;
      continue;
    }
    let id = 'siri-' + String(p.id || `post${i + 1}`).replace(/[^\w-]/g, '');
    while (usedIds.has(id)) id += '_';
    usedIds.add(id);
    const url = p.groupUrl ? addGroup(p.groupUrl, p.group) : null;
    const post = { id, text, imageIds, groupUrls: url ? [url] : [] };
    (url ? groupPosts : pagePosts).push(post);
  }

  const campaigns = [];
  if (groups.length || groupPosts.length) {
    campaigns.push({
      id: 'siri-groups',
      name: `${name} · โพสต์ในกลุ่ม`,
      enabled: groupPosts.length > 0,
      groups,
      posts: groupPosts,
      config: { ...DEFAULT_CONFIG },
    });
  }
  if (pagePosts.length) {
    campaigns.push({
      id: 'siri-page',
      name: `${name} · โพสต์ในเพจ (ยังไม่ผูกกลุ่ม)`,
      enabled: false,
      groups: [],
      posts: pagePosts,
      config: { ...DEFAULT_CONFIG },
    });
  }
  if (!campaigns.length) throw new Error('ไม่พบกลุ่มหรือโพสต์ในไฟล์');

  const withPosts = new Set(groupPosts.flatMap((p) => p.groupUrls));
  return {
    settings: migrateSettings({ version: 2, global: {}, campaigns }),
    images,
    report: {
      groups: groups.length,
      groupPosts: groupPosts.length,
      pagePosts: pagePosts.length,
      images: Object.keys(images).length,
      duplicateImages,
      missingImages,
      emptyPosts,
      groupsWithoutPosts: groups.filter((g) => !withPosts.has(g.url)).length,
    },
  };
}

// "SIRI_autopost_export.zip" -> "SIRI"
export function nameFromFile(fileName) {
  const base = String(fileName || '').replace(/\.zip$/i, '');
  return base.split(/[_\s-]+/)[0] || base || 'SIRI';
}
