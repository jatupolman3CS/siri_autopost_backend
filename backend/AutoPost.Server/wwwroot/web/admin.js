// Admin home: configs (profiles) and computers (devices).
const $ = (sel, root = document) => root.querySelector(sel);

let profiles = [];
let devices = [];

async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (res.status === 401) {
    location.href = '/login.html?next=' + encodeURIComponent(location.pathname);
    throw new Error('ต้องเข้าสู่ระบบ');
  }
  const data = await res.json().catch(() => null);
  if (!res.ok) throw new Error((data && data.error) || `HTTP ${res.status}`);
  return data;
}

let toastTimer = null;
function toast(msg, ms = 3500) {
  const el = $('#toast');
  el.textContent = msg;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (el.hidden = true), ms);
}

// Runs fn and shows its error as a toast.
const guard = (fn) => async (...a) => {
  try {
    await fn(...a);
  } catch (e) {
    toast(e.message || String(e), 5000);
  }
};

const fmtTime = (iso) => (iso ? new Date(iso).toLocaleString('th-TH', { dateStyle: 'medium', timeStyle: 'short' }) : '-');
const fmtMB = (b) => (b >= 1048576 ? `${(b / 1048576).toFixed(1)} MB` : `${Math.round(b / 1024)} KB`);

function fmtAgo(iso) {
  if (!iso) return 'ยังไม่เคยเชื่อมต่อ';
  const s = Math.max(0, Math.round((Date.now() - Date.parse(iso)) / 1000));
  if (s < 60) return `${s} วินาทีที่แล้ว`;
  if (s < 3600) return `${Math.round(s / 60)} นาทีที่แล้ว`;
  if (s < 86400) return `${Math.round(s / 3600)} ชม.ที่แล้ว`;
  return fmtTime(iso);
}

function el(tag, props = {}, ...children) {
  const e = document.createElement(tag);
  Object.assign(e, props);
  e.append(...children.filter((c) => c != null));
  return e;
}

function button(text, cls, onClick) {
  const b = el('button', { type: 'button', className: `btn small ${cls || ''}`, textContent: text });
  b.addEventListener('click', guard(onClick));
  return b;
}

const editorUrl = (profileId, deviceId) =>
  `/app/dashboard.html?profile=${profileId}${deviceId ? `&device=${deviceId}` : ''}`;

// ---------- configs ----------

function renderProfiles() {
  const box = $('#profiles');
  box.textContent = '';
  if (!profiles.length) box.append(el('p', { className: 'empty-list', textContent: 'ยังไม่มี config สร้างใหม่ด้านล่าง' }));
  for (const p of profiles) {
    const meta =
      `${p.campaigns} ชุด · ${p.groups} กลุ่ม · ${p.posts} แบบโพสต์ · ${p.images} รูป (${fmtMB(p.imageBytes)})` +
      ` · ใช้อยู่ ${p.devices} เครื่อง · แก้ล่าสุด ${fmtTime(p.updatedAt)}${p.updatedBy ? ` โดย ${p.updatedBy}` : ''}`;
    box.append(
      el(
        'div',
        { className: 'item' },
        el('div', { className: 'item-name', textContent: p.name }),
        el(
          'div',
          { className: 'item-actions' },
          el('a', { className: 'btn small primary', href: editorUrl(p.id), textContent: 'แก้ไข' }),
          button('เปลี่ยนชื่อ', 'ghost', async () => {
            const name = prompt('ชื่อ config', p.name);
            if (!name || !name.trim()) return;
            await api('PUT', `/api/profiles/${p.id}`, { name: name.trim() });
            await refresh();
          }),
          button('ลบ', 'ghost danger-text', async () => {
            const used = p.devices ? `\nมี ${p.devices} เครื่องใช้อยู่ เครื่องเหล่านั้นจะไม่มี config` : '';
            if (!confirm(`ลบ config "${p.name}" พร้อมรูปทั้งหมด?${used}\nย้อนกลับไม่ได้`)) return;
            await api('DELETE', `/api/profiles/${p.id}`);
            toast('ลบแล้ว');
            await refresh();
          })
        ),
        el('div', { className: 'item-meta', textContent: meta })
      )
    );
  }
  const copy = $('#newProfile [name=copyFrom]');
  const keep = copy.value;
  copy.textContent = '';
  copy.append(el('option', { value: '', textContent: 'เริ่มจากว่าง' }));
  for (const p of profiles) copy.append(el('option', { value: p.id, textContent: `คัดลอกจาก ${p.name}` }));
  copy.value = profiles.some((p) => p.id === keep) ? keep : '';

  const sel = $('#newDevice [name=profileId]');
  const keepSel = sel.value;
  sel.textContent = '';
  sel.append(el('option', { value: '', textContent: '— ยังไม่เลือก config —' }));
  for (const p of profiles) sel.append(el('option', { value: p.id, textContent: p.name }));
  sel.value = profiles.some((p) => p.id === keepSel) ? keepSel : profiles[0]?.id || '';
}

$('#newProfile').addEventListener(
  'submit',
  guard(async (ev) => {
    ev.preventDefault();
    const f = ev.target;
    const { id } = await api('POST', '/api/profiles', { name: f.elements['name'].value.trim(), copyFrom: f.elements['copyFrom'].value || null });
    f.reset();
    toast('สร้าง config แล้ว');
    await refresh();
    if (confirm('เปิดหน้าแก้ไข config นี้เลยไหม?')) location.href = editorUrl(id);
  })
);

// ---------- devices ----------

function showKey(device, key) {
  $('#keyDevice').textContent = device;
  $('#keyServer').textContent = location.origin;
  $('#keyValue').textContent = key;
  $('#keyBox').hidden = false;
  $('#keyBox').scrollIntoView({ behavior: 'smooth', block: 'center' });
}

$('#keyClose').addEventListener('click', () => {
  $('#keyBox').hidden = true;
  $('#keyValue').textContent = '';
});

for (const b of document.querySelectorAll('[data-copy]')) {
  b.addEventListener(
    'click',
    guard(async () => {
      await navigator.clipboard.writeText($('#' + b.dataset.copy).textContent);
      toast('คัดลอกแล้ว');
    })
  );
}

function renderDevices() {
  const box = $('#devices');
  box.textContent = '';
  if (!devices.length) box.append(el('p', { className: 'empty-list', textContent: 'ยังไม่มีเครื่อง เพิ่มเครื่องด้านล่าง' }));
  for (const d of devices) {
    const sel = el('select', { title: 'config ที่เครื่องนี้ใช้' });
    sel.append(el('option', { value: '', textContent: '— ยังไม่เลือก config —' }));
    for (const p of profiles) sel.append(el('option', { value: p.id, textContent: p.name }));
    sel.value = d.profileId || '';
    sel.addEventListener(
      'change',
      guard(async () => {
        const name = profiles.find((p) => p.id === sel.value)?.name;
        if (sel.value && !confirm(`ให้เครื่อง "${d.name}" ใช้ config "${name}"?\nเครื่องจะโหลด config นี้แทนของเดิมในรอบซิงก์ถัดไป`)) {
          sel.value = d.profileId || '';
          return;
        }
        await api('PUT', `/api/devices/${d.id}`, { name: d.name, profileId: sel.value || null });
        toast('เปลี่ยน config ของเครื่องแล้ว');
        await refresh();
      })
    );
    const status = el(
      'span',
      {},
      el('span', { className: `dot ${d.online ? 'on' : 'off'}`, textContent: d.online ? '● ออนไลน์' : '○ ออฟไลน์' }),
      d.running ? el('span', { className: 'dot run', textContent: ' · ▶ ทำงานอยู่' }) : null
    );
    const meta = `ติดต่อล่าสุด ${fmtAgo(d.lastSeenAt)}${d.version ? ` · ส่วนขยาย v${d.version}` : ''}`;
    const open = d.profileId
      ? el('a', { className: 'btn small primary', href: editorUrl(d.profileId, d.id), textContent: 'ควบคุม / ดูสถานะ' })
      : null;
    box.append(
      el(
        'div',
        { className: 'item' },
        el('div', { className: 'item-name' }, `${d.name} `, status),
        el(
          'div',
          { className: 'item-actions' },
          sel,
          open,
          button('เปลี่ยนชื่อ', 'ghost', async () => {
            const name = prompt('ชื่อเครื่อง', d.name);
            if (!name || !name.trim()) return;
            await api('PUT', `/api/devices/${d.id}`, { name: name.trim(), profileId: d.profileId });
            await refresh();
          }),
          button('ออกคีย์ใหม่', 'ghost', async () => {
            if (!confirm(`ออกคีย์ใหม่ให้ "${d.name}"?\nคีย์เดิมจะใช้ไม่ได้ทันที ต้องใส่คีย์ใหม่ในเครื่องนั้น`)) return;
            const { key } = await api('POST', `/api/devices/${d.id}/key`);
            showKey(d.name, key);
          }),
          button('ลบ', 'ghost danger-text', async () => {
            if (!confirm(`ลบเครื่อง "${d.name}"?\nเครื่องนั้นจะเชื่อมต่อ server ไม่ได้อีก (การตั้งค่าในเครื่องยังอยู่)`)) return;
            await api('DELETE', `/api/devices/${d.id}`);
            toast('ลบแล้ว');
            await refresh();
          })
        ),
        el('div', { className: 'item-meta', textContent: meta })
      )
    );
  }
}

$('#newDevice').addEventListener(
  'submit',
  guard(async (ev) => {
    ev.preventDefault();
    const f = ev.target;
    const name = f.elements['name'].value.trim();
    const { key } = await api('POST', '/api/devices', { name, profileId: f.elements['profileId'].value || null });
    f.elements['name'].value = '';
    await refresh();
    showKey(name, key);
  })
);

// ---------- account ----------

$('#btnLogout').addEventListener(
  'click',
  guard(async () => {
    await api('POST', '/api/auth/logout');
    location.href = '/login.html';
  })
);

$('#btnPassword').addEventListener(
  'click',
  guard(async () => {
    const current = prompt('รหัสผ่านเดิม');
    if (!current) return;
    const next = prompt('รหัสผ่านใหม่ (อย่างน้อย 8 ตัว)');
    if (!next) return;
    if (prompt('พิมพ์รหัสผ่านใหม่อีกครั้ง') !== next) return toast('รหัสผ่านใหม่ไม่ตรงกัน');
    await api('POST', '/api/auth/password', { current, new: next });
    toast('เปลี่ยนรหัสผ่านแล้ว');
  })
);

// ---------- init ----------

async function refresh() {
  [profiles, devices] = await Promise.all([api('GET', '/api/profiles'), api('GET', '/api/devices')]);
  renderProfiles();
  renderDevices();
}

(async () => {
  const me = await api('GET', '/api/auth/me');
  $('#me').textContent = me.username;
  await refresh();
  // Device status (online / running) changes on its own.
  setInterval(async () => {
    if (document.hidden || document.activeElement?.tagName === 'SELECT') return;
    try {
      devices = await api('GET', '/api/devices');
      renderDevices();
    } catch {
      /* next time */
    }
  }, 10000);
})().catch((e) => toast(e.message, 6000));
