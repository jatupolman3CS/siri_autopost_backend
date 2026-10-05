using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261005150000_RemoveSamplePosts")]
    public partial class RemoveSamplePosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // New workspaces used to get a week of made-up post history, a queue and error reports on their
            // sample accounts. A sample account has no browser (device_id is null) and is not an unbound
            // "Facebook · <browser>" account, which keeps its real history. Nothing real was ever posted from one.
            migrationBuilder.Sql("""
                DELETE FROM "POSTS" p
                USING "SOCIAL_ACCOUNTS" a
                WHERE p.account_id = a.id
                  AND a.device_id IS NULL
                  AND a.name NOT LIKE 'Facebook · %';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The deleted rows were sample data and are not restored.
        }
    }
}
