using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MasterPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "active",
                table: "COLLECTION_POSTS",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "settings",
                table: "COLLECTION_POSTS",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "COLLECTION_MEMBERS",
                columns: table => new
                {
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_members", x => new { x.collection_id, x.post_id });
                    table.ForeignKey(
                        name: "fk_collection_members_collection_posts_post_id",
                        column: x => x.post_id,
                        principalTable: "COLLECTION_POSTS",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_collection_members_collections_collection_id",
                        column: x => x.collection_id,
                        principalTable: "POST_COLLECTIONS",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_collection_members_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "WORKSPACES",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Every post so far belonged to exactly one collection: that becomes its first membership, before the column goes.
            migrationBuilder.Sql("""
                INSERT INTO "COLLECTION_MEMBERS" (collection_id, post_id, workspace_id, added_at)
                SELECT collection_id, id, workspace_id, created_at FROM "COLLECTION_POSTS"
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_collection_posts_collections_collection_id",
                table: "COLLECTION_POSTS");

            migrationBuilder.DropIndex(
                name: "ix_collection_posts_collection_id_created_at",
                table: "COLLECTION_POSTS");

            migrationBuilder.DropIndex(
                name: "ix_collection_posts_workspace_id",
                table: "COLLECTION_POSTS");

            migrationBuilder.DropColumn(
                name: "collection_id",
                table: "COLLECTION_POSTS");

            migrationBuilder.CreateIndex(
                name: "ix_collection_posts_workspace_id_created_at",
                table: "COLLECTION_POSTS",
                columns: new[] { "workspace_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_members_post_id",
                table: "COLLECTION_MEMBERS",
                column: "post_id");

            migrationBuilder.CreateIndex(
                name: "ix_collection_members_workspace_id",
                table: "COLLECTION_MEMBERS",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_collection_posts_workspace_id_created_at",
                table: "COLLECTION_POSTS");

            // A post can only sit in one collection again: its oldest membership stays, a post in none is deleted.
            migrationBuilder.AddColumn<Guid>(
                name: "collection_id",
                table: "COLLECTION_POSTS",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "COLLECTION_POSTS" p SET collection_id = (
                    SELECT m.collection_id FROM "COLLECTION_MEMBERS" m WHERE m.post_id = p.id ORDER BY m.added_at, m.collection_id LIMIT 1)
                """);
            migrationBuilder.Sql("""DELETE FROM "COLLECTION_POSTS" WHERE collection_id IS NULL""");

            migrationBuilder.AlterColumn<Guid>(
                name: "collection_id",
                table: "COLLECTION_POSTS",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.DropTable(
                name: "COLLECTION_MEMBERS");

            migrationBuilder.DropColumn(
                name: "active",
                table: "COLLECTION_POSTS");

            migrationBuilder.DropColumn(
                name: "settings",
                table: "COLLECTION_POSTS");

            migrationBuilder.CreateIndex(
                name: "ix_collection_posts_collection_id_created_at",
                table: "COLLECTION_POSTS",
                columns: new[] { "collection_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_posts_workspace_id",
                table: "COLLECTION_POSTS",
                column: "workspace_id");

            migrationBuilder.AddForeignKey(
                name: "fk_collection_posts_collections_collection_id",
                table: "COLLECTION_POSTS",
                column: "collection_id",
                principalTable: "POST_COLLECTIONS",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
