-- ตั้งบัญชี jatupolman048@gmail.com เป็นผู้ดูแลแพลตฟอร์ม (admin) และแผนสูงสุด Premium (key = Agency)
--
-- รันบน DB ของ "เว็บจริง" (SIRIAUTOPOST_PRD; DEV คือ SIRIAUTOPOST) เป็น SQL ธรรมดา ใช้ได้กับ psql, pgAdmin, DBeaver
--   PowerShell (ผ่าน kubectl):
--     Get-Content -Raw tools\sql\promote-admin-premium.sql | kubectl exec -i -n infra postgres-0 -- sh -c 'psql -U $POSTGRES_USER -d SIRIAUTOPOST_PRD'
--   psql ตรงๆ:
--     psql -h <host> -p <port> -U <user> -d SIRIAUTOPOST_PRD -f tools/sql/promote-admin-premium.sql
--
-- ผลที่ควรเห็น: ตาราง "ก่อน" 1 แถว (role = User), "UPDATE 1" พร้อมแถวที่แก้, ตาราง "หลัง" role = Admin, plan = Agency
-- ถ้า "ก่อน" ไม่มีแถว = บัญชีไม่อยู่ใน DB นี้ (ลอง DB อีกตัว หรือเช็กการสะกดอีเมล) ไม่มีอะไรถูกแก้
-- ถ้า "ก่อน" มี stripe_subscription_id = บัญชีจ่ายผ่าน Stripe อยู่ คำสั่งนี้จะไม่แตะ (แผนต้องตามที่ Stripe บอก)
--
-- ค่า (ชื่อคอลัมน์ snake_case, ค่า enum เป็นตัวใหญ่ตัวแรก) ตรงกับที่ API เขียนเอง; แผนที่ตั้งด้วยมือแบบนี้ไม่มีวันหมดอายุ
-- (plan_renews_at = NULL) และ limit_overrides ที่แอดมินเคยตั้งให้บัญชีนี้ (ถ้ามี) ไม่ถูกแตะ
-- หลังรัน: ออกจากระบบแล้วล็อกอินใหม่ เพราะ role อยู่ใน token ที่ออกตอนล็อกอิน
-- (ถ้าตั้ง Admin__Email=jatupolman048@gmail.com แล้ว redeploy API ด้วย โค้ดก็ตั้งค่าเดียวกันนี้ให้ทุกครั้งที่สตาร์ท)

BEGIN;

-- ก่อน
SELECT id, email, role, plan, status, stripe_subscription_id, plan_renews_at
FROM "USERS"
WHERE email = 'jatupolman048@gmail.com';

UPDATE "USERS"
SET role = 'Admin',
    plan = 'Agency',
    plan_renews_at = NULL
WHERE email = 'jatupolman048@gmail.com'
  AND stripe_subscription_id IS NULL
RETURNING id, email, role, plan;

-- หลัง (ยังไม่ commit: ดูผลก่อน ถ้าไม่ถูกต้องให้ใช้ ROLLBACK แทน COMMIT)
SELECT id, email, role, plan, status
FROM "USERS"
WHERE email = 'jatupolman048@gmail.com';

COMMIT;
