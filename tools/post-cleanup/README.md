# post-cleanup: คัด/ปรับข้อความโพสต์ใน library

สคริปต์ที่ใช้คัดและปรับปรุงข้อความโพสต์ของ workspace SIRI STUDIO PHOTO (ทำเมื่อ 2026-10-05) เก็บไว้เพื่อรันซ้ำกับ DB อื่น (เช่น `SIRIAUTOPOST_PRD` เมื่อมีข้อมูลแล้ว) ต้องมี Python 3 กับ `psycopg2`

## สิ่งที่ทำ

| ขั้น | รายละเอียด |
|---|---|
| คัดโพสต์ | `plan.py` ตัดสินทีละโพสต์ ใช้ (active) / ไม่ใช้ (inactive) พร้อมเหตุผล ดูไฟล์ report |
| ปรับข้อความ | โพสต์ที่ใช้ได้ → `{{code}}` + บล็อกโปรโมต 5 บรรทัด + เนื้อหาที่ทำความสะอาด + แฮชแท็ก 6 ตัว |
| โพสต์ที่ไม่ใช้ | ข้อความคงเดิม แก้เฉพาะช่องทางติดต่อผิด (ลิงก์ lin.ee ขาด, @siristduiophoto, @SIRISTUDIOPHOTO, ลิงก์ TikTok/LIFF เดิม) |
| สื่อนำ | ทุกโพสต์ได้โปสเตอร์ + วิดีโอเป็นสองไฟล์แรก (รวมไม่เกิน 20 ไฟล์) |
| ชื่อเพจกดได้ | ตั้ง `PageTags` ของชุดโพสต์ ส่วนขยายพิมพ์ชื่อเพจในบล็อกโปรโมตเป็น @mention |
| `IMPORTED_POSTS` | แก้ช่องทางติดต่อผิดแบบเดียวกัน |

บล็อกโปรโมต (บรรทัดแรกสุดคือรหัสกลุ่ม ถ้ากลุ่มไม่มีรหัสบรรทัดนั้นหายไป):

```
{{code}}
สั่งรูปออนไลน์ได้ด้วยตัวเอง ไม่ต้องไปร้าน : https://www.siristudiophoto.com
ดูผลงาน : https://www.siristudiophoto.com/index/portfolio
สั่งงาน สอบถาม จองคิว คลิก https://lin.ee/c2I3lh4
Line Official : @siristudiophoto
Page facebook : https://www.facebook.com/KHRUSIRI (SIRI Studio Photo รับตัดต่อ รีทัช รูปติดบัตร รูปจบ สมัครงาน สมัครสอบ อัดรูป)
```

## วิธีรัน

```bash
# 1) ลองก่อน (rollback ทุกอย่าง แสดงตัวเลข + เขียน report)
python cleanup_posts.py --db SIRIAUTOPOST --workspace <workspace-id> --collection <collection-id> \
    --lead <poster-media-id> <video-media-id> --report report.csv

# 2) เขียนจริง
python cleanup_posts.py ... --commit
```

- `--lead` = id ของโปสเตอร์และวิดีโอใน `MEDIA_FILES` (อัปโหลดเข้าไลบรารีก่อน)
- `--drop-media drop.json` = รายการ media id ที่แก้ลายน้ำไม่ได้ ให้ถอดออกจากทุกโพสต์
- `--repair-needed need.json` = media id ที่ยังต้องแก้ (โพสต์ที่รูปสะอาดจะถูกเลือกก่อนตอนจำกัดข้อความซ้ำ)
- `--cap 10` = จำนวนโพสต์สูงสุดต่อ "ข้อความเดียวกัน" (กันโพสต์ซ้ำในกลุ่มเดิม ๆ)
- เชื่อมต่อด้วย `ConnectionStrings__Default` ใน `siri_autopost_backend/.env` (เปลี่ยนชื่อ DB ด้วย `--db`) ตั้ง `SIRI_DB_ENV` ถ้าอยากใช้ไฟล์ .env อื่น
- DB ต้องผ่าน migration `MasterPosts` แล้ว (มีคอลัมน์ `COLLECTION_POSTS.active`) ไม่งั้นสคริปต์หยุดพร้อมข้อความ
- ทำ backup ก่อนเขียนจริงเสมอ (ไฟล์ของ dev อยู่ที่ `C:\ProjectAutoPost\backups\`)

`r2.py` = client ของ R2 (อ่าน `R2__*` จาก `backend/.env` หรือไฟล์ที่ตั้งใน `SIRI_R2_ENV`) ใช้ตอนอัปโหลดโปสเตอร์/วิดีโอและรูปที่แก้แล้วทับ key เดิม
