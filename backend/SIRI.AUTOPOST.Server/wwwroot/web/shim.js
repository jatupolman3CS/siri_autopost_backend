// Runs the extension's dashboard in a normal web page: chrome.storage.local,
// chrome.runtime.sendMessage and chrome.permissions are replaced by calls to
// this server. Settings and images belong to one config (?profile=<id>);
// state, logs and commands belong to one computer (?device=<id>).
(() => {
  const qs = new URLSearchParams(location.search);
  const profileId = qs.get('profile') || '';
  let deviceId = qs.get('device') || '';

  const LIVE_MS = 4000;
  const COMMAND_WAIT_MS = 75000;

  class ApiError extends Error {
    constructor(status, message, data) {
      super(message);
      this.status = status;
      this.data = data;
    }
  }

  async function api(method, url, body) {
    const res = await fetch(url, {
      method,
      credentials: 'same-origin',
      headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (res.status === 401) {
      location.href = '/login.html?next=' + encodeURIComponent(location.pathname + location.search);
      throw new ApiError(401, 'ต้องเข้าสู่ระบบใหม่');
    }
    let data = null;
    try {
      data = await res.json();
    } catch {
      /* empty body */
    }
    if (!res.ok) throw new ApiError(res.status, (data && data.error) || `HTTP ${res.status}`, data);
    return data;
  }

  // ---------- bar on top of the dashboard ----------

  const bar = document.createElement('div');
  bar.className = 'web-bar';
  bar.innerHTML = `
    <a class="web-back" href="/">← จัดการ config / เครื่อง</a>
    <span class="web-title">config: <b class="web-profile">...</b></span>
    <label class="web-device">เครื่อง
      <select class="web-device-select"><option value="">— ไม่เลือก —</option></select>
    </label>
    <span class="web-dev-status"></span>
    <span class="web-msg" hidden></span>`;
  const elProfile = bar.querySelector('.web-profile');
  const elSelect = bar.querySelector('.web-device-select');
  const elDevStatus = bar.querySelector('.web-dev-status');
  const elMsg = bar.querySelector('.web-msg');

  function showMsg(html, kind = 'warn') {
    elMsg.className = `web-msg ${kind}`;
    elMsg.innerHTML = html;
    elMsg.hidden = false;
  }

  function mountBar() {
    document.body.prepend(bar);
    document.body.classList.add('web-mode');
  }
  if (document.body) mountBar();
  else document.addEventListener('DOMContentLoaded', mountBar);

  elSelect.addEventListener('change', () => {
    const p = new URLSearchParams(location.search);
    if (elSelect.value) p.set('device', elSelect.value);
    else p.delete('device');
    location.search = p.toString();
  });

  // ---------- data ----------

  const store = { settings: undefined, state: {}, logs: [] };
  let revision = 0;
  let lastLogId = 0;
  let lastStateAt = null;
  const imageIds = new Set();
  const imgCache = new Map(); // id -> { name, type, data }
  const listeners = [];
  let staleShown = false;
  let saving = 0; // settings saves in flight
  let saveChain = Promise.resolve();

  function emit(changes) {
    for (const fn of listeners) {
      try {
        fn(changes, 'local');
      } catch (e) {
        console.error(e);
      }
    }
  }

  function fmtAgo(iso) {
    if (!iso) return 'ยังไม่เคยเชื่อมต่อ';
    const s = Math.max(0, Math.round((Date.now() - Date.parse(iso)) / 1000));
    if (s < 60) return `${s} วินาทีที่แล้ว`;
    if (s < 3600) return `${Math.round(s / 60)} นาทีที่แล้ว`;
    if (s < 86400) return `${Math.round(s / 3600)} ชม.ที่แล้ว`;
    return new Date(iso).toLocaleString('th-TH');
  }

  function renderDevice(dev) {
    if (!dev) {
      elDevStatus.className = 'web-dev-status';
      elDevStatus.textContent = 'ยังไม่ได้เลือกเครื่อง: แก้ config ได้ แต่สั่งเริ่ม/หยุด และดูสถานะไม่ได้';
      return;
    }
    elDevStatus.className = 'web-dev-status ' + (dev.online ? 'on' : 'off');
    elDevStatus.textContent =
      `${dev.online ? '● ออนไลน์' : '○ ออฟไลน์'} · ติดต่อล่าสุด ${fmtAgo(dev.lastSeenAt)}` +
      (dev.version ? ` · v${dev.version}` : '') +
      (dev.profileId !== profileId ? ' · ⚠ เครื่องนี้ใช้ config อื่นอยู่' : '');
  }

  function remoteChanged() {
    if (staleShown) return;
    staleShown = true;
    showMsg('config นี้ถูกแก้จากที่อื่น (เครื่องรันหรือหน้าเว็บอื่น) <button class="btn small" type="button">โหลดใหม่</button>');
    elMsg.querySelector('button').addEventListener('click', () => location.reload());
  }

  async function pollLive(first = false) {
    if (deviceId) {
      const live = await api('GET', `/api/devices/${deviceId}/live?afterLog=${first ? 0 : lastLogId}`);
      renderDevice(live.device);
      if (live.stateAt !== lastStateAt) {
        lastStateAt = live.stateAt;
        store.state = live.state || {};
        if (!first) emit({ state: { newValue: store.state } });
      }
      if (live.logs.length) {
        lastLogId = live.logs[live.logs.length - 1].id;
        store.logs = [...store.logs, ...live.logs.map(({ t, level, msg }) => ({ t, level, msg }))].slice(-400);
        if (!first) emit({ logs: { newValue: store.logs } });
      }
      if (live.device.profileId === profileId && live.profileRevision > revision && !saving) remoteChanged();
    } else if (!first) {
      const r = await api('GET', `/api/profiles/${profileId}/revision`);
      if (r.revision > revision && !saving) remoteChanged();
    }
  }

  async function loadDevices() {
    const devices = await api('GET', '/api/devices');
    for (const d of devices) {
      const o = document.createElement('option');
      o.value = d.id;
      o.textContent = `${d.name}${d.profileId === profileId ? '' : d.profileName ? ` (ใช้ ${d.profileName})` : ' (ยังไม่เลือก config)'}`;
      elSelect.append(o);
    }
    // No device chosen: pick the only computer that uses this config.
    if (!deviceId) {
      const mine = devices.filter((d) => d.profileId === profileId);
      if (mine.length === 1) {
        deviceId = mine[0].id;
        const p = new URLSearchParams(location.search);
        p.set('device', deviceId);
        history.replaceState(null, '', '?' + p.toString());
      }
    }
    if (deviceId && !devices.some((d) => d.id === deviceId)) deviceId = '';
    elSelect.value = deviceId;
  }

  const ready = (async () => {
    if (!profileId) {
      showMsg('ไม่ได้ระบุ config <a href="/">กลับไปเลือก</a>', 'bad');
      throw new Error('no profile');
    }
    const s = await api('GET', `/api/profiles/${profileId}/settings`);
    revision = s.revision;
    store.settings = s.settings ?? undefined;
    elProfile.textContent = s.name;
    document.title = `${s.name} · FB AutoPost`;
    for (const id of await api('GET', `/api/profiles/${profileId}/images`)) imageIds.add(id);
    await loadDevices();
    await pollLive(true);
    if (!deviceId) renderDevice(null);
    setInterval(() => pollLive().catch((e) => console.warn('[shim] live', e)), LIVE_MS);
  })();
  ready.catch((e) => {
    console.error(e);
    if (e.status !== 401 && e.message !== 'no profile') showMsg(`โหลด config ไม่สำเร็จ: ${e.message}`, 'bad');
  });

  // Settings saves run one at a time so baseRevision is always current.
  function saveSettings(settings) {
    saving++;
    const p = saveChain.then(async () => {
      try {
        const r = await api('PUT', `/api/profiles/${profileId}/settings`, { settings, baseRevision: revision });
        revision = r.revision;
        store.settings = settings;
      } catch (e) {
        if (e.status === 409) {
          alert('config นี้ถูกแก้จากที่อื่นระหว่างที่คุณแก้อยู่\nจะโหลดของล่าสุดใหม่ (การแก้ล่าสุดของหน้านี้ยังไม่ได้บันทึก)');
          location.reload();
        } else {
          showMsg(`บันทึกไม่สำเร็จ: ${e.message}`, 'bad');
        }
        throw e;
      } finally {
        saving--;
      }
    });
    saveChain = p.catch(() => {});
    return p;
  }

  function keyList(keys) {
    if (keys == null) return null;
    if (typeof keys === 'string') return [keys];
    if (Array.isArray(keys)) return keys;
    return Object.keys(keys);
  }

  async function fetchImages(ids) {
    const want = ids.filter((id) => imageIds.has(id) && !imgCache.has(id));
    for (let i = 0; i < want.length; i += 20) {
      const batch = want.slice(i, i + 20);
      const got = await api('POST', `/api/profiles/${profileId}/images/get`, { ids: batch });
      for (const [id, rec] of Object.entries(got)) imgCache.set(id, rec);
    }
  }

  const local = {
    async get(keys) {
      await ready;
      const list = keyList(keys) || ['settings', 'state', 'logs', ...[...imageIds].map((id) => 'img:' + id)];
      const imgs = list.filter((k) => k.startsWith('img:')).map((k) => k.slice(4));
      if (imgs.length) await fetchImages(imgs);
      const out = {};
      for (const k of list) {
        let v;
        if (k.startsWith('img:')) v = imgCache.get(k.slice(4));
        else if (k in store) v = store[k];
        if (v !== undefined) out[k] = JSON.parse(JSON.stringify(v));
        else if (keys && typeof keys === 'object' && !Array.isArray(keys) && k in keys) out[k] = keys[k];
      }
      return out;
    },

    async set(items) {
      await ready;
      for (const [k, v] of Object.entries(items)) {
        if (!k.startsWith('img:')) continue;
        const id = k.slice(4);
        await api('PUT', `/api/profiles/${profileId}/images/${encodeURIComponent(id)}`, v);
        imageIds.add(id);
        imgCache.set(id, v);
      }
      if ('settings' in items) await saveSettings(JSON.parse(JSON.stringify(items.settings)));
      if ('logs' in items && Array.isArray(items.logs) && !items.logs.length) {
        store.logs = [];
        if (deviceId) {
          await api('DELETE', `/api/devices/${deviceId}/logs`);
          sendCommand('clearLogs', {}).catch(() => {});
        }
        emit({ logs: { newValue: [] } });
      }
    },

    async remove(keys) {
      await ready;
      const ids = keyList(keys).filter((k) => k.startsWith('img:')).map((k) => k.slice(4));
      if (!ids.length) return;
      await api('POST', `/api/profiles/${profileId}/images/delete`, { ids });
      for (const id of ids) {
        imageIds.delete(id);
        imgCache.delete(id);
      }
    },

    async getKeys() {
      await ready;
      return ['settings', 'state', 'logs', ...[...imageIds].map((id) => 'img:' + id)];
    },
  };

  // ---------- commands to the computer ----------

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  async function sendCommand(cmd, args) {
    const { id } = await api('POST', `/api/devices/${deviceId}/commands`, { cmd, args });
    const until = Date.now() + COMMAND_WAIT_MS;
    while (Date.now() < until) {
      await sleep(1500);
      const c = await api('GET', `/api/commands/${id}`);
      if (c.status === 'done') {
        pollLive().catch(() => {});
        return c.result || { ok: true };
      }
      if (c.status === 'expired') return { ok: false, error: 'เครื่องไม่ได้รับคำสั่ง (ออฟไลน์นานเกินไป)' };
    }
    return { ok: true, queued: true };
  }

  const LOCAL_ONLY = { loadBundledConfig: 'ใช้ได้เฉพาะในส่วนขยาย ใช้ "อัปโหลดการตั้งค่า" แทน' };

  async function sendMessage(msg) {
    if (!msg || msg.target !== 'fbap-bg') return undefined;
    const { target, cmd, ...args } = msg;
    if (LOCAL_ONLY[cmd]) return { ok: false, error: LOCAL_ONLY[cmd] };
    if (!deviceId) return { ok: false, error: 'เลือก "เครื่องที่สั่งงาน" ด้านบนก่อน' };
    showMsg(`ส่งคำสั่งไปที่เครื่องแล้ว รอเครื่องรับ (ไม่เกิน 1 นาที)...`, 'info');
    try {
      const r = await sendCommand(cmd, args);
      if (r.queued) showMsg('เครื่องยังไม่ตอบ คำสั่งจะทำงานเมื่อเครื่องออนไลน์ (ภายใน 10 นาที)', 'warn');
      else elMsg.hidden = true;
      return r;
    } catch (e) {
      showMsg(`ส่งคำสั่งไม่สำเร็จ: ${e.message}`, 'bad');
      return { ok: false, error: e.message };
    }
  }

  window.chrome = {
    storage: {
      local,
      onChanged: { addListener: (fn) => listeners.push(fn), removeListener: () => {} },
    },
    runtime: {
      sendMessage,
      getManifest: () => ({ version: 'web' }),
      getURL: (p) => '/app/' + String(p).replace(/^\/+/, ''),
    },
    // Screenshot permission is granted on the computer itself.
    permissions: { contains: async () => true, request: async () => true },
  };
})();
