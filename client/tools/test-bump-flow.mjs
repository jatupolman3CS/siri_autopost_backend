// End-to-end test of bumping and of posting to a Facebook PAGE: the real background.js (chrome.* and the Facebook page
// faked, see cloud-harness.mjs) against a running SIRIAUTOPOST.Api with the schedule flow of the web app.
//   TEST_PSQL='psql -h localhost -U postgres mydb -tA -c' node tools/test-bump-flow.mjs [apiUrl]   (default http://localhost:5100)
// A bump is due hours after its post, so the test moves the due time of the bump in the database with TEST_PSQL (a psql
// command line that takes the SQL as its last argument); without it the test only covers the part that needs no clock.
// Signs up a throwaway user on that API; it stays there with its workspace.
import assert from 'node:assert/strict';
import { execSync } from 'node:child_process';

import { API, ADMIN, data, page, tabUrls, bg, api, setToken, step, sleep } from './cloud-harness.mjs';

const PSQL = process.env.TEST_PSQL || '';
const sql = (text) => execSync(`${PSQL} ${JSON.stringify(text)}`, { encoding: 'utf8' }).trim();

const GROUP_URL = 'https://www.facebook.com/groups/webonly';
const PAGE_URL = 'https://www.facebook.com/baandee.shop';
const POST_URL = 'https://www.facebook.com/groups/webonly/posts/987654321/';

step('owner signs up and the admin grants the top plan (bumping is a Premium feature)');
const email = `bump${Date.now()}@test.co`;
let r = await api('POST', '/api/auth/signup', { email, password: 'password123' });
assert.equal(r.status, 200, r.text);
const ownerToken = r.json.token;
const ownerId = r.json.user.id;
r = await api('POST', '/api/auth/login', ADMIN);
assert.equal(r.status, 200, `admin login: ${r.text}`);
setToken(r.json.token);
r = await api('PUT', `/api/admin/customers/${ownerId}/plan`, { plan: 'agency' });
assert.equal(r.status, 200, r.text);
setToken(ownerToken);
const ws = (await api('GET', '/api/workspaces')).json[0];
assert.equal(ws.bump, true);

step('the extension is paired; typing and browsing off, a one-minute gap');
const code = (await api('POST', `/api/workspaces/${ws.id}/devices/pairing`)).json.code;
r = await bg('cloudPair', { apiUrl: API, code, name: 'คอมทดสอบ' });
assert.equal(r.ok, true, r.error);
const device = (await api('GET', `/api/workspaces/${ws.id}/devices`)).json[0];
const engine = (await api('GET', `/api/workspaces/${ws.id}/engine`)).json;
r = await api('PUT', `/api/workspaces/${ws.id}/engine/anti-ban`, { ...engine.antiBan, min: 1, max: 2, typing: false, scroll: false, advanced: { ...engine.antiBan.advanced, minGap: 0 } });
assert.equal(r.status, 200, r.text);

step('a collection, a link set with a group, a library image and a schedule that bumps one hour after posting');
r = await api('POST', `/api/workspaces/${ws.id}/collections`, { name: 'ชุดทดสอบ' });
const collection = r.json;
r = await api('POST', `/api/workspaces/${ws.id}/collections/${collection.id}/posts`, { text: 'ขายของ {{code}}', mediaIds: [] });
assert.equal(r.status, 200, r.text);
r = await api('POST', `/api/workspaces/${ws.id}/link-sets`, { name: 'ชุดลิงก์' });
const set = r.json;
r = await api('POST', `/api/workspaces/${ws.id}/link-sets/${set.id}/links`, { name: 'กลุ่มจากเว็บ', url: GROUP_URL, code: 'A1' });
assert.equal(r.status, 200, r.text);
const form = new FormData();
form.append('file', new Blob([Buffer.from([0xff, 0xd8, 0xff, 0xe0, 1, 2, 3])], { type: 'image/jpeg' }), 'poster.jpg');
r = await api('POST', `/api/workspaces/${ws.id}/media`, undefined, { form });
assert.equal(r.status, 200, r.text);
const image = r.json;
r = await api('POST', `/api/workspaces/${ws.id}/schedules`, {
  collectionId: collection.id,
  linkSetId: set.id,
  mode: 'daily',
  times: ['09:00'],
  startNow: true,
  utcOffsetMinutes: 420,
  order: 'shuffle',
  bumpHours: 1,
  bump: { rounds: 1, text: '{ดันค่ะ|ดันค่ะ}', mediaIds: [image.id], imagesEach: 1 },
});
assert.equal(r.status, 200, r.text);
assert.equal(r.json.schedule.bumpHours, 1);
await sleep(25000); // the opening round starts 2-20 s after it is made

step('the post goes out and the extension tells the web app where it is on Facebook');
page.postUrl = POST_URL;
async function takeJobs() {
  r = await api('POST', `/api/workspaces/${ws.id}/devices/${device.id}/commands`, { cmd: 'takeJobs' });
  assert.equal(r.status, 200, r.text);
  const cmd = r.json;
  let done;
  for (let i = 0; i < 40; i++) {
    await bg('cloudConfig', { mode: 'auto' });
    done = (await api('GET', `/api/workspaces/${ws.id}/devices/${device.id}/commands/${cmd.id}`)).json;
    if (done.status === 'done') break;
    await sleep(250);
  }
  assert.equal(done.status, 'done');
  await sleep(1500); // the job itself runs in the post queue after the command is answered
}
await takeJobs();
assert.equal(page.posted.length, 1);
assert.equal(page.posted[0].text, 'ขายของ A1');

if (!PSQL) {
  console.log('\n(TEST_PSQL is not set: the bump itself is not covered)\n');
  process.exit(0);
}

step('the web app queued the bump (round 1, one image) for the address the extension reported');
const rows = sql(`select url, round, status, array_length(media_ids, 1) from "POST_BUMPS" where workspace_id = '${ws.id}'`);
assert.equal(rows, `${POST_URL}|1|Queued|1`);

step('an hour later (the due time is moved) the same browser comments on it');
sql(`update "POST_BUMPS" set due_at = now() - interval '1 minute' where workspace_id = '${ws.id}'`);
await sleep(65000); // the anti-ban pause after the post
await takeJobs();
assert.equal(page.commented.length, 1, 'a comment was made');
assert.equal(page.commented[0].text, 'ดันค่ะ');
assert.deepEqual(page.commented[0].files, ['poster']); // the library names a file without its extension
assert.equal(page.commented[0].url, POST_URL, 'the post was opened');
assert.equal(sql(`select status from "POST_BUMPS" where workspace_id = '${ws.id}'`), 'Done');
assert.equal(page.posted.length, 1, 'a bump is a comment, not a post');

step('a Facebook PAGE: the extension acts as the page first, then posts');
await sleep(65000); // the pause after the comment
page.canSwitch = true;
r = await api('POST', `/api/workspaces/${ws.id}/test-post/manual`, { url: PAGE_URL, text: 'โพสต์ของเพจ', mediaIds: [], deviceId: device.id });
assert.equal(r.status, 200, r.text);
assert.equal(r.json.targetUrl, PAGE_URL);
await takeJobs();
assert.equal(page.switched, 1, 'acted as the page');
assert.equal(page.posted.length, 2);
assert.equal(page.posted[1].text, 'โพสต์ของเพจ');
assert.ok(tabUrls.includes(PAGE_URL), 'the page address was opened');

console.log('\nผ่านทุกข้อ ✔');
process.exit(0);
