START TRANSACTION;
ALTER TABLE "SCHEDULES" ADD repeat character varying(30) NOT NULL DEFAULT 'Recent';

INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
VALUES ('20261005140000_SchedulePostRepeat', '10.0.12');

COMMIT;

START TRANSACTION;
DELETE FROM "POSTS" p
USING "SOCIAL_ACCOUNTS" a
WHERE p.account_id = a.id
  AND a.device_id IS NULL
  AND a.name NOT LIKE 'Facebook · %';

INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
VALUES ('20261005150000_RemoveSamplePosts', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "COLLECTION_POSTS" ADD active boolean NOT NULL DEFAULT TRUE;

ALTER TABLE "COLLECTION_POSTS" ADD settings jsonb NOT NULL DEFAULT '{}';

CREATE TABLE "COLLECTION_MEMBERS" (
    collection_id uuid NOT NULL,
    post_id uuid NOT NULL,
    workspace_id uuid NOT NULL,
    added_at timestamp with time zone NOT NULL,
    CONSTRAINT pk_collection_members PRIMARY KEY (collection_id, post_id),
    CONSTRAINT fk_collection_members_collection_posts_post_id FOREIGN KEY (post_id) REFERENCES "COLLECTION_POSTS" (id) ON DELETE CASCADE,
    CONSTRAINT fk_collection_members_collections_collection_id FOREIGN KEY (collection_id) REFERENCES "POST_COLLECTIONS" (id) ON DELETE CASCADE,
    CONSTRAINT fk_collection_members_workspaces_workspace_id FOREIGN KEY (workspace_id) REFERENCES "WORKSPACES" (id) ON DELETE CASCADE
);

INSERT INTO "COLLECTION_MEMBERS" (collection_id, post_id, workspace_id, added_at)
SELECT collection_id, id, workspace_id, created_at FROM "COLLECTION_POSTS"

ALTER TABLE "COLLECTION_POSTS" DROP CONSTRAINT fk_collection_posts_collections_collection_id;

DROP INDEX ix_collection_posts_collection_id_created_at;

DROP INDEX ix_collection_posts_workspace_id;

ALTER TABLE "COLLECTION_POSTS" DROP COLUMN collection_id;

CREATE INDEX ix_collection_posts_workspace_id_created_at ON "COLLECTION_POSTS" (workspace_id, created_at);

CREATE INDEX ix_collection_members_post_id ON "COLLECTION_MEMBERS" (post_id);

CREATE INDEX ix_collection_members_workspace_id ON "COLLECTION_MEMBERS" (workspace_id);

INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
VALUES ('20261005160000_MasterPosts', '10.0.12');

COMMIT;

