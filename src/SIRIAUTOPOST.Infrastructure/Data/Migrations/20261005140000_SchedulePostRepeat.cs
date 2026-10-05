using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261005140000_SchedulePostRepeat")]
    public partial class SchedulePostRepeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every schedule so far avoided the group's last posts: that is "Recent".
            migrationBuilder.AddColumn<string>(
                name: "repeat",
                table: "SCHEDULES",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Recent");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "repeat",
                table: "SCHEDULES");
        }
    }
}
