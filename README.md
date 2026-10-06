# Siri AutoPost (Backend & Client)

คลังโค้ดนี้รวบรวม **Backend Server** (.NET Core Web API / PostgreSQL) และ **Client** (Google Chrome Extension สำหรับ AutoPost) ไว้อยู่ด้วยกัน

- `src/` + `tests/` + `SIRIAUTOPOST.sln` = API ใหม่แบบ Clean Architecture (.NET 10) ที่หน้าเว็บ Angular (`siri_autopost_ui`) ใช้งาน: สมัคร/เข้าสู่ระบบ (JWT), เวิร์กสเปซ, บัญชีโซเชียล, ตั้งเวลาโพสต์, รายงานข้อผิดพลาด, คลังสื่อ, การตั้งค่า anti-ban และเมื่อออฟไลน์ และเป็น API ที่ส่วนขยายรับงานโพสต์ไปโพสต์จริง (โหมด "เชื่อมต่อเว็บ AutoPost" ดู `client/README.md`)
- `backend/SIRI.AUTOPOST.Server/` = server เดิม (legacy) ที่ส่วนขยายใช้งานอยู่ตอนนี้

---

## โครงสร้างโปรเจกต์

```text
siri_autopost_backend/
├── src/                                 # โครงใหม่ Clean Architecture
│   ├── SIRIAUTOPOST.Domain/             # Entities, Enums, Exceptions, Interfaces, ValueObjects (ไม่อ้างอิงใคร)
│   ├── SIRIAUTOPOST.Application/        # DTOs, Features (CQRS Commands/Queries), Interfaces, Validators
│   ├── SIRIAUTOPOST.Infrastructure/     # EF Core DbContext, Configurations, Migrations, Repositories, Services
│   └── SIRIAUTOPOST.Api/                # Controllers, Middlewares, Extensions, Program.cs
├── tests/
│   ├── SIRIAUTOPOST.Domain.Tests/
│   ├── SIRIAUTOPOST.Application.Tests/
│   └── SIRIAUTOPOST.Api.IntegrationTests/  # ต่อ PostgreSQL จริง (Test DB)
├── SIRIAUTOPOST.sln
│
├── backend/                  # server เดิม (legacy) ASP.NET Core Web API
│   ├── SIRI.AUTOPOST.Server/ # โค้ด Backend API (.NET 10 / C#)
│   └── Dockerfile            # สำหรับรัน Backend ใน Container (build จาก root ของ repo)
│
├── client/                   # Google Chrome Extension (Bot ตัวโพสต์)
│   ├── manifest.json         # Chrome Extension Manifest V3
│   ├── background.js         # Service Worker หลัก คุมจังหวะและคิวโพสต์ + โหมดเชื่อมต่อเว็บ
│   ├── content.js            # Content script จำลองการกระทำบน Facebook
│   ├── status.html / .js     # หน้าเดียวของส่วนขยาย: สถานะ + ปุ่ม "อนุญาต" ตอนจับคู่กับเว็บ
│   ├── dashboard.*           # หน้าตั้งค่าเดิม ใช้เป็นหน้าแก้ไขของ server เดิมเท่านั้น (ในส่วนขยายจะพาไป status.html)
│   ├── lib/                  # ฟังก์ชันตัวช่วย (shared, backup, zip, siri-import)
│   ├── icons/                # ไอคอนของ Extension
│   ├── config/               # ข้อมูลตั้งต้นของ tools/siri-to-config.mjs (ส่วนขยายไม่อ่านตอนทำงาน)
│   └── tools/                # เครื่องมือแปลงข้อมูลและทดสอบ (test-cloud.mjs, test-online.mjs)
│
├── deploy/                   # Kubernetes + Jenkins (ดู deploy/README.md)
├── docker-compose.yml        # รัน server เดิม + PostgreSQL
├── docker-compose.saas.yml   # รัน API ใหม่ + หน้าเว็บ Angular + PostgreSQL
├── .env.example              # ตัวอย่าง Environment
└── README.md
```

---

## 0. โครงใหม่ (SIRIAUTOPOST.sln)

ต้องมี [.NET 10 SDK](https://dotnet.microsoft.com/) และ PostgreSQL

```bash
dotnet build SIRIAUTOPOST.sln
dotnet test SIRIAUTOPOST.sln          # integration test ต้องมี PostgreSQL (ดูด้านล่าง)
cd src/SIRIAUTOPOST.Api && dotnet run # http://localhost:5100  (OpenAPI: /openapi/v1.json)
```

- Connection string ตอนพัฒนาอยู่ที่ `src/SIRIAUTOPOST.Api/appsettings.Development.json` (`localhost:5432` ฐาน `siriautopost`) ระบบรัน migration ให้เองตอนเริ่มใน Development
- **บัญชีผู้ดูแล:** ตอนเริ่มระบบจะสร้างผู้ดูแลแพลตฟอร์มจาก `Admin:Email`/`Admin:Password` (ใน Development คือ `admin@autopost.local` / `admin1234`) ถ้าไม่ตั้งค่าไว้จะไม่สร้าง ถ้าอีเมลใน `Admin:Email` มีบัญชีลูกค้าอยู่แล้ว ระบบจะเลื่อนบัญชีนั้นเป็นผู้ดูแลและตั้งแผนสูงสุด (Agency/Premium) ตอนเริ่มระบบครั้งถัดไป (รหัสผ่านคงเดิม ไม่ต้องตั้ง `Admin:Password` ส่วนแผนที่จ่ายผ่าน Stripe ไม่แตะ) แล้วให้เข้าสู่ระบบใหม่
- **JWT:** ตั้ง `Jwt:Key` (อย่างน้อย 32 ตัวอักษร) ทุก environment นอก Development ไม่อย่างนั้นระบบจะไม่ยอมเริ่ม เช่น `Jwt__Key=...`
- เวิร์กสเปซใหม่เริ่มต้น **ว่าง** ไม่มีบัญชีหรือข้อความตัวอย่าง (เดิมมีบัญชีโซเชียล 7 บัญชีจากดีไซน์ ซึ่งเป็นข้อมูลปลอม ถูกตัดออกแล้ว) ระบบรองรับ **กลุ่มและเพจ Facebook** เท่านั้นในตอนนี้ แพลตฟอร์มอื่น (Instagram, X, TikTok, LINE, Threads) จะทำใน phase ถัดไป บัญชีจริงได้มาจากการจับคู่ส่วนขยายแต่ละเครื่อง (ชื่อส่วนขยายในเวิร์กสเปซเดียวกันห้ามซ้ำ)
- **แพ็กเกจ**: นอกจากจำนวนบัญชี โพสต์ต่อวัน อุปกรณ์ และที่นั่งแล้ว แต่ละแพ็กเกจจำกัดจำนวน **กลุ่ม/เพจ คลังรูป และคลังโพสต์** และกำหนดฟังก์ชันที่ใช้ได้: Pro = anti-ban ขั้นสูง แจ้งเตือน ตอบกลับอัตโนมัติ **AI ร่างโพสต์**; Premium (key `agency`) = ทั้งหมด + **ดันโพสต์** + รายงานลูกค้า (ดู CLAUDE.md หัวข้อ Packages)
- **AI ร่างโพสต์** ใช้คีย์ของแพลตฟอร์ม (`Ai__ApiKey`) ยังไม่ใส่คีย์ ปุ่ม AI ในหน้าเขียนโพสต์จะถูกปิดและบอกเหตุผล
- **ดันโพสต์** (Premium): หลังโพสต์ลงกลุ่มไปแล้ว N ชั่วโมง ส่วนขยายจะเปิดลิงก์โพสต์นั้นแล้วคอมเมนต์ (ข้อความ/รูปจากคลัง ตามที่ตั้งในตารางโพสต์) เพื่อดันโพสต์ขึ้นมา
- Integration test ใช้ฐาน `siriautopost_test` (ลบแล้วสร้างใหม่ทุกครั้ง) เปลี่ยนได้ด้วย environment variable `SIRIAUTOPOST_TEST_DB`
- เพิ่ม migration: `dotnet ef migrations add <ชื่อ> -p src/SIRIAUTOPOST.Infrastructure -s src/SIRIAUTOPOST.Api -o Data/Migrations`
- เพิ่มฟีเจอร์ใหม่: Entity ใน Domain → Command/Query + Handler ใน `Application/Features/<ฟีเจอร์>/<ฟีเจอร์>.cs` → ลงทะเบียนใน `Application/DependencyInjection.cs` → Repository + Configuration ใน Infrastructure → Controller ใน Api

| กลุ่ม | Endpoint |
|---|---|
| Auth | `POST /api/auth/signup`, `POST /api/auth/login`, `GET /api/auth/config` (`googleClientId`), `POST /api/auth/google`, `GET /api/auth/me` |
| แผนและบิล | `GET /api/plans` (ไม่ต้องเข้าสู่ระบบ), `GET /api/billing` (แผน รอบบิล บัตร และการใช้งานเทียบกับขีดจำกัด), `GET /api/billing/invoices`, `PUT /api/billing/plan` (`plan`, `cycle`, `promoCode`; แผนเสียเงินครั้งแรกตอบ `checkoutUrl` ของ Stripe), `POST /api/billing/checkout/confirm` (`sessionId`), `POST /api/billing/portal` |
| Stripe | `POST /api/webhooks/stripe` (ไม่ต้องเข้าสู่ระบบ ตรวจลายเซ็น `Stripe-Signature`) |
| ทีม | `GET/POST /api/workspaces/{ws}/members`, `PUT/DELETE .../members/{memberId}` |
| เจ้าของแพลตฟอร์ม (`role=admin`) | `GET /api/admin/summary\|health\|audit`, `GET /api/admin/customers`, `GET /api/admin/jobs?customerId&take`, `POST /api/admin/customers/{id}/status\|pause\|retry-failed\|refund\|impersonate`, `PUT .../customers/{id}/plan\|limits\|note`, `DELETE .../customers/{id}/devices/{deviceId}`, `GET /api/admin/transactions`, `POST .../transactions/{id}/refund\|retry`, `PUT /api/admin/plans/{key}`, `GET/POST /api/admin/promos`, `PUT .../promos/{code}/active` |
| เวิร์กสเปซ | `GET/POST /api/workspaces`, `GET /api/workspaces/{ws}/accounts`, `POST .../accounts/{id}/reconnect` |
| โพสต์ | `GET .../posts?from&to`, `POST .../posts/schedule`, `DELETE .../posts/{id}`, `POST .../posts/{id}/retry`, `POST .../posts/retry` (ลองใหม่เป็นชุด ≤500 โพสต์), `POST .../posts/{id}/run-now` (โพสต์เดี๋ยวนี้ ลัดคิว: ย้ายโพสต์จากรอบเดิมมาเป็นตอนนี้ ใช้รันโพสต์ที่ล้มเหลวซ้ำทันทีด้วย), `POST .../posts/{id}/dismiss`, `GET .../errors` |
| คลัง | `GET/POST .../media` (multipart field `file`), `GET .../media/{id}/content`, `GET/POST .../snippets` |
| ระบบโพสต์ | `GET .../engine`, `PUT .../engine/anti-ban`, `PUT .../engine/offline`, `POST .../engine/extension`, `POST .../engine/waiting/skip` |
| อุปกรณ์ (เจ้าของ) | `GET .../devices`, `POST .../devices/pairing` (รหัสจับคู่ 10 นาที), `PUT .../devices/{id}` (`name`, `jobsPaused`), `DELETE .../devices/{id}` |
| ชุดโพสต์ของส่วนขยาย | `GET/PUT .../devices/{id}/config` (`baseRevision` ไม่ตรง = 409), `GET/PUT .../extension-images/{imageId}`, `GET .../devices/{id}/live`, `POST .../devices/{id}/commands`, `GET .../devices/{id}/commands/{commandId}`, `DELETE .../devices/{id}/logs` |
| เหตุการณ์สด | `GET .../events/stream` (Server-Sent Events, `?after=` / `Last-Event-ID` เล่นย้อนสิ่งที่พลาด), `GET .../events?after=&take=` |
| ส่วนขยาย (`X-Device-Key`) | `POST /api/device/pair`, `POST /api/device/heartbeat`, `PUT /api/device/groups`, `POST /api/device/jobs/claim` (204 = ไม่มีงาน), `POST /api/device/jobs/{id}/result`, `GET /api/device/media/{id}`, `POST /api/device/sync` (สถานะ + log + คำสั่งจากเว็บ; `wait: true` ค้างสายได้ถึง 25 วินาที), `GET/PUT /api/device/config`, `POST /api/device/images/missing`, `GET/PUT /api/device/images/{id}`, `POST /api/device/commands/{id}/result` |
| ดาวน์โหลดส่วนขยาย | `GET /api/extension/download` (ไม่ต้องเข้าสู่ระบบ, zip ของ `client/`) |

ทุก endpoint ต้องส่ง `Authorization: Bearer <token>` ยกเว้น signup, login, `GET /api/auth/config`, `POST /api/auth/google`, `GET /api/plans`, `POST /api/webhooks/stripe`, `GET /api/extension/download`, `/healthz` และ `/api/device/*` (ใช้หัว `X-Device-Key` จากการจับคู่ ยกเว้น `pair`)

- **แผนและข้อจำกัด:** ราคาและข้อจำกัดของแต่ละแผน (บัญชี, โพสต์ต่อวัน, อุปกรณ์, ที่นั่งทีม) อยู่ในตาราง `PLAN_SETTINGS` แก้ได้จากหน้าแอดมิน และแอดมินตั้งค่าเฉพาะลูกค้ารายคนทับได้ ข้อจำกัดนับตามแผนของ **เจ้าของเวิร์กสเปซ** เสมอ (`WorkspaceDto.limits` ให้หน้าเว็บใช้แสดง)
- **ชำระเงินด้วย Stripe:** ดูหัวข้อด้านล่าง ทุกการเปลี่ยนแผนแบบเสียเงินผ่าน Stripe เท่านั้น
- **ทีม:** เชิญด้วยอีเมล (ถ้ายังไม่มีบัญชี จะเข้าทีมให้เองตอนสมัคร) บทบาทในเวิร์กสเปซ: ผู้ชม (ดูอย่างเดียว) → ผู้แก้ไข (โพสต์, คลังสื่อ) → ผู้ดูแล (ตั้งค่า anti-ban/ออฟไลน์, อุปกรณ์, สมาชิก) → เจ้าของ จำนวนที่นั่ง (รวมเจ้าของ) มาจากแผนของเจ้าของ
- **สถานะลูกค้า:** แอดมินระงับ/แบนได้ (เข้าสู่ระบบไม่ได้ ได้ 403 และ token เดิมใช้ไม่ได้ทันที) หยุดงานโพสต์ทั้งหมดของลูกค้า หรือคืนเงินรายการล่าสุด
- **โหมดช่วยเหลือ:** แอดมินเปิดแดชบอร์ดของลูกค้าได้ 1 ชั่วโมงแบบ **ดูอย่างเดียว** (`POST /api/admin/customers/{id}/impersonate`) คำขอที่แก้ข้อมูลทุกอย่างจะได้ 403
- **ประวัติการดำเนินการ:** ทุกการกระทำของแอดมินและการเปลี่ยนแผนของลูกค้าถูกบันทึกใน `AUDIT_ENTRIES` (`GET /api/admin/audit?customerId=`) และใช้คำนวณ MRR ย้อนหลังและ churn
- **สถานะระบบ:** `GET /api/admin/health` ให้ตัวเลขจริง: MRR เทียบ 30 วันก่อน, churn, ส่วนขยายที่ใช้งานใน 24 ชม., อัตราสำเร็จ 7 วัน, latency ของ API (p95), เวลาตอบของฐานข้อมูล, คิวที่ค้าง, อัตราข้อผิดพลาด 24 ชม.

### ชำระเงินด้วย Stripe

การสมัครแผนเสียเงิน (Basic / Pro / Agency) ทำที่ **Stripe Checkout** (โหมด subscription) บัตรและใบแจ้งหนี้ดูได้ที่ Stripe Billing Portal แผนในระบบเปลี่ยนก็ต่อเมื่อ Stripe ยืนยันแล้วเท่านั้น (webhook หรือตอนผู้ใช้กลับจาก Checkout) ไม่มีฟอร์มบัตรของเราเอง

ตั้งค่า (`Stripe` ใน appsettings หรือ environment `Stripe__...`):

| คีย์ | |
|---|---|
| `Stripe__SecretKey` | secret key (`sk_test_...` / `sk_live_...`) ว่าง = ปิดการชำระเงิน: แผนเสียเงินซื้อไม่ได้ และหน้าเว็บบอกอย่างนั้น |
| `Stripe__PublishableKey` | publishable key (`pk_test_...` / `pk_live_...`) ของบัญชีเดียวกัน ใช้เริ่ม Stripe.js ในหน้าต่างชำระเงินของแอป ว่าง = พาลูกค้าไปหน้า Stripe Checkout แทน |
| `Stripe__SubscriptionPaymentMethods` | ชนิดการชำระเงินของ subscription ที่จ่ายในหน้าต่างของแอป ค่าเริ่มต้น `card,link` (Apple Pay / Google Pay เป็น wallet ของบัตร) ถ้าบัญชีใช้ Link ไม่ได้ให้เอา `link` ออก |
| `Stripe__WebhookSecret` | signing secret (`whsec_...`) ของ webhook endpoint `<โดเมน>/api/webhooks/stripe` |
| `Stripe__ReturnBaseUrl` | ที่อยู่หน้าเว็บที่ Stripe พาลูกค้ากลับ ว่าง = ใช้ origin ของเบราว์เซอร์ที่เรียก (ถูกต้องเมื่อหน้าเว็บกับ API อยู่ไซต์เดียวกัน) |
| `Stripe__Currency` | สกุลเงิน (ค่าเริ่มต้น `thb`; ราคาแผนเป็นบาทเต็มหน่วย) |
| `Stripe__PortalConfigurationId` | ไม่บังคับ: ใช้ Billing Portal configuration ที่ระบุแทนค่าเริ่มต้นของบัญชี |

Webhook ต้องรับ event: `checkout.session.completed`, `customer.subscription.created|updated|deleted`, `invoice.paid`, `invoice.payment_failed`, `refund.created|updated` และสำหรับหน้าต่างชำระเงินในแอป `payment_intent.succeeded|payment_failed|processing|canceled` ทดสอบในเครื่องด้วย `stripe listen --forward-to localhost:5100/api/webhooks/stripe`

วิธีทำงาน:
- **ซื้อแผน:** `PUT /api/billing/plan` สร้าง Checkout Session (ราคาส่งแบบ inline, สินค้า `autopost_<แผน>` ต่อแผน, โค้ดส่วนลดเป็น coupon ลดครั้งเดียวของใบแจ้งหนี้แรก, รายปีลด 20%) แล้วตอบ `checkoutUrl` หน้าเว็บพาไปจ่าย
- **แผนที่มีการสมัครอยู่แล้ว:** เปลี่ยนแผน/รอบบิลโดย Stripe คิดส่วนต่างทันที (`always_invoice`, ถ้าตัดบัตรไม่ผ่านจะไม่เปลี่ยนแผน), เลือกแผน Free = ยกเลิกเมื่อสิ้นรอบ (`cancel_at_period_end`), เลือกแผนเดิมอีกครั้ง = ยกเลิกการยกเลิก
- **สถานะจาก Stripe:** `invoice.paid` → ใช้งานปกติ + บันทึก `TRANSACTIONS` (พร้อม `receiptUrl`), `invoice.payment_failed` → `past_due`, `customer.subscription.deleted` → กลับแผน Free ระบบอ่านสถานะจริงจาก Stripe ใหม่ทุกครั้ง จึงไม่เพี้ยนแม้ event มาสลับลำดับ และกันซ้ำด้วยตาราง `PAYMENT_EVENTS`
- **แอดมิน:** คืนเงินผ่าน Stripe (`POST /api/admin/transactions/{id}/refund`), ลองเก็บเงินใบแจ้งหนี้ที่ค้างใหม่ (`.../retry`), ลูกค้าที่ถูกระงับ/แบนจะถูก `pause_collection` (Stripe ไม่ตัดเงินระหว่างนั้น และกลับมาตัดเมื่อคืนสถานะ ถ้าติดต่อ Stripe ไม่ได้สถานะจะไม่เปลี่ยน) ตั้งแผนให้ลูกค้าเองได้เฉพาะรายที่ไม่มีการสมัครอยู่ใน Stripe
- ลูกค้าที่สมัครแผนเสียเงินก่อนมี Stripe (สถานะ `trial` เดิม) ไม่ถูกแตะ แอดมินปรับสถานะหรือแผนให้ได้

- **หน้าต่างชำระเงินในแอป (5 ช่องทาง):** บัตร, Apple Pay, Google Pay, Link, PromptPay ที่หน้า `/app/billing` (`POST /api/billing/payments` → Stripe.js ยืนยัน → `POST /api/billing/payments/{id}/confirm` + webhook `payment_intent.*`) บัตร/Apple Pay/Google Pay/Link จ่าย subscription ที่ต่ออายุอัตโนมัติ ส่วน PromptPay Stripe ตัดซ้ำเองไม่ได้ จึงจ่ายล่วงหน้า 1 เดือน/1 ปีแล้วแผนหมดอายุเอง อธิบายเต็มใน [`docs/payment-checkout.md`](docs/payment-checkout.md)

**หมายเหตุ:** โค้ดนี้ทดสอบกับ gateway จำลองและ event ตัวอย่างที่เซ็นลายเซ็นจริงเท่านั้น (ดู `tests/SIRIAUTOPOST.Api.IntegrationTests/Payments`) ยังไม่เคยรันกับ Stripe จริง ลองด้วย test key และ webhook ทดสอบก่อนใส่ live key

### รันทั้งระบบด้วย Docker (API + หน้าเว็บ Angular + PostgreSQL)

ต้อง clone `siri_autopost_ui` ไว้ข้าง repo นี้ (หรือตั้ง `UI_PATH`) แล้ว:

```bash
cp .env.example .env    # ตั้ง DB_PASSWORD, ADMIN_PASSWORD, JWT_KEY (32+ ตัวอักษร), ADMIN_EMAIL และ Stripe__SecretKey / Stripe__WebhookSecret
docker compose -f docker-compose.saas.yml up -d --build    # หน้าเว็บที่ http://localhost:8090
```

nginx ในคอนเทนเนอร์ `web` เสิร์ฟหน้าเว็บและส่ง `/api` ต่อไปที่ API ส่วนขยายจึงจับคู่กับ origin เดียวกันได้ ระบบรัน migration และสร้างบัญชีแอดมินให้เองตอนเริ่ม 

**HTTPS:** ชี้ DNS ของโดเมนมาที่เครื่องนี้ เปิดพอร์ต 80/443 ตั้ง `DOMAIN` ใน `.env` แล้วรัน
`docker compose -f docker-compose.saas.yml --profile https up -d --build` Caddy จะขอและต่ออายุใบรับรอง Let's Encrypt ให้เอง (ตั้ง `DOMAIN=localhost` เพื่อลองในเครื่องด้วยใบรับรองทดสอบ)

**Kubernetes / Jenkins:** ดู `deploy/README.md` (ตัวแปร environment ที่ต้องมี, ตั้ง Stripe webhook, ขีดจำกัดที่ต้องตรงกัน)

**CI:** GitHub Actions (`.github/workflows/ci.yml`) build (`-warnaserror`) + test ทั้ง solution กับ PostgreSQL, build server เดิมแล้วรันจริงพร้อม `test-online.mjs`, ทดสอบส่วนขยายโหมดเว็บ (`test-cloud.mjs`) กับ API จริง และ build Docker image ทุก pull request
- Frontend Angular ของโครงนี้อยู่ที่ repo `siri_autopost_ui` (ใช้ `openapi.snapshot.json` สร้าง type ของ API)

---

## 1. วิธีติดตั้งและใช้งาน Client (Chrome Extension)

1. เปิดเบราว์เซอร์ **Google Chrome** แล้วไปที่ `chrome://extensions`
2. เปิดสวิตช์ **Developer mode** (โหมดนักพัฒนา) ที่มุมบนขวา
3. คลิกปุ่ม **Load unpacked** (โหลดส่วนขยายที่คลายการบีบอัดแล้ว)
4. เลือกโฟลเดอร์ `client` ในโปรเจกต์นี้ (`.../siri_autopost_backend/client`)
5. เปิดเว็บ AutoPost ไปที่หน้าทีม/อุปกรณ์ กด "เชื่อมต่อ Chrome นี้" ส่วนขยายจะพาแท็บไปหน้า `status.html` กด "อนุญาต" หนึ่งครั้งก็จับคู่เสร็จ (ไม่มีหน้าตั้งค่าในส่วนขยายแล้ว: ทุกอย่างตั้งที่เว็บ) หรือดาวน์โหลดส่วนขยายจาก `GET /api/extension/download`

---

## 2. วิธีรัน Backend Server เดิม (legacy)

server เดิมเก็บตารางชื่อตัวพิมพ์ใหญ่ขึ้นต้น `FBAP_` (เช่น `FBAP_PROFILES`; ตารางประวัติ migration ชื่อ `fbap_ef_migrations` ตัวพิมพ์เล็ก) และอ่าน `.env` ในโฟลเดอร์ปัจจุบันหรือโฟลเดอร์แม่ก่อนเริ่ม (ตัวแปรที่ตั้งไว้แล้วชนะ)

### วิธีที่ 1: รันด้วย Docker Compose (ใช้ `docker-compose.yml` ที่ root)
1. คัดลอกไฟล์ `.env.example` เป็น `.env`:
   ```bash
   cp .env.example .env
   ```
2. แก้ไขรหัสผ่าน `DB_PASSWORD` และ `ADMIN_PASSWORD` ใน `.env`
3. สั่งรัน container:
   ```bash
   docker compose up -d --build
   ```
4. ระบบจะเปิด API เซิร์ฟเวอร์ที่พอร์ต `8080` (หรือตามพอร์ตที่กำหนดใน `.env`)

### วิธีที่ 2: รันแบบ Local Development (.NET SDK)
1. ติดตั้ง [.NET 10 SDK](https://dotnet.microsoft.com/)
2. เข้าไปที่โฟลเดอร์ Backend:
   ```bash
   cd backend/SIRI.AUTOPOST.Server
   dotnet run
   ```

---

## การเชื่อมต่อกับ Frontend (siri_autopost_ui)
- โครงใหม่ (`SIRIAUTOPOST.Api`) อนุญาต CORS จาก `http://localhost:4200` (Angular) ตั้งเพิ่มได้ที่ `Cors:AllowedOrigins`
- server เดิมรองรับ CORS สำหรับเรียกใช้งานจากภายนอก (เช่น `http://localhost:5173` หรือโดเมนที่กำหนด)
- กำหนด Allowed Origins ใน `appsettings.json` หรือผ่าน Environment Variable `Cors__AllowedOrigins__0`
