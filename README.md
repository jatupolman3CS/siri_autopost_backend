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
│   ├── Dockerfile            # สำหรับรัน Backend ใน Container
│   ├── docker-compose.yml    # Docker Compose สำหรับรัน DB + Server
│   └── .env.example          # ตัวอย่างการตั้งค่า Environment
│
├── client/                   # Google Chrome Extension (Bot ตัวโพสต์)
│   ├── manifest.json         # Chrome Extension Manifest V3
│   ├── background.js         # Service Worker หลัก คุมจังหวะและคิวโพสต์
│   ├── content.js            # Content script จำลองการกระทำบน Facebook
│   ├── dashboard.html        # หน้าตั้งค่า / ควบคุมการทำงานของ Extension
│   ├── dashboard.js          # สคริปต์ควบคุม Dashboard
│   ├── dashboard.css         # สไตล์ Dashboard
│   ├── lib/                  # ฟังก์ชันตัวช่วย (shared, backup, telegram, spintax)
│   ├── icons/                # ไอคอนของ Extension
│   ├── config/               # ไฟล์ Config สำรอง
│   └── tools/                # เครื่องมือแปลงข้อมูลและทดสอบ
│
├── docker-compose.yml        # Docker compose ระดับ root
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
- **บัญชีผู้ดูแล:** ตอนเริ่มระบบจะสร้างผู้ดูแลแพลตฟอร์มจาก `Admin:Email`/`Admin:Password` (ใน Development คือ `admin@autopost.local` / `admin1234`) ถ้าไม่ตั้งค่าไว้จะไม่สร้าง
- **JWT:** ตั้ง `Jwt:Key` (อย่างน้อย 32 ตัวอักษร) ทุก environment นอก Development ไม่อย่างนั้นระบบจะไม่ยอมเริ่ม เช่น `Jwt__Key=...`
- เวิร์กสเปซใหม่ทุกอันจะมี **ข้อมูลตัวอย่าง** จากดีไซน์: บัญชีโซเชียล 7 บัญชี (เพจ Facebook พร้อม 20 กลุ่ม), ข้อความสำเร็จรูป 4 อัน, ประวัติโพสต์ 1 สัปดาห์ คิวโพสต์ล่วงหน้า 1 สัปดาห์ และรายงานข้อผิดพลาด 5 รายการ (บัญชีตัวอย่างไม่มีเครื่องผูกอยู่ โพสต์ของบัญชีเหล่านี้จึงไม่ถูกส่งจริง บัญชีจริงได้มาจากการจับคู่ส่วนขยาย)
- Integration test ใช้ฐาน `siriautopost_test` (ลบแล้วสร้างใหม่ทุกครั้ง) เปลี่ยนได้ด้วย environment variable `SIRIAUTOPOST_TEST_DB`
- เพิ่ม migration: `dotnet ef migrations add <ชื่อ> -p src/SIRIAUTOPOST.Infrastructure -s src/SIRIAUTOPOST.Api -o Data/Migrations`
- เพิ่มฟีเจอร์ใหม่: Entity ใน Domain → Command/Query + Handler ใน `Application/Features/<ฟีเจอร์>/<ฟีเจอร์>.cs` → ลงทะเบียนใน `Application/DependencyInjection.cs` → Repository + Configuration ใน Infrastructure → Controller ใน Api

| กลุ่ม | Endpoint |
|---|---|
| Auth | `POST /api/auth/signup`, `POST /api/auth/login`, `GET /api/auth/me`, `PUT /api/auth/me/plan` |
| เวิร์กสเปซ | `GET/POST /api/workspaces`, `GET /api/workspaces/{ws}/accounts`, `POST .../accounts/{id}/reconnect` |
| โพสต์ | `GET .../posts?from&to`, `POST .../posts/schedule`, `DELETE .../posts/{id}`, `POST .../posts/{id}/retry`, `POST .../posts/{id}/dismiss`, `GET .../errors` |
| คลัง | `GET/POST .../media` (multipart field `file`), `GET .../media/{id}/content`, `GET/POST .../snippets` |
| ระบบโพสต์ | `GET .../engine`, `PUT .../engine/anti-ban`, `PUT .../engine/offline`, `POST .../engine/extension`, `POST .../engine/waiting/skip` |
| อุปกรณ์ (เจ้าของ) | `GET .../devices`, `POST .../devices/pairing` (รหัสจับคู่ 10 นาที), `DELETE .../devices/{id}` |
| ส่วนขยาย (`X-Device-Key`) | `POST /api/device/pair`, `POST /api/device/heartbeat`, `PUT /api/device/groups`, `POST /api/device/jobs/claim` (204 = ไม่มีงาน), `POST /api/device/jobs/{id}/result`, `GET /api/device/media/{id}` |

ทุก endpoint ต้องส่ง `Authorization: Bearer <token>` ยกเว้น signup, login, `/healthz` และ `/api/device/*` (ใช้หัว `X-Device-Key` จากการจับคู่ ยกเว้น `pair`)
- Frontend Angular ของโครงนี้อยู่ที่ repo `siri_autopost_ui` (ใช้ `openapi.snapshot.json` สร้าง type ของ API)

---

## 1. วิธีติดตั้งและใช้งาน Client (Chrome Extension)

1. เปิดเบราว์เซอร์ **Google Chrome** แล้วไปที่ `chrome://extensions`
2. เปิดสวิตช์ **Developer mode** (โหมดนักพัฒนา) ที่มุมบนขวา
3. คลิกปุ่ม **Load unpacked** (โหลดส่วนขยายที่คลายการบีบอัดแล้ว)
4. เลือกโฟลเดอร์ `client` ในโปรเจกต์นี้ (`.../siri_autopost_backend/client`)
5. ปักหมุดไอคอนส่วนขยาย แล้วคลิกเพื่อเปิดหน้า Dashboard ตั้งค่าและเริ่มทำงาน

---

## 2. วิธีรัน Backend Server เดิม (legacy)

### วิธีที่ 1: รันด้วย Docker Compose (แนะนำสำหรับ Production)
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
