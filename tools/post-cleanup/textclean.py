import re,json,collections
HANDLE=r"@\s?siri\w*"
URL=r"https?://\S+"
STALE=[r"โควิด",r"covid",r"ข้อความ(ใน(เพจ|ไลน์))?(ลูกค้า)?(เยอะ|จำนวนมาก)",r"ลูกค้า(ทัก|แอด)(เข้า)?มา(เยอะ|จำนวนมาก|อย่างล้นหลาม)",r"แอดมิน(ดูแล|ตอบ)[^\n]{0,20}(ไม่ทั่วถึง|ไม่ทัน|ไม่ค่อย)",r"ตอบ(กลับ)?(ช้า|หลัง|ทีหลัง)",r"ทยอยตอบ",r"ไม่ค่อยมีเวลา(อัพเดต|ลง|อัพ)",r"ข้อความอัตโนมัติ",r"กดติดตาม|ถูกใจเพจ|ลุ้นรับ|ของรางวัล|รับโปรโมชั่น|รับฟรีภาพใหญ่",r"คิว(ด่วน)?เต็ม|เปิดรับคิว|ปิดรับ|ร้านหยุด|หมดเขต",r"ส่วนลด\s*\d+\s*บาท|รับส่วนลดพิเศษ",r"ฝากติดตาม",r"ทางร้านจึงขอแจ้ง|แจ้งเปลี่ยน|ไลน์ใหม่|เปลี่ยนไลน์",r"tiktok",r"ไอจี|\bIG\b",r"อ่านรายละเอียดบางส่วนในข้อความเพจ",r"ลิ้?ง(ค์|ก์)?ในเพจ|ลิ้?ง(ค์|ก์)?ในข้อความเพจ|ลิ้?ง(ค์|ก์)?ในคอม(เมนท์|เม้นท์)|ตามลิ้?ง(ค์)?",r"ในเพจ(แอดมิน|ตอนนี้|ร้านดูแล)",r"ข้อความหลัง|ตอบกลับหลัง"]
STALE_RE=re.compile("|".join(STALE),re.I)
def norm_ws(t):
    t=t.replace("\r\n","\n").replace("\r","\n").replace("￼","").replace("​","").replace("﻿","").replace(" "," ")
    t=re.sub(r"[ \t]+"," ",t)
    t=re.sub(r" ?\n ?","\n",t)
    t=re.sub(r"\n{3,}","\n\n",t)
    return t.strip()
OLD_HEADER=[r"สั่งรูปออนไลน์ได้ด้วยตัวเอง[^\n]*",r"ดูผลงาน ?:[^\n]*",r"สั่งงาน สอบถาม จองคิว คลิก[^\n]*",r"💻 สั่งทำรูปผ่านเว็บไซต์[^\n]*",r"✅ ผลงาน ?:[^\n]*",r"✨━+✨"]
def strip_header(t):
    for p in OLD_HEADER: t=re.sub(p,"",t)
    return t
PAGE_NAME=r"(?:ดู(?:รีวิว|ผลงาน)อื่น ?ๆ ?(?:ที่)?เพจ |ติดตามผลงาน(?:มากมาย)?(?:ได้)?ที่เพจ |ที่เพจ )?SIRI Studio Photo รับตัดต่อ รีทัช รูปติดบัตร รูปจบ สมัครงาน สมัครสอบ อัดรูป(?: ?ค่ะ| ?ได้เลยนะคะ| ?ได้เลย)?"
def split_tags(t):
    tags=re.findall(r"#[^\s#]+",t)
    body=re.sub(r"#[^\s#]*","",t)
    return body,tags
def cta_strip(t):
    # remove calls to action that carry a handle or a link (the promo block above the post has them)
    t=re.sub(r"(?:📩 ?|📲 ?|💬 ?)?(?:สนใจ|ติดต่อ|สั่งงาน|สอบถาม|แอดไลน์|ทัก)[^\n.!?]{0,60}?(?:พิมพ์|ไอดี|ID|id|line|ไลน์|คลิก)[^\n]{0,40}?"+HANDLE+r"[^\n]*?(?:\(มี ?@ ?(?:นะคะ)?\))?(?: ?หรือ ?(?:คลิก ?)?"+URL+r")?(?: ?ได้เลย(?:นะคะ|ค่ะ|ครับผม)?)?","",t)
    t=re.sub(r"(?:สนใจ|ติดต่อ|สั่งงาน)[^\n]{0,40}?(?:คลิก|ลิงก์|ลิ้งค์)\s*"+URL,"",t)
    t=re.sub(r"(?:หรือ ?)?(?:คลิก ?)?"+URL,"",t)
    t=re.sub(r"(?:พิมพ์|ไอดี|ID ?line|line|ไลน์) ?"+HANDLE+r"(?: ?ได้เลย(?:นะคะ|ค่ะ)?)?(?: ?\(มี ?@ ?(?:นะคะ)?\))?","",t,flags=re.I)
    t=re.sub(HANDLE,"",t,flags=re.I)
    return t
def drop_stale_lines(t):
    out=[]
    for ln in t.split("\n"):
        if STALE_RE.search(ln): continue
        out.append(ln)
    return "\n".join(out)
FIX=[("แต้วหน้า","แต่งหน้า"),("ผู็ช่วย","ผู้ช่วย"),("มีบริกาาร","มีบริการ"),("เนี๊ยบ","เนี้ยบ"),("เล๊ยยยย","เลย"),("ปตรี","ป.ตรี"),("ปโท","ป.โท"),("ปเอก","ป.เอก"),("ปบัณฑิต","ป.บัณฑิต"),("สมัคสอบ","สมัครสอบ"),("ท้องถิ่น กศน )","ท้องถิ่น กศน.)"),("เชิ๊ต","เชิ้ต"),("ลิ้งค์","ลิงก์"),("ลิงค์","ลิงก์"),("มหาลัย","มหาวิทยาลัย")]
def fixes(t):
    for a,b in FIX: t=t.replace(a,b)
    return t
def tidy(t):
    t=re.sub(r"[ ]{2,}"," ",t)
    t=re.sub(r" ?\n ?","\n",t)
    t=re.sub(r"\n{3,}","\n\n",t)
    t=re.sub(r"^[\s\-–—•·.,:;!?\"“”']+","",t)   # leading junk
    t=re.sub(r"[\s\-–—•·,:;\"“”]+$","",t)
    return t.strip()
def clean(t):
    t=norm_ws(t)
    t=strip_header(t)
    t=re.sub(PAGE_NAME,"",t,flags=re.I)
    body,tags=split_tags(t)
    body=cta_strip(body)
    body=drop_stale_lines(norm_ws(body))
    body=fixes(body)
    body=tidy(body)
    return body,tags
