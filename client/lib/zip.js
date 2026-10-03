// Minimal ZIP reader (stored + deflate entries) built on DecompressionStream.
// Works in extension pages and in Node 18+ (falls back to node:zlib there).

const SIG_EOCD = 0x06054b50;
const SIG_CENTRAL = 0x02014b50;
const SIG_LOCAL = 0x04034b50;
const MAX_TOTAL_BYTES = 500 * 1024 * 1024; // refuse zip bombs

const TOO_BIG = 'ไฟล์ใน zip ใหญ่เกินไป';
const BROKEN = 'ไฟล์ zip เสีย';

// Inflates one entry, counting the real output: a crafted entry that
// declares a small size cannot expand past `limit` bytes.
async function inflateRaw(bytes, limit) {
  let stream;
  try {
    stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('deflate-raw'));
  } catch {
    // Node before 20.14 has no 'deflate-raw' DecompressionStream.
    const zlib = await import('node:zlib');
    let out;
    try {
      out = zlib.inflateRawSync(bytes, { maxOutputLength: Math.max(1, limit) });
    } catch (e) {
      throw new Error(e && e.code === 'ERR_BUFFER_TOO_LARGE' ? TOO_BIG : BROKEN);
    }
    return new Uint8Array(out.buffer, out.byteOffset, out.byteLength);
  }
  const reader = stream.getReader();
  const chunks = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.length;
    if (size > limit) {
      await reader.cancel();
      throw new Error(TOO_BIG);
    }
    chunks.push(value);
  }
  const out = new Uint8Array(size);
  let at = 0;
  for (const c of chunks) {
    out.set(c, at);
    at += c.length;
  }
  return out;
}

const THAI_RE = /[฀-๿]/;

function strictUtf8(bytes) {
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return null;
  }
}

function thaiCodePage(bytes) {
  try {
    return new TextDecoder('windows-874').decode(bytes);
  } catch {
    return new TextDecoder().decode(bytes);
  }
}

// Names without the UTF-8 flag: many tools (macOS Finder, 7-Zip) still write
// UTF-8, old Windows zips use the Thai code page. Decide once per archive:
// UTF-8 only when every such name is valid UTF-8 and the result is Thai
// (some code-page Thai words happen to be valid UTF-8, e.g. "รถ" -> "ö").
function pickNameDecoder(unflagged) {
  const decoded = unflagged.map(strictUtf8);
  const utf8 = decoded.every((s) => s !== null) && decoded.some((s) => THAI_RE.test(s));
  return utf8 ? (b) => new TextDecoder().decode(b) : thaiCodePage;
}

// Returns Map(path -> Uint8Array) of all files (folders skipped). Paths always
// use "/" (Windows PowerShell's Compress-Archive writes "\").
export async function readZip(input) {
  const buf = input instanceof Uint8Array ? input : new Uint8Array(input);
  const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  let eocd = -1;
  for (let i = buf.length - 22; i >= 0 && i >= buf.length - 22 - 65535; i--) {
    if (dv.getUint32(i, true) === SIG_EOCD) {
      eocd = i;
      break;
    }
  }
  if (eocd < 0) throw new Error('ไม่ใช่ไฟล์ zip');
  const count = dv.getUint16(eocd + 10, true);
  let p = dv.getUint32(eocd + 16, true);
  if (count === 0xffff || p === 0xffffffff) throw new Error('ไฟล์ zip ใหญ่เกินไป (zip64 ไม่รองรับ)');

  const all = [];
  for (let n = 0; n < count; n++) {
    if (p + 46 > buf.length || dv.getUint32(p, true) !== SIG_CENTRAL) throw new Error(BROKEN);
    const nameLen = dv.getUint16(p + 28, true);
    const extraLen = dv.getUint16(p + 30, true);
    const commentLen = dv.getUint16(p + 32, true);
    all.push({
      flags: dv.getUint16(p + 8, true),
      method: dv.getUint16(p + 10, true),
      csize: dv.getUint32(p + 20, true),
      usize: dv.getUint32(p + 24, true),
      local: dv.getUint32(p + 42, true),
      nameBytes: buf.subarray(p + 46, p + 46 + nameLen),
    });
    p += 46 + nameLen + extraLen + commentLen;
  }
  const isAscii = (b) => b.every((x) => x < 0x80);
  const decodeOther = pickNameDecoder(all.filter((e) => !(e.flags & 0x800) && !isAscii(e.nameBytes)).map((e) => e.nameBytes));

  const entries = [];
  let declared = 0;
  for (const e of all) {
    const raw = e.flags & 0x800 || isAscii(e.nameBytes) ? new TextDecoder().decode(e.nameBytes) : decodeOther(e.nameBytes);
    const name = raw.replace(/\\/g, '/');
    if (name.endsWith('/')) continue;
    if (e.flags & 1) throw new Error('zip ที่ตั้งรหัสผ่านไว้ไม่รองรับ');
    declared += e.usize;
    if (declared > MAX_TOTAL_BYTES) throw new Error(TOO_BIG);
    entries.push({ name, method: e.method, csize: e.csize, usize: e.usize, local: e.local });
  }

  const files = new Map();
  let total = 0;
  for (const e of entries) {
    if (e.local + 30 > buf.length || dv.getUint32(e.local, true) !== SIG_LOCAL) throw new Error(BROKEN);
    const start = e.local + 30 + dv.getUint16(e.local + 26, true) + dv.getUint16(e.local + 28, true);
    if (start + e.csize > buf.length) throw new Error(BROKEN);
    const raw = buf.subarray(start, start + e.csize);
    let data;
    if (e.method === 0) {
      if (e.csize !== e.usize) throw new Error(BROKEN);
      data = raw.slice();
    } else if (e.method === 8) {
      // Real size may not exceed the declared size nor the overall budget.
      data = await inflateRaw(raw, Math.min(e.usize, MAX_TOTAL_BYTES - total));
      if (data.length !== e.usize) throw new Error(BROKEN);
    } else {
      throw new Error(`zip บีบอัดแบบที่ไม่รองรับ (method ${e.method})`);
    }
    total += data.length;
    files.set(e.name, data);
  }
  return files;
}
