# หน้าต่างชำระเงิน 5 ช่องทาง (Payment Checkout)

หน้า `/app/billing` ของ Angular มีหน้าต่างชำระเงินในแอป: บัตร, Apple Pay, Google Pay, Link และ PromptPay เรียงกันเป็นรายการเดียว มีไอคอนประจำแต่ละช่องทาง กดเลือกแถวแล้วแถวนั้นเปิดฟอร์มของตัวเอง ผู้ให้บริการชำระเงินคือ **Stripe** ทั้งหมด (รองรับครบทุกช่องทางในที่เดียว และบัญชี Stripe ประเทศไทยรับ PromptPay ได้)

> สถานะการทดสอบ: ทุกอย่างทดสอบกับ gateway จำลอง, event ที่เซ็นลายเซ็นจริง และการจับ HTTP ที่ส่งไป Stripe (`StripePaymentGatewayTests`) หน้า UI ดูใน dev server จริงจนถึงขั้น Stripe.js แล้ว **ยังไม่เคยรันกับบัญชี Stripe จริง** (ไม่มี test key ในเครื่องนี้) ลองด้วย test key ตามหัวข้อ "ลองใช้" ก่อนเปิดใช้จริง

## 1. ทำไมแยกเป็น 2 เส้นทาง

| ช่องทาง | วิธีที่ Stripe ตัดเงิน | ผลกับแผน |
|---|---|---|
| บัตร, Apple Pay, Google Pay, Link | **Subscription** (สร้างแบบ `default_incomplete`, จ่ายใบแจ้งหนี้ใบแรกด้วย Stripe.js) | ต่ออายุอัตโนมัติ มี Billing Portal ยกเลิกได้ |
| PromptPay | **PaymentIntent** ครั้งเดียว (`allowed_payment_method_types = promptpay`) | จ่ายล่วงหน้า 1 เดือน/1 ปี ไม่ต่ออายุเอง ครบกำหนดกลับไป Free |

เหตุผล: PromptPay เป็น payment method แบบใช้ครั้งเดียว Stripe ไม่รองรับกับ Checkout โหมด subscription และ Subscriptions รองรับเฉพาะ `send_invoice` (https://docs.stripe.com/payments/promptpay) จึงตัดซ้ำอัตโนมัติไม่ได้ แอปเลยขายเป็น "จ่ายครั้งเดียวต่อรอบ" และ `PrepaidExpiryWorker` พาลูกค้ากลับ Free เมื่อครบกำหนด (ลูกค้าจ่ายซ้ำเพื่อต่ออายุได้ ระบบนับต่อจากวันหมดอายุเดิม)

## 2. ลำดับการทำงาน

```
เบราว์เซอร์                       API (.NET)                             Stripe
   │ GET  /api/billing/payment-config ─▶ publishable key (pk_...)
   │ POST /api/billing/payments {plan, cycle, promoCode, method}
   │                                    ├─ card/wallet/link ─▶ Subscription (default_incomplete)
   │                                    └─ promptpay ───────▶ PaymentIntent (promptpay)
   │ ◀─ { id: pi_..., clientSecret, amount, flow }   + แถว PAYMENT_ATTEMPTS (pending)
   │ Stripe.js:  confirmCardPayment / confirmPayment (Express Checkout) / confirmPromptPayPayment
   │ POST /api/billing/payments/{id}/confirm ──▶ อ่าน PaymentIntent จาก Stripe แล้วอัปเดตแถว
   │ ◀─ { status: pending|succeeded|failed, user: {plan...} }
   │                                    ◀─ webhook payment_intent.* (+ invoice.*, customer.subscription.*)
```

กฎสำคัญ: **แผนเปลี่ยนจากสิ่งที่ Stripe บอกเท่านั้น** ไม่เชื่อ "สำเร็จ" ที่เบราว์เซอร์รายงาน webhook กับ `/confirm` ทำงานผ่านโค้ดชุดเดียวกัน (`PaymentIntentSync`) ใครมาก่อนชนะ อีกฝั่งเห็นว่าเสร็จแล้ว

## 3. Frontend (Angular, `siri_autopost_ui`)

- `shared/components/payment-method-list/` รายการ 5 แถวแบบ radio (ไอคอนในตัว, ลูกศร/Home/End เลือกแถว, แถวที่เลือกเปิดฟอร์มเหมือน accordion ของ Stripe)
- `features/billing/payment-checkout/` หน้าต่างชำระเงิน แต่ละแถวเรียก Stripe.js:
  - **บัตร**: `elements.create('card')` + `stripe.confirmCardPayment(clientSecret, …)`
  - **Apple Pay / Google Pay / Link**: `elements.create('expressCheckout', { paymentMethods: … })` ที่เปิดเฉพาะปุ่มของแถวนั้น + `stripe.confirmPayment({ elements, clientSecret, redirect: 'if_required' })` (Stripe วาดปุ่มทางการเอง; อุปกรณ์ที่ใช้ไม่ได้จะมีข้อความอธิบายแทนที่จะซ่อนแถว)
  - **PromptPay**: `stripe.confirmPromptPayPayment(clientSecret, { payment_method: { billing_details: { email } } })` Stripe แสดง QR ใน modal ของตัวเอง
- หลัง Stripe.js ตอบ หน้าต่างเรียก `POST /payments/{id}/confirm` แล้วโพลทุก 2 วินาทีระหว่าง `pending` (`PaymentStore.settle`) แสดงผลสำเร็จ/ล้มเหลว/รอดำเนินการ
- ไม่มี publishable key = ใช้หน้า Stripe Checkout เดิม (redirect) ไม่พังของเดิม
- ข้อมูลบัตรพิมพ์ใน iframe ของ Stripe เท่านั้น ไม่ผ่านเซิร์ฟเวอร์เรา (PCI SAQ-A)

## 4. Backend (`siri_autopost_backend`)

ตัวอย่างสร้างการชำระเงิน (ย่อจาก `StripePaymentGateway`):

```csharp
// บัตร / Apple Pay / Google Pay / Link: subscription ที่ยังไม่ active จนกว่าใบแจ้งหนี้ใบแรกจะจ่าย
var sub = await new SubscriptionService(client).CreateAsync(new SubscriptionCreateOptions
{
    Customer = customerId,
    Items = [new() { PriceData = new() { Currency = "thb", Product = "autopost_pro",
                     UnitAmount = 79000, Recurring = new() { Interval = "month" } } }],
    PaymentBehavior = "default_incomplete",
    PaymentSettings = new() { SaveDefaultPaymentMethod = "on_subscription", PaymentMethodTypes = ["card", "link"] },
    Metadata = metadata,
    Expand = ["latest_invoice.confirmation_secret"],
}, new RequestOptions { IdempotencyKey = $"payment-subscription:{attemptId}" });
var clientSecret = sub.LatestInvoice.ConfirmationSecret.ClientSecret;   // pi_..._secret_...

// PromptPay: PaymentIntent ครั้งเดียว
var pi = await new PaymentIntentService(client).CreateAsync(new PaymentIntentCreateOptions
{
    Amount = 79000, Currency = "thb", Customer = customerId, ReceiptEmail = email,
    AllowedPaymentMethodTypes = ["promptpay"], Metadata = metadata,
}, new RequestOptions { IdempotencyKey = $"payment-prepaid:{attemptId}" });
```

- ราคามาจากฐานข้อมูล (`PlanSetting`) + โค้ดส่วนลด + โหมดทดสอบของแอดมิน (`PaymentOverride`) เบราว์เซอร์ไม่ส่งจำนวนเงินมาเลย
- ตาราง `PAYMENT_ATTEMPTS` (1 แถวต่อ PaymentIntent, `StripePaymentIntentId` unique) เก็บสถานะ `pending/succeeded/failed`, ช่องทางที่ใช้จริง (`apple_pay`, `google_pay`, `link`, `card`, `promptpay`) และข้อความที่ล้มเหลว `succeeded` เป็นสถานะสุดท้าย (event ล้มเหลวที่มาช้าไม่ย้อนได้) ส่วน `failed` กลับเป็น `succeeded` ได้ เพราะลูกค้าลองบัตรใบอื่นกับ PaymentIntent เดิมได้
- แผนที่ซื้อล่วงหน้า: `User.ApplyPrepaid` (วันหมดอายุเก็บใน `PlanRenewsAt`, ไม่มี `StripeSubscriptionId`) + บันทึก `TRANSACTIONS` (คืนเงินผ่านแอดมินได้ ใช้ `payment_intent` เดียวกัน) ซ้ำ webhook/โพลไม่ซ้ำรายการ เพราะ key `intent:{pi}` ใน `PAYMENT_EVENTS`

## 5. Webhook และความปลอดภัย

1. Stripe Dashboard → Developers → Webhooks → Add endpoint `https://<โดเมน>/api/webhooks/stripe` เลือก event:
   `payment_intent.succeeded`, `payment_intent.payment_failed`, `payment_intent.processing`, `payment_intent.canceled` (ของใหม่) และของเดิม `customer.subscription.created|updated|deleted`, `invoice.paid`, `invoice.payment_failed`, `checkout.session.completed`, `refund.created|updated`
2. คัดลอก signing secret (`whsec_…`) ใส่ `Stripe__WebhookSecret` (คั่นด้วย `,` ได้หลายค่าตอนเปลี่ยน secret)
3. เซิร์ฟเวอร์ตรวจ `Stripe-Signature` กับ **body ดิบ** (`EventUtility.ConstructEvent`, อ่าน body ตรง ๆ ไม่ผ่าน model binding) ลายเซ็นผิด/ไม่มี = 400 ไม่ประมวลผล เวลาใน header เกิน tolerance ของ SDK ถูกปฏิเสธ (กัน replay)
4. endpoint เป็น anonymous ได้เพราะลายเซ็นคือ credential ส่วน endpoint อื่นต้องล็อกอิน และ `/confirm` คืน 404 สำหรับ PaymentIntent ของคนอื่น
5. กันซ้ำ: Stripe ส่ง event อย่างน้อยหนึ่งครั้ง (อาจซ้ำ/สลับลำดับ) handler บันทึก `evt_…` ใน `PAYMENT_EVENTS` และอ่านสถานะล่าสุดของ PaymentIntent/Subscription จาก Stripe ทุกครั้ง ไม่เชื่อ payload ที่มาถึง
6. ตอบ 200 เมื่อจัดการเสร็จ ตอบ 5xx เมื่อพลาด เพื่อให้ Stripe ส่งซ้ำ (retry หลายวัน)
7. ความลับ: `Stripe__SecretKey` กับ `Stripe__WebhookSecret` อยู่ใน environment/secret ของเซิร์ฟเวอร์เท่านั้น เบราว์เซอร์ได้แค่ publishable key (`pk_…`)
8. สถานะที่แอปรู้: **สำเร็จ** (`succeeded`) → เปิดแผน/บันทึกรายการ, **ล้มเหลว** (`failed`, `payment_intent.payment_failed`/`canceled`) → บันทึกเหตุผล แผนไม่เปลี่ยน ลูกค้าลองใหม่ได้, **รอดำเนินการ** (`pending`, `processing`/`requires_action`) → หน้าต่างโพลต่อ ปิดหน้าต่างได้ webhook เปิดแผนให้เอง

## 6. ตั้งค่า

| ค่า | ใช้ทำอะไร |
|---|---|
| `Stripe__SecretKey` | `sk_test_…`/`sk_live_…` ว่าง = ปิดการชำระเงินทั้งระบบ |
| `Stripe__PublishableKey` | `pk_…` ของบัญชีเดียวกัน ว่าง = ใช้หน้า Stripe Checkout เดิมแทนหน้าต่างในแอป |
| `Stripe__WebhookSecret` | `whsec_…` ของ endpoint ข้างบน |
| `Stripe__SubscriptionPaymentMethods` | ค่าเริ่มต้น `card,link` (Apple/Google Pay เป็น wallet ของบัตร จึงไม่ต้องใส่) ถ้าบัญชีใช้ Link ไม่ได้ให้เอา `link` ออก ว่าง = ตาม Dashboard |

ใน Stripe Dashboard → Settings → Payment methods เปิด **Cards, Link, PromptPay** (บัญชีต้องเป็นประเทศไทยและสกุลเงิน THB) ส่วน **Apple Pay** ต้อง Register domain ของเว็บ (Settings → Payment methods → Apple Pay → Add new domain, ต้องเป็น HTTPS) Google Pay ไม่ต้องลงทะเบียนแต่ต้อง HTTPS

## 7. ลองใช้

1. ตั้ง `Stripe__SecretKey`/`Stripe__PublishableKey` เป็น test key, รัน API กับ Angular (`npm start`)
2. `stripe listen --forward-to localhost:5100/api/webhooks/stripe` แล้วใส่ `whsec_…` ที่ได้
3. `/app/billing` → เลือกแผน → "ไปหน้าชำระเงิน": บัตร `4242 4242 4242 4242` (สำเร็จ), `4000 0000 0000 0002` (ถูกปฏิเสธ), `4000 0025 0000 3155` (3-D Secure) PromptPay ใน sandbox มีปุ่ม **Simulate scan** บนหน้า QR ให้เลือกอนุมัติ/ปฏิเสธ
4. อยากทดสอบด้วยเงินจริงจำนวนน้อย: แอดมินตั้ง "โหมดทดสอบการชำระเงิน" (`/app/admin/payment-test`) ให้อีเมลของตัวเองจ่ายขั้นต่ำ 10 บาท
5. ทดสอบอัตโนมัติ: `dotnet test` (`PaymentCheckoutTests`, `StripePaymentGatewayTests`, `PaymentAttemptTests`) และ `npx ng test --watch=false` (`payment-checkout.component.spec.ts`, `payment-method-list.component.spec.ts`, `payment.store.spec.ts`)

## 8. ข้อจำกัดที่รู้

- PromptPay ต่ออายุอัตโนมัติไม่ได้ (ข้อจำกัดของ Stripe) ลูกค้าต้องจ่ายซ้ำเมื่อครบกำหนด ยังไม่มีอีเมลเตือนก่อนหมดอายุ
- ลูกค้าที่มี subscription อยู่แล้วเปลี่ยนแผนด้วยปุ่มเลือกแผนเดิม (คิดส่วนต่าง) หน้าต่างนี้ใช้ตอนซื้อครั้งแรก/ต่ออายุ prepaid เท่านั้น
- ส่วนลด 100% ที่ทำให้ยอดต่ำกว่า 10 บาท (ขั้นต่ำของ Stripe) ใช้ในหน้าต่างนี้ไม่ได้ ระบบแจ้งให้ลองโดยไม่ใช้โค้ด
- subscription ที่เริ่มแล้วลูกค้าทิ้งหน้าต่างกลางทาง Stripe ปล่อยให้หมดอายุเอง (`incomplete_expired`) ไม่กระทบแผนเพราะแผนยังไม่ถูกผูก
- ไอคอนในรายการเป็นภาพแทนที่ใช้สีของแบรนด์ ปุ่มจริงของ Apple Pay/Google Pay/Link เป็นของ Stripe
