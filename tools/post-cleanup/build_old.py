import re,json,collections
from textclean import norm_ws,strip_header,STALE_RE,fixes,tidy
BRAND="SIRI Studio Photo"
def flat(t):
    t=norm_ws(t); t=re.sub(r"\s+"," ",t); return t
# ---- good sentences (verbatim from the shop's own posts) ----
SP_=r" ?"
G={}
G["b3"]=(r"ตัดต่อรีทัชและภาพติดบัตรสำหรับยื่นจบ หรือสมัครงาน สมัครสอบทุก(?:มหาลัย|มหาวิทยาลัย) รอรับงานสวย ๆ ที่บ้านได้เลย ไม่ต้องจ้างช่างแต่งหน้าทำผม ไม่ต้องซื้อชุดก็มีภาพติดบัตรสวย ๆ หล่อ ๆ ได้",None)
G["b6"]=(r"(?:SIRI ?STUDIO ?PHOTO |SIRISTU ?DIO ?P[A-Z ]*? |SIRISTUDIO PHOTO )?(?:รับ)?ตัดต่อภาพติดบัตรทุกชนิด แต่งหน้าทำผมฟรี เลือกทรงผม เลือกชุดได้ ถ่ายที่ไหนก็สวยครบ จบที่เดียว ราคาถูกกกกกก มีบริการอัด(?:ภาพ|ภาพติดบัตร)ทุกขนาด (?:อัดขยาย)?พร้อมกรอบเลือกเองได้ ส่งทั่วไทย ภาพอัดสีสวยคมชัด เคลือบน้ำยากันรอยนิ้วมือใช้ได้นาน ๆ",None)
G["list"]=(r"SIRI ?STUDIO ?PHOTO รับตัดต่อรูปจบ รูปติดบัตรชุดครุย ชุดพยาบาล ผู้ช่วยพยาบาล ข้าราชการทุกสังกัด ชุดนักเรียน นักศึกษาทุกสถาบัน ยูนิฟอร์มต่าง ๆ",None)
G["slogan"]=(r"SIRI STUDIO PHOTO ตัดต่อภาพติดบัตร อัดขยายรูป แต่งหน้าทำผมฟรี เลือกทรงได้ อยากถ่ายรูปติดบัตรสวย ๆ นึกถึงเรานะคะ ?#?ถ่ายจากมือถือก็สวยได้ #?ถ่าย(?:ที่ไหน|ที่บ้าน)สวยที่นี่ ถ่ายแล้วรอรับรูปที่บ้านได้เลย ไม่ต้องเสียเวลามาเอง",None)
G["serv2"]=(r"รับตัดต่อภาพติดบัตร รูปจบทุกสถาบัน ทุกระดับ รูปบัตรพนักงาน บัตรข้าราชการทุกหน่วยงาน สังกัด ชุดสมัครงาน สมัครสอบ อัดขยายรูป อัดกรอบทุกขนาด พร้อมไฟล์ภาพ",None)
G["steps"]=(r"(?:สวัสดีค่ะ )?SIRI ?STUDIO ?PHOTO ร้านถ่ายรูปออนไลน์ ถ่ายง่าย ๆ ได้ทุกที่เพียง 2 ขั้นตอน 1\. แอดไลน์คลิก (?:https?://\S+)? ?(?:หรือพิมพ์ @siristudiophoto ในไลน์ได้เลย)? ?2\. สั่งรายละเอียด เลือกทรงผมเองได้ รอรับภาพได้เลย",
   "ถ่ายง่าย ๆ ได้ทุกที่ เพียง 2 ขั้นตอน\n1. แอดไลน์ @siristudiophoto\n2. สั่งรายละเอียด เลือกทรงผมเองได้\nรอรับภาพได้เลย")
G["online2"]=(r"รับตัดต่อเปลี่ยนชุด แต่งหน้า ทำผม จากรูปเดิม หรือรูปถ่ายจากโทรศัพท์มือถือก็สามารถทำได้ค่ะ ได้ไฟล์พร้อมปริ้นไม่ต้องไปถ่ายที่ร้านให้เสียเวลา(?: ไม่ต้องกลัวโควิด)? -ชุดครุย -ชุดข้าราชการ ยูนิฟอร์มทุกสถาบัน -ชุดนักเรียน นักศึกษา -ชุดสมัครงาน -สมัครสอบ ก พ ฟรี ไฟล์พร้อมปริ้นส์ 2 ขนาด และไฟล์ ก พ(?: ข้าราชการท้องถิ่น ตำรวจ)?(?: สำหรับท่าที่ต้องการสอบ หรือไฟล์อัพระบบขนาดต่าง ๆ ฟรี)?",
   "รับตัดต่อเปลี่ยนชุด แต่งหน้า ทำผม จากรูปเดิมหรือรูปถ่ายจากมือถือก็ทำได้ ได้ไฟล์พร้อมปริ้น ไม่ต้องไปถ่ายที่ร้านให้เสียเวลา\n- ชุดครุย\n- ชุดข้าราชการ ยูนิฟอร์มทุกสถาบัน\n- ชุดนักเรียน นักศึกษา\n- ชุดสมัครงาน\n- สมัครสอบ ก.พ.\nฟรี! ไฟล์พร้อมปริ้นส์ 2 ขนาด และไฟล์สำหรับสมัครสอบ ก.พ.")
SENT_ENDERS=r"(?<=นะคะ)|(?<=ค่ะ)|(?<=ค่า)|(?<=จ้า)|(?<=ครับ)|(?<=เลย)"
def clause_ok(c):
    c=c.strip()
    if len(c)<6: return False
    if STALE_RE.search(c): return False
    if re.search(r"รับงานตลอด|ไม่ค่อยได้เข้ามา|แอดมิน|ติดต่อ|สอบถาม|สั่งงาน|ไลน์|line|@|https?:|คิวอาร์|สแกน|แสกน|ลิ้?ง|ข้อความ|ตอบ(?!แทน)",c,re.I): return False
    if re.search(r"ก่อน/หลัง|วันนี้|พรุ่งนี้|ทยอย|จัดส่งเรียบร้อย|พัสดุ|รอบบ่าย|รอบค่ำ|ร้านหยุด|ปิดรับ",c): return False
    if re.match(r"^[ะาิีึืุูเแโใไ็่้๊๋ํ์]",c): return False   # starts mid-word
    return True
def intro_of(txt):
    # txt: flat text before the first good sentence, hashtags removed
    txt=re.sub(r"#\s*\S*","",txt)
    txt=re.sub(r"https?://\S+","",txt)
    txt=re.sub(r"@\s?\w+","",txt)
    txt=re.sub(r"\s+"," ",txt).strip()
    parts=[p for p in re.split(SENT_ENDERS,txt) if p.strip()]
    keep=[p.strip() for p in parts if clause_ok(p)]
    out=" ".join(keep).strip()
    out=fixes(out)
    out=re.sub(r"SIRI ?STUDIO ?PHOTO|SIRISTU ?DIO ?P\w*",BRAND,out,flags=re.I)
    if len(out)>180 or len(out)<8: return ""
    return tidy(out)
def build_old(text):
    t=strip_header(norm_ws(text))
    f=flat(t)
    hits=[]
    for k,(pat,rep) in G.items():
        for m in re.finditer(pat,f):
            hits.append((m.start(),m.end(),k,m.group(0),rep))
    if not hits: return None
    hits.sort()
    # drop overlaps (keep earliest/longest)
    sel=[];end=-1
    for h in hits:
        if h[0]>=end: sel.append(h); end=h[1]
    first=sel[0][0]
    intro=intro_of(f[:first])
    parts=[]
    if intro: parts.append(intro)
    seen=set()
    for s,e,k,txt_,rep in sel:
        if k in seen: continue
        seen.add(k)
        body=rep if rep else txt_
        if not rep:
            body=re.sub(r"#\s*","",body)           # inline hashtag marks become plain words
            body=re.sub(r"SIRI ?STUDIO ?PHOTO|SIRISTU ?DIO ?P\w*|SIRISTUDIO PHOTO",BRAND,body,flags=re.I)
            body=body.replace("รับตัดต่อภาพติดบัตรทุกชนิด","ตัดต่อภาพติดบัตรทุกชนิด") if body.startswith("รับตัดต่อภาพติดบัตรทุกชนิด") else body
        parts.append(body.strip())
    return "\n\n".join(parts),[k for *_,k,_,_ in sel],intro
