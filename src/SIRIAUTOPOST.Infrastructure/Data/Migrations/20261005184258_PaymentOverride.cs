using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PAYMENT_OVERRIDES",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    amount = table.Column<int>(type: "integer", nullable: false),
                    emails = table.Column<List<string>>(type: "text[]", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_overrides", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PAYMENT_OVERRIDES");
        }
    }
}
