using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_media_files_workspace_id",
                table: "MEDIA_FILES");

            migrationBuilder.AddColumn<Guid>(
                name: "folder_id",
                table: "MEDIA_FILES",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MEDIA_FOLDERS",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_folders", x => x.id);
                    table.ForeignKey(
                        name: "fk_media_folders_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "WORKSPACES",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_files_workspace_id_folder_id",
                table: "MEDIA_FILES",
                columns: new[] { "workspace_id", "folder_id" });

            migrationBuilder.CreateIndex(
                name: "ix_media_folders_workspace_id",
                table: "MEDIA_FOLDERS",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MEDIA_FOLDERS");

            migrationBuilder.DropIndex(
                name: "ix_media_files_workspace_id_folder_id",
                table: "MEDIA_FILES");

            migrationBuilder.DropColumn(
                name: "folder_id",
                table: "MEDIA_FILES");

            migrationBuilder.CreateIndex(
                name: "ix_media_files_workspace_id",
                table: "MEDIA_FILES",
                column: "workspace_id");
        }
    }
}
