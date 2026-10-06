"""Bring the cleaned SIRI STUDIO PHOTO workspace from the dev database to the production database.

  python copy_to_prd.py --src SIRIAUTOPOST --dst SIRIAUTOPOST_PRD --workspace <id> --migration prd_migration.sql [--commit]

Everything runs in ONE transaction on the destination: the three migrations (SchedulePostRepeat, RemoveSamplePosts,
MasterPosts: the pipeline-neutral SQL of `dotnet ef migrations script 20261005130000_ActiveAndRename 20261005160000_MasterPosts`)
and then the data. Any error, or no --commit, rolls everything back.
Refuses to run unless the destination holds no users and no workspaces and sits exactly at the migration before these three.

What is copied (columns the destination knows only): the workspace's owner and its members' users, the workspace, its members,
media folders and media rows (files stay in the shared R2 bucket), collections, library posts and their memberships, link sets and
links, snippets, imported archive posts.
What is NOT copied: devices, social accounts, schedules, posts history, extension settings, billing rows. Changed on the way:
Stripe ids of the users are cleared (they belong to the test account), notification tokens are cleared, link sets lose the
account ids (accounts are not copied)."""
import argparse, io, os, re, sys, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import db

PRE_MIGRATION = "20261005130000_ActiveAndRename"
POST_MIGRATION = "20261005160000_MasterPosts"
# (table, where-clause on the source, parameters)
def plan(ws):
    return [
        ("USERS", 'id in (select owner_id from "WORKSPACES" where id=%s union select user_id from "WORKSPACE_MEMBERS" where workspace_id=%s and user_id is not null)', (ws, ws)),
        ("WORKSPACES", "id=%s", (ws,)),
        ("WORKSPACE_MEMBERS", "workspace_id=%s", (ws,)),
        ("MEDIA_FOLDERS", "workspace_id=%s", (ws,)),
        ("MEDIA_FILES", "workspace_id=%s", (ws,)),
        ("POST_COLLECTIONS", "workspace_id=%s", (ws,)),
        ("COLLECTION_POSTS", "workspace_id=%s", (ws,)),
        ("COLLECTION_MEMBERS", "workspace_id=%s", (ws,)),
        ("LINK_SETS", "workspace_id=%s", (ws,)),
        ("SET_LINKS", "workspace_id=%s", (ws,)),
        ("SNIPPETS", "workspace_id=%s", (ws,)),
        ("IMPORTED_POSTS", "workspace_id=%s", (ws,)),
    ]


def columns(cur, table):
    cur.execute("select column_name from information_schema.columns where table_schema='public' and table_name=%s order by ordinal_position", (table,))
    return [r[0] for r in cur.fetchall()]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True); ap.add_argument("--dst", required=True)
    ap.add_argument("--workspace", required=True); ap.add_argument("--migration", required=True)
    ap.add_argument("--commit", action="store_true")
    a = ap.parse_args()
    if a.src == a.dst: raise SystemExit("source and destination are the same database")
    src = db.connect(a.src); sc = src.cursor()
    dst = db.connect(a.dst); dst.autocommit = False; dc = dst.cursor()

    # ---- preflight on the destination ----
    dc.execute('select migration_id from "__EFMigrationsHistory" order by migration_id desc limit 1')
    last = dc.fetchone()[0]
    if last != PRE_MIGRATION: raise SystemExit(f"destination is at {last}, expected {PRE_MIGRATION}")
    for t in ("USERS", "WORKSPACES", "COLLECTION_POSTS", "MEDIA_FILES"):
        dc.execute(f'select count(*) from "{t}"'); n = dc.fetchone()[0]
        if n: raise SystemExit(f'destination {t} is not empty ({n} rows): refusing to copy over existing data')
    print("preflight ok: destination at", last, "and empty")

    # ---- schema: the three migrations, one transaction ----
    sql = open(a.migration, encoding="utf-8-sig").read()   # EF writes a BOM
    sql = re.sub(r"^(START TRANSACTION;|COMMIT;)\s*$", "", sql, flags=re.M)
    sql = re.sub(r'(FROM "COLLECTION_POSTS")\s*\n\s*\n(ALTER TABLE)', r"\1;\n\n\2", sql)   # EF leaves this statement unterminated
    dc.execute(sql)
    dc.execute('select migration_id from "__EFMigrationsHistory" order by migration_id desc limit 1')
    if dc.fetchone()[0] != POST_MIGRATION: raise SystemExit("migrations did not end at " + POST_MIGRATION)
    print("schema migrated to", POST_MIGRATION)

    # ---- data ----
    counts = {}
    for table, where, params in plan(a.workspace):
        scols = columns(sc, table); dcols = columns(dc, table)
        cols = [c for c in dcols if c in scols]
        missing = [c for c in dcols if c not in scols]
        if missing: raise SystemExit(f"{table}: destination has columns the source lacks: {missing}")
        lst = ", ".join(f'"{c}"' for c in cols)
        buf = io.StringIO()
        sc.copy_expert(cur_sql(sc, f'COPY (SELECT {lst} FROM "{table}" WHERE {where}) TO STDOUT', params), buf)
        buf.seek(0)
        dc.copy_expert(f'COPY "{table}" ({lst}) FROM STDIN', buf)
        dc.execute(f'select count(*) from "{table}"'); counts[table] = dc.fetchone()[0]
        sc.execute(f'select count(*) from "{table}" WHERE {where}', params); expected = sc.fetchone()[0]
        if counts[table] != expected: raise SystemExit(f"{table}: copied {counts[table]} of {expected}")
        print(f"  {table:20} {counts[table]:>6} rows")
    # things that belong to the source environment
    dc.execute('update "USERS" set stripe_customer_id=null, stripe_subscription_id=null')
    dc.execute('update "LINK_SETS" set post_as_account_id=null, account_ids=\'{}\'')
    dc.execute('select notifications from "WORKSPACES"')
    for (n,) in dc.fetchall():
        txt = json.dumps(n)
        print("  notifications settings hold", len(re.findall(r'"(?:Token|BotToken|AccessToken)"\s*:\s*"[^"]+', txt)), "non-empty token(s) before clearing")
    dc.execute("""update "WORKSPACES" set notifications = (
        select coalesce(jsonb_object_agg(k, case when k ~* 'token' then '""'::jsonb else v end), '{}'::jsonb) from jsonb_each(notifications) as t(k, v))""")
    # sanity: no leftover references the destination cannot resolve
    dc.execute('select count(*) from "COLLECTION_MEMBERS" m left join "COLLECTION_POSTS" p on p.id=m.post_id where p.id is null'); assert dc.fetchone()[0] == 0
    dc.execute('select count(*) from "COLLECTION_POSTS" p where not exists (select 1 from "COLLECTION_MEMBERS" m where m.post_id=p.id)'); orphans = dc.fetchone()[0]
    print("library posts that sit in no collection:", orphans)
    if a.commit: dst.commit(); print("COMMITTED to", a.dst)
    else: dst.rollback(); print("ROLLED BACK (dry run, add --commit to write to", a.dst + ")")


def cur_sql(cur, sql, params):
    return cur.mogrify(sql, params).decode()


if __name__ == "__main__":
    main()
