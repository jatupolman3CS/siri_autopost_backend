"""Select, clean and re-write the library posts of one workspace (SIRI STUDIO PHOTO promo clean-up, 2026-10-05).

  python cleanup_posts.py --db SIRIAUTOPOST --workspace <id> --collection <id> --lead <poster-media-id> <video-media-id>
                          [--drop-media drop.json] [--repair-needed need.json] [--cap 10] [--report out.csv] [--commit]

Without --commit everything runs inside one transaction that is rolled back (a dry run that prints the numbers).
What it does, per post of the workspace (read from COLLECTION_POSTS, oldest first):
  * decides keep / switch off (rules in plan.py: only posts with an effective, evergreen sales message stay on);
  * a kept post gets a new text:  {{code}} line (the group's code, when it has one) + the 5-line promo block + body + hashtags;
  * a switched-off post keeps its text, only wrong contact strings are fixed (broken lin.ee link, typo'd / upper-case id,
    TikTok and old LIFF links);
  * every post gets the two lead files (poster, video) as its first media, own images follow (20 files at most);
  * the collection's page tag (the clickable @mention of the Facebook page) is set;
  * IMPORTED_POSTS texts get the same contact-string fix.
The decisions can be written to a CSV (--report).  Run it ONCE per database: a workspace whose posts already carry the promo
block is refused (the rules read the original texts), use finish_media.py for later media changes."""
import argparse, csv, json, os, re, sys, warnings, collections
warnings.filterwarnings("ignore")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import db, plan

PAGE_TAGS = "SIRI Studio Photo รับตัดต่อ รีทัช รูปติดบัตร รูปจบ สมัครงาน สมัครสอบ อัดรูป | https://www.facebook.com/KHRUSIRI"


def sanitize(t):
    t = re.sub(r"https://lin\.ee/c2I3(?!lh4)", "https://lin.ee/c2I3lh4", t)
    t = re.sub(r"@siristduiophoto", "@siristudiophoto", t, flags=re.I)
    t = re.sub(r"@siristudiophoto", "@siristudiophoto", t, flags=re.I)
    t = re.sub(r"https?://(?:vt\.)?tiktok\.com/\S*", "", t)
    t = re.sub(r"https://liff\.line\.me/\S+", "https://www.siristudiophoto.com", t)
    return t


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", required=True)
    ap.add_argument("--workspace", required=True)
    ap.add_argument("--collection", required=True)
    ap.add_argument("--lead", nargs=2, required=True, metavar=("POSTER_ID", "VIDEO_ID"))
    ap.add_argument("--drop-media", help="json list of media ids to take off every post (images that could not be repaired)")
    ap.add_argument("--repair-needed", help="json list of media ids whose image still needed repair (such posts rank last when capping)")
    ap.add_argument("--cap", type=int, default=10)
    ap.add_argument("--report")
    ap.add_argument("--commit", action="store_true")
    a = ap.parse_args()
    drop = set(json.load(open(a.drop_media))) if a.drop_media else set()
    need = set(json.load(open(a.repair_needed))) if a.repair_needed else set()
    lead = list(a.lead)
    conn = db.connect(a.db); conn.autocommit = False; cur = conn.cursor()
    cur.execute("select 1 from information_schema.columns where table_name='COLLECTION_POSTS' and column_name='active'")
    if not cur.fetchone():
        raise SystemExit("COLLECTION_POSTS has no 'active' column: this database is on the old schema. Apply the MasterPosts migration first.")
    cur.execute('select id,text,media_ids::text[],created_at,active from "COLLECTION_POSTS" where workspace_id=%s order by created_at,id', (a.workspace,))
    rows = cur.fetchall()
    done = sum(1 for r in rows if (r[1] or "").startswith("{{code}}\n" + plan.PROMO.split("\n")[0]))
    if done:
        raise SystemExit(f"{done} posts already carry the promo block: this workspace was cleaned before. "
                         "The rules read the original texts, so a second run would mix things up. Restore a backup first if you really want to start over.")
    posts = [{"id": str(r[0]), "text": r[1] or "", "media_ids": [str(m) for m in (r[2] or [])], "created_at": r[3].isoformat()} for r in rows]
    print("posts in workspace:", len(posts))
    res = []
    for i, p in enumerate(posts):
        r = plan.build(p); r["idx"] = i; r["id"] = p["id"]; res.append(r)
    res = plan.finalize(res, posts, need, cap=a.cap)
    upd = []; trimmed = 0
    for r in res:
        p = posts[r["idx"]]
        own = [m for m in p["media_ids"] if m not in drop and m not in lead]
        if len(own) > 20 - len(lead): own = own[: 20 - len(lead)]; trimmed += 1
        text = plan.final_text(r["body"], r["tags"]) if r["active"] else sanitize(p["text"])
        r["new_text"] = text
        r["own_media"] = len(own)
        upd.append((text, lead + own, bool(r["active"]), p["id"], a.workspace))
    cur.executemany('update "COLLECTION_POSTS" set text=%s, media_ids=%s::uuid[], active=%s, updated_at=now() where id=%s and workspace_id=%s', upd)
    cur.execute('update "POST_COLLECTIONS" set settings=jsonb_set(settings,%s,to_jsonb(%s::text)) where id=%s and workspace_id=%s',
                ("{PageTags}", PAGE_TAGS, a.collection, a.workspace))
    cur.execute('select id,text from "IMPORTED_POSTS" where workspace_id=%s', (a.workspace,))
    n = 0
    for i, t in cur.fetchall():
        s = sanitize(t or "")
        if s != t: cur.execute('update "IMPORTED_POSTS" set text=%s where id=%s', (s, i)); n += 1
    cur.execute('select count(*) from "COLLECTION_POSTS" where workspace_id=%s and %s::uuid = any(media_ids)', (a.workspace, lead[0]))
    k = cur.fetchone()[0]
    cur.execute('update "MEDIA_FILES" set used_count=%s where id = any(%s::uuid[])', (k, lead))
    c = collections.Counter(r["tier"].split("-")[0] if r["active"] else "off:" + r["code"] for r in res)
    print("active:", sum(r["active"] for r in res), "off:", sum(not r["active"] for r in res), dict(c))
    print("own images trimmed to fit 20 files:", trimmed, "| imported archive rows fixed:", n, "| lead files used by", k, "posts")
    if a.report:
        with open(a.report, "w", encoding="utf-8-sig", newline="") as f:
            w = csv.writer(f)
            w.writerow(["post_id", "original_date", "decision", "group", "reason", "own_images", "orig_chars", "new_chars", "original_text_start", "new_text_start"])
            for r in res:
                p = posts[r["idx"]]
                w.writerow([p["id"], p["created_at"][:10], "ใช้ (active)" if r["active"] else "ไม่ใช้ (inactive)",
                            r["tier"] if r["active"] else r["code"], r["reason"], r["own_media"], len(p["text"]), len(r["new_text"]),
                            p["text"][:120].replace("\n", " ⏎ "), r["body"][:120].replace("\n", " ⏎ ") if r["active"] else ""])
        print("report:", a.report)
    if a.commit: conn.commit(); print("COMMITTED")
    else: conn.rollback(); print("ROLLED BACK (dry run, add --commit to write)")


if __name__ == "__main__":
    main()
