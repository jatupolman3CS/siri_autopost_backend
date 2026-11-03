"""Remove the SIRI_Promo_Poster media id from every post in a database. Dry run unless --commit.
Usage (PowerShell):
  $env:SIRI_DB_ENV='C:\ProjectAutoPost\siri_autopost_backend\backend\.env_prd'
  python remove_poster_prd.py --db SIRIAUTOPOST_PRD            # dry run
  python remove_poster_prd.py --db SIRIAUTOPOST_PRD --commit   # write
"""
import argparse, db

P = '476b8791-35c9-4304-bb81-57a1b7bbaed3'
a = argparse.ArgumentParser()
a.add_argument('--db', required=True)
a.add_argument('--commit', action='store_true')
a = a.parse_args()
c = db.connect(a.db)
cur = c.cursor()
for t in ('POSTS', 'COLLECTION_POSTS'):
    cur.execute(f'update "{t}" set media_ids=array_remove(media_ids,%s::uuid) where %s::uuid = any(media_ids)', (P, P))
    print(t, cur.rowcount)
cur.execute('update "MEDIA_FILES" set used_count=0 where id=%s', (P,))
(c.commit if a.commit else c.rollback)()
print('committed' if a.commit else 'dry run (rolled back)')
