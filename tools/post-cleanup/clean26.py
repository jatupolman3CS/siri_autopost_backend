import re
from textclean import norm_ws,fixes,tidy
HANDLE=r"@\s?siri\w*"
URLR=r"https?://\S+"
META_CUT=["สำหรับ มหาวิทยาลัยเฉลิมกาญจนา"]
EMOJI=r"[\U0001F300-\U0001FAFF☀-➿️‍]"
TRAIL=r"\s*(ชุดครุย|ข้าราชการท้องถิ่น|ชุดนักศึกษาสถาบันการจัดการปัญญาภิวัฒน์)\s*$"

def clean26(text):
    t=norm_ws(text)
    for m in META_CUT:
        i=t.find(m)
        if i>=0: t=t[:i]
    # everything from the first hashtag on is hashtags / keyword tail
    if re.search(r"\s#",t):
        t=re.sub(r"\s#\s?[^\s#]*.*$","",t,flags=re.S)
    t=re.sub(r"^#.*$","",t,flags=re.M)
    # calls to action that carry the old link/handle: the promo block above the post replaces them
    t=re.sub(r"(?:📩 ?)?สนใจสั่งงาน(?:ได้ในไลน์)?\s*พิมพ์ ?"+HANDLE+r"(?: ?ค่ะ)?\s*\(มี ?@ ?\)(?:\s*หรือ ?คลิก ?"+URLR+r")?(?: ?ได้เลยครับผม)?","",t)
    t=re.sub(r"(?:📩 ?)?สนใจสั่งงาน\s*\n?\s*พิมพ์ ?"+HANDLE+r"\s*\(มี ?@ ?\)\s*(?:หรือ ?คลิก ?"+URLR+r")?","",t)
    t=re.sub(r"สั่งงาน(?:ง่าย)?ผ่านไลน์(?:หรือเว็บ)?(?:ได้เลย)? ?"+URLR,"สั่งงานผ่านไลน์ได้เลย",t)
    t=re.sub(r"สนใจคลิก ?"+URLR,"",t)
    t=re.sub(URLR,"",t)
    t=re.sub(HANDLE,"",t)
    t=fixes(t)
    t=t.replace("ใบหน้าแต่งหน้า","ใบหน้า แต่งหน้า").replace("บุคคลากร","บุคลากร")
    t=re.sub(r"แน่นอนว\s*$","แน่นอนค่ะ",t)
    t=re.sub(r"แน่นอนว(?=\s)","แน่นอนค่ะ",t)
    t=re.sub(r"^ขอบพระคุณลูกค้าชุดครุยป\.โท ชุดกากี(?=🤍)","",t)
    t=re.sub(r"(เดี๋ยวเราดูแลให้ค่ะ)\s+ชุด.*$",r"\1",t)
    t=t.replace("รูปเชิ้ตขาว เสื้อขาว เสื้อคอปก ชุดสูท","")
    t=re.sub(r"สอบ ?กพ","สอบ ก.พ.",t)
    t=re.sub(r"รูปสมัครสอบกพ","รูปสมัครสอบ ก.พ.",t)
    lines=[l.strip() for l in t.split("\n")]
    lines=[l for l in lines if l not in ("✨","🤍","\"",".")]
    out=[]
    for l in lines:
        # keyword-list lines (a run of "ชุด..." words, no sentence): keep only a hook before the run
        if l.count("ชุด")>=6 and not re.search(r"[!?]|ค่ะ|นะคะ",l):
            m=re.match(r"^(.*?)(?:\s+ชุด\S+){5,}.*$",l)
            if m and len(m.group(1).strip())>=8: out.append(m.group(1).strip())
            continue
        out.append(l)
    t="\n".join(out)
    t=re.sub(r"\n{3,}","\n\n",t)
    t=re.sub(r"(ได้เลย|ค่ะ|ค่า|ครับ)(?=ชุด|สนใจ)",r"\1 ",t)
    t=re.sub(r"[\"“”]\s*$","",t)                       # stray quote
    t=tidy(t)
    t=re.sub(TRAIL,"",t)
    t=tidy(t)
    # readability: a one-paragraph text gets a break after its hook sentence
    if "\n" not in t and len(t)>200:
        t=re.sub(r"^(.{15,}?[!?](?: ?"+EMOJI+r"+)*) +",r"\1\n\n",t,count=1)
    return t
