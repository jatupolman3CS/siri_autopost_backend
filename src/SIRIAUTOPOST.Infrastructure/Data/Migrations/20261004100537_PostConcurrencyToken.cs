using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <summary>
    /// POSTS gets an optimistic concurrency token on PostgreSQL's row version. xmin is a system column every table
    /// already has, so there is nothing to create: the migration only moves the model snapshot forward (EF would
    /// otherwise generate an ADD COLUMN that PostgreSQL refuses).
    /// </summary>
    public partial class PostConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
