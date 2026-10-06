"""Media step after cleanup_posts.py.

  python finish_media.py --db SIRIAUTOPOST --workspace <id> [--drop drop.json] [--sizes sizes.json] [--commit]

  --drop   json list of media ids that could not be repaired: taken off every post (the file stays in the library, switched off)
  --sizes  json {mediaId: newSizeInBytes} for images that were repaired in place (same R2 key): updates MEDIA_FILES.size
Dry run (rolled back) unless --commit."""
import argparse, json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import db


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", required=True)
    ap.add_argument("--workspace", required=True)
    ap.add_argument("--drop")
    ap.add_argument("--sizes")
    ap.add_argument("--active-only", action="store_true", help="take the --drop images only off posts that are switched on (posts that are off keep them)")
    ap.add_argument("--keep-files-on", action="store_true", help="do not switch the dropped files off in the media library")
    ap.add_argument("--commit", action="store_true")
    a = ap.parse_args()
    drop = json.load(open(a.drop)) if a.drop else []
    sizes = json.load(open(a.sizes)) if a.sizes else {}
    conn = db.connect(a.db); conn.autocommit = False; cur = conn.cursor()
    if drop:
        cur.execute('update "COLLECTION_POSTS" set media_ids = array(select m from unnest(media_ids) m where m <> all(%s::uuid[])), updated_at=now() '
                    'where workspace_id=%s and media_ids && %s::uuid[]' + (' and active' if a.active_only else ''), (drop, a.workspace, drop))
        print("posts that lost an unrepairable image:", cur.rowcount)
        if not a.keep_files_on:
            cur.execute('update "MEDIA_FILES" set active=false where workspace_id=%s and id = any(%s::uuid[])', (a.workspace, drop))
            print("library files switched off:", cur.rowcount)
    n = 0
    for mid, size in sizes.items():
        cur.execute('update "MEDIA_FILES" set size=%s where workspace_id=%s and id=%s', (int(size), a.workspace, mid)); n += cur.rowcount
    if sizes: print("sizes updated:", n)
    if a.commit: conn.commit(); print("COMMITTED")
    else: conn.rollback(); print("ROLLED BACK (dry run, add --commit to write)")


if __name__ == "__main__":
    main()
