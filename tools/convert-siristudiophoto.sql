-- Converts old LINE ids to @siristudiophoto in the database and puts the new header on top of every post.
--   @970wfiou / teach_pp / @siristuiophoto      -> @siristudiophoto
--   https://lin.ee/c2I3lh4 (inside post texts)  -> https://line.me/R/ti/p/@siristudiophoto
--   campaign footer (extension settings)        -> the 3-line header, position 'top'
-- Run on SIRIAUTOPOST (dev) first, then SIRIAUTOPOST_PRD:
--   psql -d SIRIAUTOPOST -f tools/convert-siristudiophoto.sql
-- Read the counts of step 1, then the script updates inside one transaction. Replace COMMIT by ROLLBACK to test.

\echo '== 1. rows that still hold an old value =='
SELECT 'EXTENSION_CONFIGS' AS tbl, count(*) FROM "EXTENSION_CONFIGS" WHERE settings ~* '970wfiou|teach_pp|siristuiophoto|lin\.ee/c2I3lh4'
UNION ALL SELECT 'COLLECTION_POSTS', count(*) FROM "COLLECTION_POSTS" WHERE text ~* '970wfiou|teach_pp|siristuiophoto|lin\.ee/c2I3lh4'
UNION ALL SELECT 'SNIPPETS', count(*) FROM "SNIPPETS" WHERE text ~* '970wfiou|teach_pp|siristuiophoto|lin\.ee/c2I3lh4'
UNION ALL SELECT 'IMPORTED_POSTS', count(*) FROM "IMPORTED_POSTS" WHERE text ~* '970wfiou|teach_pp|siristuiophoto|lin\.ee/c2I3lh4'
UNION ALL SELECT 'POSTS (still queued)', count(*) FROM "POSTS" WHERE status = 'queued' AND content ~* '970wfiou|teach_pp|siristuiophoto|lin\.ee/c2I3lh4';

BEGIN;

-- Text conversion, applied in this order (the lin.ee link first so the header below is not touched).
CREATE TEMP TABLE _pairs(ord int, a text, b text) ON COMMIT DROP;
INSERT INTO _pairs VALUES
  (1, 'https://lin.ee/c2I3lh4', 'https://line.me/R/ti/p/@siristudiophoto'),
  (2, '@970wfiou',  '@siristudiophoto'),
  (3, '@teach_pp',  '@siristudiophoto'),
  (4, 'teach_pp',   '@siristudiophoto'),
  (5, '@siristuiophoto', '@siristudiophoto');

CREATE FUNCTION pg_temp.conv(t text) RETURNS text LANGUAGE plpgsql AS $$
DECLARE r record;
BEGIN
  FOR r IN SELECT a, b FROM _pairs ORDER BY ord LOOP
    t := replace(t, r.a, r.b);
  END LOOP;
  RETURN t;
END $$;

-- Library posts, snippets, imported posts, and posts still waiting in the queue (history is left alone).
-- Library posts are capped at 5,000 characters and the new link is longer: a post already at the cap
-- loses its last few characters again (they were cut at 5,000 when imported, too).
UPDATE "COLLECTION_POSTS" SET text = left(pg_temp.conv(text), 5000) WHERE text IS DISTINCT FROM left(pg_temp.conv(text), 5000);
UPDATE "SNIPPETS"         SET text = pg_temp.conv(text) WHERE text IS DISTINCT FROM pg_temp.conv(text);
UPDATE "IMPORTED_POSTS"   SET text = pg_temp.conv(text) WHERE text IS DISTINCT FROM pg_temp.conv(text);
UPDATE "POSTS"            SET content = pg_temp.conv(content)
 WHERE status = 'queued' AND content IS DISTINCT FROM pg_temp.conv(content);

-- Extension settings (the JSON of client/lib/shared.js): post texts and campaign footers.
-- New header = campaigns[*].config.footer, footerPosition 'top'. The revision goes up so devices pull it.
CREATE FUNCTION pg_temp.conv_settings(s jsonb) RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE
  hdr constant text := E'สั่งรูปออนไลน์ได้ด้วยตัวเอง ไม่ต้องไปร้าน : https://www.siristudiophoto.com\nดูผลงาน : https://www.siristudiophoto.com/index/portfolio\nสั่งงาน สอบถาม จองคิว คลิก https://lin.ee/c2I3lh4';
  i int;
BEGIN
  s := pg_temp.conv(s::text)::jsonb;                       -- post texts, group texts, anywhere
  IF jsonb_typeof(s->'campaigns') = 'array' THEN
    FOR i IN 0 .. jsonb_array_length(s->'campaigns') - 1 LOOP
      s := jsonb_set(s, ARRAY['campaigns', i::text, 'config', 'footer'], to_jsonb(hdr));
      s := jsonb_set(s, ARRAY['campaigns', i::text, 'config', 'footerPosition'], to_jsonb('top'::text));
    END LOOP;
  END IF;
  RETURN s;
END $$;

UPDATE "EXTENSION_CONFIGS"
   SET settings = pg_temp.conv_settings(settings::jsonb)::text, revision = revision + 1, updated_at = now(), updated_by_device = false
 WHERE settings <> '' AND settings::jsonb IS DISTINCT FROM pg_temp.conv_settings(settings::jsonb);

-- Legacy server table: only where it exists (the new API's database does not have it).
DO $do$
BEGIN
  IF to_regclass('"FBAP_PROFILES"') IS NOT NULL THEN
    EXECUTE $q$UPDATE "FBAP_PROFILES"
       SET settings = pg_temp.conv_settings(settings), revision = revision + 1, updated_at = now(), updated_by = 'convert-siristudiophoto'
     WHERE settings IS NOT NULL AND settings IS DISTINCT FROM pg_temp.conv_settings(settings)$q$;
  END IF;
END $do$;

\echo '== 2. rows that still hold an old value (all should be 0) =='
SELECT 'EXTENSION_CONFIGS' AS tbl, count(*) FROM "EXTENSION_CONFIGS" WHERE settings ~* '970wfiou|teach_pp|siristuiophoto'
UNION ALL SELECT 'COLLECTION_POSTS', count(*) FROM "COLLECTION_POSTS" WHERE text ~* '970wfiou|teach_pp|siristuiophoto'
UNION ALL SELECT 'SNIPPETS', count(*) FROM "SNIPPETS" WHERE text ~* '970wfiou|teach_pp|siristuiophoto'
UNION ALL SELECT 'IMPORTED_POSTS', count(*) FROM "IMPORTED_POSTS" WHERE text ~* '970wfiou|teach_pp|siristuiophoto'
UNION ALL SELECT 'POSTS (still queued)', count(*) FROM "POSTS" WHERE status = 'queued' AND content ~* '970wfiou|teach_pp|siristuiophoto';

COMMIT;

-- Collection footers (POST_COLLECTIONS.settings, owned jsonb) are not touched: check the key names first with
--   SELECT id, name, settings FROM "POST_COLLECTIONS";
-- and set the header there from the web app ("ชุดโพสต์" > settings) or with jsonb_set on that key.
