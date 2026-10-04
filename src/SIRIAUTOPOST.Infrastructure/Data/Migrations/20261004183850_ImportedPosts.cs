using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportedPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IMPORTED_POSTS",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    link_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    media = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imported_posts", x => x.id);
                    table.ForeignKey(
                        name: "fk_imported_posts_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "WORKSPACES",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_imported_posts_workspace_id_posted_at",
                table: "IMPORTED_POSTS",
                columns: new[] { "workspace_id", "posted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_imported_posts_workspace_id_source_source_key",
                table: "IMPORTED_POSTS",
                columns: new[] { "workspace_id", "source", "source_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IMPORTED_POSTS");
        }
    }
}
