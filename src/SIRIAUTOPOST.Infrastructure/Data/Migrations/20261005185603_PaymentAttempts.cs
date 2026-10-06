using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PAYMENT_ATTEMPTS",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stripe_payment_intent_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    stripe_subscription_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    flow = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    requested_method = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    paid_method = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    plan = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    cycle = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    promo_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    failure_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_attempts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "USERS",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_stripe_payment_intent_id",
                table: "PAYMENT_ATTEMPTS",
                column: "stripe_payment_intent_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_user_id_created_at",
                table: "PAYMENT_ATTEMPTS",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PAYMENT_ATTEMPTS");
        }
    }
}
