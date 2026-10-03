using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AutoPost.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fbap_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fbap_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fbap_users",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fbap_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fbap_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    key_hash = table.Column<string>(type: "text", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    state = table.Column<string>(type: "jsonb", nullable: true),
                    state_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fbap_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_fbap_devices_fbap_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "fbap_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "fbap_profile_images",
                columns: table => new
                {
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    image_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fbap_profile_images", x => new { x.profile_id, x.image_id });
                    table.ForeignKey(
                        name: "fk_fbap_profile_images_fbap_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "fbap_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fbap_device_commands",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cmd = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    args = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    done_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fbap_device_commands", x => x.id);
                    table.ForeignKey(
                        name: "fk_fbap_device_commands_fbap_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "fbap_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fbap_device_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    t = table.Column<long>(type: "bigint", nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    msg = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fbap_device_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_fbap_device_logs_fbap_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "fbap_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fbap_device_commands_device_id_status",
                table: "fbap_device_commands",
                columns: new[] { "device_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_fbap_device_logs_device_id_id",
                table: "fbap_device_logs",
                columns: new[] { "device_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_fbap_devices_key_hash",
                table: "fbap_devices",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fbap_devices_profile_id",
                table: "fbap_devices",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_fbap_users_username",
                table: "fbap_users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fbap_device_commands");

            migrationBuilder.DropTable(
                name: "fbap_device_logs");

            migrationBuilder.DropTable(
                name: "fbap_profile_images");

            migrationBuilder.DropTable(
                name: "fbap_users");

            migrationBuilder.DropTable(
                name: "fbap_devices");

            migrationBuilder.DropTable(
                name: "fbap_profiles");
        }
    }
}
