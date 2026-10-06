# Minimal R2 (S3 SigV4) client: list / get / put. Keys come from siri_autopost_backend/backend/.env (never printed).
import hmac,hashlib,datetime,urllib.request,urllib.parse,re,sys,os
def _env():
    e={}
    # SIRI_R2_ENV = a .env file holding R2__Endpoint, R2__BucketName, R2__AccessKeyId, R2__SecretAccessKey
    for l in open(os.environ.get("SIRI_R2_ENV", r"C:\ProjectAutoPost\siri_autopost_backend\backend\.env"),encoding="utf-8"):
        if "=" in l and not l.startswith("#"):
            k,v=l.rstrip("\n").split("=",1); e[k]=v
    return e
E=_env()
ENDPOINT=E["R2__Endpoint"].rstrip("/"); BUCKET=E["R2__BucketName"]; AK=E["R2__AccessKeyId"]; SK=E["R2__SecretAccessKey"]
HOST=urllib.parse.urlparse(ENDPOINT).netloc
def _sign(k,m): return hmac.new(k,m.encode(),hashlib.sha256).digest()
def req(method,key="",query="",body=b"",ctype=None):
    now=datetime.datetime.now(datetime.timezone.utc); amz=now.strftime("%Y%m%dT%H%M%SZ"); ds=now.strftime("%Y%m%d")
    path="/"+BUCKET+("/"+urllib.parse.quote(key,safe="/~") if key else "")
    ph=hashlib.sha256(body).hexdigest()
    hdr={"host":HOST,"x-amz-content-sha256":ph,"x-amz-date":amz}
    if ctype: hdr["content-type"]=ctype
    sh=";".join(sorted(hdr)); ch="".join(f"{k}:{hdr[k]}\n" for k in sorted(hdr))
    cq="&".join(sorted(query.split("&"))) if query else ""
    cr=f"{method}\n{path}\n{cq}\n{ch}\n{sh}\n{ph}"
    scope=f"{ds}/auto/s3/aws4_request"
    sts=f"AWS4-HMAC-SHA256\n{amz}\n{scope}\n{hashlib.sha256(cr.encode()).hexdigest()}"
    k=_sign(_sign(_sign(_sign(("AWS4"+SK).encode(),ds),"auto"),"s3"),"aws4_request")
    sig=hmac.new(k,sts.encode(),hashlib.sha256).hexdigest()
    h={"x-amz-content-sha256":ph,"x-amz-date":amz,"Authorization":f"AWS4-HMAC-SHA256 Credential={AK}/{scope}, SignedHeaders={sh}, Signature={sig}"}
    if ctype: h["Content-Type"]=ctype
    r=urllib.request.Request(ENDPOINT+path+("?"+query if query else ""),data=body if method in("PUT","POST") else None,method=method,headers=h)
    return urllib.request.urlopen(r,timeout=120)
def get(key): return req("GET",key).read()
def put(key,data,ctype): return req("PUT",key,body=data,ctype=ctype).status
def head(key): return req("HEAD",key).headers
def list_keys(prefix=""):
    out=[];tok=None
    while True:
        q="list-type=2&prefix="+urllib.parse.quote(prefix,safe="")+("&continuation-token="+urllib.parse.quote(tok,safe="") if tok else "")
        x=req("GET","",q).read().decode()
        out+=re.findall(r"<Key>(.*?)</Key>",x)
        m=re.search(r"<NextContinuationToken>(.*?)</NextContinuationToken>",x)
        if re.search(r"<IsTruncated>true",x) and m: tok=m.group(1)
        else: break
    return out
if __name__=="__main__":
    ks=list_keys(sys.argv[1] if len(sys.argv)>1 else "")
    print(len(ks)); print("\n".join(ks[:8]))
