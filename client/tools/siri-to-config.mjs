// Converts a SIRI autopost export zip into config/autopost-config.json, the
// file the extension loads automatically on a fresh install.
//
// Usage:
//   node tools/siri-to-config.mjs <export.zip> [output.json] [options]
//
// Options (all optional, most repeatable):
//   --footer "a\nb"        text added to every post of every campaign (\n = new line)
//   --footer-position p    afterGreeting (default: after "ขออนุญาต…/สวัสดี…") | top (before the content, under the group code) | end
//   --lead <file>          image/video attached first to every post (repeatable, in order)
//   --replace "old=>new"   literal replacement in every post (repeatable)
//   --share-page-posts     page posts become shared posts every group may use
//   --keep-page-posts ids  comma list of page post ids (e.g. P055,P067) that stay
//                          in the switched-off page campaign instead
//   --add-groups <file>    extra groups for the groups campaign: a text file with
//                          category headings, links, "| group text", "(note)"
//                          (a note adds the group switched off), see parseGroupList;
//                          a group already in the campaign is not added again,
//                          but its "| group text" (e.g. a new code) replaces the old one
//   --set key=value        campaign setting for every campaign (repeatable),
//                          e.g. --set groupDelayMin=8 --set activeHoursEnabled=true
//   --global key=value     global setting (repeatable), e.g. --global dailyMaxPosts=30
//   --all-groups           every post of the groups campaign may go to any group
//                          (each group picks at random); run after the group codes
//                          are taken from the groups' own posts
//   --codes-to-groups      group codes (e.g. "#Jan240015 #Kru320/01/23") leave the
//                          posts and become the text of the post's group, which
//                          goes on the first line when posting; the codes are
//                          the ones that start some post of the export
//   --drop-images <file>   image ids to take out of every post, one per line
//                          ("# ..." = comment)
//   --swap-images <dir>    edited copies of export images, named <image id>.jpg
//                          (or .png/.webp/.gif); each one replaces that image
import { readFile, writeFile, mkdir, readdir } from 'node:fs/promises';
import { dirname, resolve, basename, extname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { readZip } from '../lib/zip.js';
import { convertSiriExport, nameFromFile, bytesToBase64 } from '../lib/siri-import.js';
import { buildBackup, summarize, BUNDLED_CONFIG_PATH } from '../lib/backup.js';
import { replaceInPosts, migrateSettings, parseGroupList, leadingCodes, codeKey, stripCodes, DEFAULT_CONFIG, DEFAULT_GLOBAL } from '../lib/shared.js';

const MEDIA_TYPES = {
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.png': 'image/png',
  '.webp': 'image/webp',
  '.gif': 'image/gif',
  '.mp4': 'video/mp4',
  '.m4v': 'video/mp4',
  '.mov': 'video/quicktime',
  '.webm': 'video/webm',
};

const fail = (msg) => {
  console.error(msg);
  process.exit(1);
};

// Settings with a fixed set of values.
const CHOICES = {
  postMode: ['random', 'sequence'],
  typingSpeed: ['slow', 'normal', 'fast'],
  footerPosition: ['afterGreeting', 'top', 'end'],
};
const TIME_KEYS = ['activeStart', 'activeEnd'];

// "key=value" -> [key, value typed like the default]
function typedSetting(pair, defaults, flag) {
  const cut = pair.indexOf('=');
  if (cut <= 0) fail(`${flag} needs key=value, got: ${pair}`);
  const key = pair.slice(0, cut);
  const raw = pair.slice(cut + 1);
  if (!(key in defaults) || typeof defaults[key] === 'object') fail(`${flag}: unknown setting "${key}"`);
  const d = defaults[key];
  if (typeof d === 'boolean') {
    if (raw !== 'true' && raw !== 'false') fail(`${flag} ${key} must be true or false`);
    return [key, raw === 'true'];
  }
  if (typeof d === 'number') {
    const n = raw.trim() === '' ? NaN : Number(raw);
    if (!Number.isFinite(n) || n < 0) fail(`${flag} ${key} must be a number >= 0 (got "${raw}")`);
    return [key, n];
  }
  if (CHOICES[key] && !CHOICES[key].includes(raw)) fail(`${flag} ${key} must be one of: ${CHOICES[key].join(', ')}`);
  if (TIME_KEYS.includes(key) && !/^([01]\d|2[0-3]):[0-5]\d$/.test(raw)) fail(`${flag} ${key} must be HH:MM`);
  return [key, raw];
}

// Same id cleaning as the SIRI importer ("P.002" -> "P002"), case-insensitive.
const cleanId = (id) => String(id).replace(/[^\w-]/g, '').toUpperCase();

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
let footer = '';
let footerPosition = '';
let sharePagePosts = false;
let codesToGroups = false;
let allGroups = false;
const keepPagePosts = new Set();
const leads = [];
const groupFiles = [];
const dropFiles = [];
const swapDirs = [];
const replacements = [];
const sets = [];
const globals = [];
const positional = [];
for (let i = 0; i < args.length; i++) {
  const a = args[i];
  if (a === '--footer') footer = String(args[++i] || '').replace(/\\n/g, '\n');
  else if (a === '--footer-position') footerPosition = String(args[++i] || '');
  else if (a === '--lead') leads.push(String(args[++i] || ''));
  else if (a === '--add-groups') groupFiles.push(String(args[++i] || ''));
  else if (a === '--drop-images') dropFiles.push(String(args[++i] || ''));
  else if (a === '--swap-images') swapDirs.push(String(args[++i] || ''));
  else if (a === '--share-page-posts') sharePagePosts = true;
  else if (a === '--codes-to-groups') codesToGroups = true;
  else if (a === '--all-groups') allGroups = true;
  else if (a === '--keep-page-posts') String(args[++i] || '').split(',').map((s) => s.trim()).filter(Boolean).forEach((id) => keepPagePosts.add(id));
  else if (a === '--set') sets.push(typedSetting(String(args[++i] || ''), DEFAULT_CONFIG, '--set'));
  else if (a === '--global') globals.push(typedSetting(String(args[++i] || ''), DEFAULT_GLOBAL, '--global'));
  else if (a === '--replace') {
    const pair = String(args[++i] || '');
    const cut = pair.indexOf('=>');
    if (cut <= 0) fail(`--replace needs "old=>new", got: ${pair}`);
    replacements.push([pair.slice(0, cut), pair.slice(cut + 2)]);
  } else if (a.startsWith('--')) fail(`unknown option ${a}`);
  else positional.push(a);
}
const [zipPath, outArg] = positional;
if (!zipPath) fail('Usage: node tools/siri-to-config.mjs <export.zip> [output.json] [options] (see the top of this file)');
if (footerPosition && !['afterGreeting', 'top', 'end'].includes(footerPosition)) fail('--footer-position must be afterGreeting, top or end');
const out = resolve(outArg || resolve(root, BUNDLED_CONFIG_PATH));

const files = await readZip(await readFile(zipPath));
const converted = await convertSiriExport(files, { name: nameFromFile(basename(zipPath)) });
const { images, report } = converted;
let settings = converted.settings;

// Group codes leave the posts and go next to the group link (group text);
// when posting, the group text goes on the first line. A group gets every code
// seen in its own posts: the most used set first, then the others, each code
// once (spelling variants such as "Kru320/01/23Feb230003" count as the same).
const codeKeys = new Set();
const codesStripped = [];
const groupCodes = [];
if (codesToGroups) {
  for (const c of settings.campaigns) for (const p of c.posts) for (const t of leadingCodes(p.text).split(/\s+/)) if (t) codeKeys.add(codeKey(t));
  const norm = (s) => s.replace(/#/g, '').toLowerCase();
  for (const c of settings.campaigns) {
    const sets = new Map(); // group url -> Map(normalized set -> { tokens, count })
    for (const p of c.posts) {
      if (!p.groupUrls.length) continue; // shared posts keep their text
      const { text, codes } = stripCodes(p.text, codeKeys);
      if (!codes.length) continue;
      p.text = text;
      codesStripped.push(p.id.replace(/^siri-/, ''));
      for (const url of p.groupUrls) {
        if (!sets.has(url)) sets.set(url, new Map());
        const key = norm(codes.join(''));
        const s = sets.get(url).get(key) || { count: 0, spellings: new Map() };
        s.count++;
        s.spellings.set(codes.join(' '), (s.spellings.get(codes.join(' ')) || 0) + 1);
        sets.get(url).set(key, s);
      }
    }
    for (const g of c.groups) {
      if (g.text || !sets.has(g.url)) continue;
      const merged = [];
      for (const { spellings } of [...sets.get(g.url).values()].sort((a, b) => b.count - a.count)) {
        const tokens = [...spellings.entries()].sort((a, b) => b[1] - a[1])[0][0].split(' '); // the most used spelling
        for (const t of tokens) if (!norm(merged.join('')).includes(norm(t))) merged.push(t);
      }
      g.text = merged.join(' ');
      groupCodes.push(`${g.name || g.url}: ${g.text}`);
    }
  }
}

// Shared content: page posts every group may use (except the ones kept back).
let shared = 0;
let keptIds = [];
const codedGroups = [];
if (sharePagePosts) {
  const groupsCamp = settings.campaigns.find((c) => c.id === 'siri-groups');
  const pageCamp = settings.campaigns.find((c) => c.id === 'siri-page');
  if (!groupsCamp || !pageCamp) fail('--share-page-posts: this export has no group or page posts to share');
  const wanted = new Set([...keepPagePosts].map(cleanId));
  const keep = (p) => wanted.has(cleanId(p.id.replace(/^siri-/, '')));
  const unknown = [...wanted].filter((id) => !pageCamp.posts.some((p) => cleanId(p.id.replace(/^siri-/, '')) === id));
  if (unknown.length) fail(`--keep-page-posts: no page post with id ${unknown.join(', ')}`);
  const moved = pageCamp.posts.filter((p) => !keep(p));
  pageCamp.posts = pageCamp.posts.filter(keep);
  keptIds = pageCamp.posts.map((p) => p.id.replace(/^siri-/, ''));
  groupsCamp.posts.push(...moved.map((p) => ({ ...p, groupUrls: [] })));
  shared = moved.length;
  if (!pageCamp.posts.length) settings.campaigns = settings.campaigns.filter((c) => c !== pageCamp);
  if (groupsCamp.groups.length && groupsCamp.posts.length) groupsCamp.enabled = true;
  // Groups whose own posts carry a membership code (e.g. "#Jan240015
  // #Kru320/01/23") get it as their group text, so shared posts put it on the
  // first line too (posts that already start with it are left as they are).
  for (const g of groupsCamp.groups) {
    if (g.text) continue;
    const count = new Map();
    for (const p of groupsCamp.posts) {
      if (!p.groupUrls.includes(g.url)) continue;
      const codes = leadingCodes(p.text);
      if (codes) count.set(codes, (count.get(codes) || 0) + 1);
    }
    const best = [...count.entries()].sort((a, b) => b[1] - a[1])[0];
    if (best) {
      g.text = best[0];
      codedGroups.push(`${g.name || g.url}: ${best[0]}`);
    }
  }
}

// Every post may go to any group (after the group codes were read above).
let opened = 0;
if (allGroups) {
  const groupsCamp = settings.campaigns.find((c) => c.id === 'siri-groups');
  if (!groupsCamp) fail('--all-groups: this export has no groups campaign');
  for (const p of groupsCamp.posts) {
    if (p.groupUrls.length) opened++;
    p.groupUrls = [];
  }
}

// Extra groups (they use the shared posts; their own posts can be added later).
const added = { on: 0, off: 0, dup: 0, recoded: [], invalid: [] };
for (const file of groupFiles) {
  const groupsCamp = settings.campaigns.find((c) => c.id === 'siri-groups');
  if (!groupsCamp) fail('--add-groups: no groups campaign in this export');
  const { groups, invalid } = parseGroupList(await readFile(file, 'utf8'));
  added.invalid.push(...invalid);
  const have = new Map(groupsCamp.groups.map((g) => [g.url, g]));
  for (const g of groups) {
    const old = have.get(g.url);
    if (old) {
      added.dup++;
      if (g.text && g.text !== old.text) {
        added.recoded.push(`${old.name || old.url}: ${old.text || '(none)'} -> ${g.text}`);
        old.text = g.text;
      }
      continue;
    }
    have.set(g.url, g);
    groupsCamp.groups.push({ url: g.url, name: g.name, text: g.text, enabled: g.enabled, dailyMax: g.dailyMax || 0 });
    added[g.enabled ? 'on' : 'off']++;
  }
}

// Images taken out of every post (e.g. ones showing another shop's LINE).
const allPosts = () => settings.campaigns.flatMap((c) => c.posts);
const dropped = new Set();
for (const file of dropFiles) {
  let text;
  try {
    text = await readFile(file, 'utf8');
  } catch {
    fail(`--drop-images: cannot read ${file}`);
  }
  for (const line of text.split(/\r?\n/)) {
    const id = line.replace(/#.*/, '').trim();
    if (!id) continue;
    if (!images[id]) fail(`--drop-images: no image with id ${id} in this export`);
    dropped.add(id);
  }
}
const ownImagesBefore = new Set(allPosts().filter((p) => p.imageIds.length).map((p) => p.id));
for (const p of allPosts()) p.imageIds = p.imageIds.filter((id) => !dropped.has(id));
for (const id of dropped) delete images[id];

// Edited copies of export images (e.g. the new LINE QR code drawn over the
// old one). The copy gets its own id from its content, so a computer that
// loaded an older config cannot keep showing the old picture.
const swapped = [];
for (const dir of swapDirs) {
  let names;
  try {
    names = (await readdir(dir)).sort();
  } catch {
    fail(`--swap-images: cannot read folder ${dir}`);
  }
  for (const file of names) {
    const ext = extname(file).toLowerCase();
    const type = MEDIA_TYPES[ext];
    if (!type || !type.startsWith('image/')) continue;
    const oldId = basename(file, extname(file));
    if (dropped.has(oldId)) fail(`--swap-images: ${file} is also in --drop-images`);
    if (!images[oldId]) fail(`--swap-images: ${file} matches no image in this export (name it <image id>${ext})`);
    const bytes = await readFile(resolve(dir, file));
    const newId = 'siri-' + createHash('sha1').update(bytes).digest('hex').slice(0, 20);
    const name = images[oldId].name;
    delete images[oldId];
    images[newId] = { name, type, data: `data:${type};base64,${bytesToBase64(new Uint8Array(bytes))}` };
    for (const p of allPosts()) p.imageIds = [...new Set(p.imageIds.map((id) => (id === oldId ? newId : id)))];
    swapped.push(oldId);
  }
}
const noOwnImages = allPosts().filter((p) => ownImagesBefore.has(p.id) && !p.imageIds.length).map((p) => p.id.replace(/^siri-/, ''));

const replaced = replaceInPosts(settings, replacements);
for (const c of settings.campaigns) {
  if (footer) c.config.footer = footer;
  if (footerPosition) c.config.footerPosition = footerPosition;
  for (const [k, v] of sets) c.config[k] = v;
}
for (const [k, v] of globals) settings.global[k] = v;

const leadIds = [];
for (const file of leads) {
  const type = MEDIA_TYPES[extname(file).toLowerCase()];
  if (!type) fail(`--lead: unsupported file type: ${file}`);
  const bytes = await readFile(file);
  const id = 'lead-' + createHash('sha1').update(bytes).digest('hex').slice(0, 20);
  images[id] = { name: basename(file), type, data: `data:${type};base64,${bytesToBase64(new Uint8Array(bytes))}` };
  if (!leadIds.includes(id)) leadIds.push(id);
}
for (const c of settings.campaigns) c.leadImageIds = [...leadIds];

settings = migrateSettings(settings); // validate every value
const manifest = JSON.parse(await readFile(resolve(root, 'manifest.json'), 'utf8'));
const backup = buildBackup(settings, images, { includeImages: true, includeToken: false, version: manifest.version });
await mkdir(dirname(out), { recursive: true });
const json = JSON.stringify(backup, null, 2);
await writeFile(out, json);

const sum = summarize(backup.settings, backup.images);
console.log(`Wrote ${out} (${(json.length / 1024 / 1024).toFixed(1)} MB)`);
for (const c of backup.settings.campaigns) {
  const own = c.posts.filter((p) => p.groupUrls.length).length;
  console.log(`  - ${c.name}: ${c.groups.length} groups, ${c.posts.length} posts (${own} group-only, ${c.posts.length - own} for any group), ${c.enabled ? 'enabled' : 'disabled'}`);
}
console.log(`  images: ${sum.images} (duplicates merged: ${report.duplicateImages}, missing: ${report.missingImages})`);
if (allGroups) console.log(`  every post of the groups campaign usable in every group (${opened} were limited to one group)`);
if (codesToGroups) {
  console.log(`  group codes taken out of ${codesStripped.length} posts (${codesStripped.join(', ') || '-'})`);
  console.log(`    codes: ${[...codeKeys].join(' ')}`);
  console.log(`  group codes next to the group link: ${groupCodes.length}`);
  for (const line of groupCodes) console.log(`    ${line}`);
}
if (sharePagePosts) {
  console.log(`  page posts shared with every group: ${shared}, kept back: ${keptIds.join(', ') || '-'}`);
  console.log(`  group codes taken from the groups' own posts (first line of every post there): ${codedGroups.length}`);
  for (const line of codedGroups) console.log(`    ${line}`);
}
if (groupFiles.length) {
  console.log(`  extra groups added: ${added.on} on, ${added.off} off (have a note), duplicates skipped: ${added.dup}`);
  for (const line of added.recoded) console.log(`    group text replaced: ${line}`);
  for (const line of added.invalid) console.log(`    not a group link, skipped: ${line}`);
}
if (footer) console.log(`  text block (${backup.settings.campaigns[0]?.config.footerPosition}):\n    ${footer.split('\n').join('\n    ')}`);
if (dropFiles.length) {
  console.log(`  images taken out of posts: ${dropped.size}`);
  if (noOwnImages.length) console.log(`    posts left with only the lead media: ${noOwnImages.length} (${noOwnImages.join(', ')})`);
}
if (swapDirs.length) console.log(`  images replaced by edited copies: ${swapped.length}`);
for (const file of leads) console.log(`  lead media (first files of every post): ${file}`);
for (const [a, b] of replacements) console.log(`  replaced "${a}" -> "${b}"`);
if (replacements.length) console.log(`  replacements made: ${replaced}`);
for (const [k, v] of sets) console.log(`  set ${k} = ${v}`);
for (const [k, v] of globals) console.log(`  global ${k} = ${v}`);
