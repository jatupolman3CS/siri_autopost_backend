# Siri AutoPost (Backend & Client)

คลังโค้ดนี้รวบรวม **Backend Server** (.NET Core Web API / PostgreSQL) และ **Client** (Google Chrome Extension สำหรับ AutoPost) ไว้อยู่ด้วยกัน

---

## โครงสร้างโปรเจกต์

```text
siri_autopost_backend/
├── backend/                  # ASP.NET Core Web API Server
│   ├── AutoPost.Server/      # โค้ด Backend API (.NET 10 / C#)
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

## 1. วิธีติดตั้งและใช้งาน Client (Chrome Extension)

1. เปิดเบราว์เซอร์ **Google Chrome** แล้วไปที่ `chrome://extensions`
2. เปิดสวิตช์ **Developer mode** (โหมดนักพัฒนา) ที่มุมบนขวา
3. คลิกปุ่ม **Load unpacked** (โหลดส่วนขยายที่คลายการบีบอัดแล้ว)
4. เลือกโฟลเดอร์ `client` ในโปรเจกต์นี้ (`.../siri_autopost_backend/client`)
5. ปักหมุดไอคอนส่วนขยาย แล้วคลิกเพื่อเปิดหน้า Dashboard ตั้งค่าและเริ่มทำงาน

---

## 2. วิธีรัน Backend Server

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
   cd backend/AutoPost.Server
   dotnet run
   ```

---

## การเชื่อมต่อกับ Frontend (siri_autopost_ui)
- Backend รองรับ CORS สำหรับเรียกใช้งานจากภายนอก (เช่น `http://localhost:5173` หรือโดเมนที่กำหนด)
- กำหนด Allowed Origins ใน `appsettings.json` หรือผ่าน Environment Variable `Cors__AllowedOrigins__0`
