using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class StripePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "cancel_at_period_end",
                table: "USERS",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "plan_renews_at",
                table: "USERS",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_customer_id",
                table: "USERS",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_subscription_id",
                table: "USERS",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "amount",
                table: "TRANSACTIONS",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "receipt_url",
                table: "TRANSACTIONS",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_invoice_id",
                table: "TRANSACTIONS",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_payment_intent_id",
                table: "TRANSACTIONS",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_refund_id",
                table: "TRANSACTIONS",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PAYMENT_EVENTS",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_events", x => x.key);
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_stripe_customer_id",
                table: "USERS",
                column: "stripe_customer_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_stripe_invoice_id",
                table: "TRANSACTIONS",
                column: "stripe_invoice_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_stripe_payment_intent_id",
                table: "TRANSACTIONS",
                column: "stripe_payment_intent_id");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_stripe_refund_id",
                table: "TRANSACTIONS",
                column: "stripe_refund_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_events_at",
                table: "PAYMENT_EVENTS",
                column: "at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PAYMENT_EVENTS");

            migrationBuilder.DropIndex(
                name: "ix_users_stripe_customer_id",
                table: "USERS");

            migrationBuilder.DropIndex(
                name: "ix_transactions_stripe_invoice_id",
                table: "TRANSACTIONS");

            migrationBuilder.DropIndex(
                name: "ix_transactions_stripe_payment_intent_id",
                table: "TRANSACTIONS");

            migrationBuilder.DropIndex(
                name: "ix_transactions_stripe_refund_id",
                table: "TRANSACTIONS");

            migrationBuilder.DropColumn(
                name: "cancel_at_period_end",
                table: "USERS");

            migrationBuilder.DropColumn(
                name: "plan_renews_at",
                table: "USERS");

            migrationBuilder.DropColumn(
                name: "stripe_customer_id",
                table: "USERS");

            migrationBuilder.DropColumn(
                name: "stripe_subscription_id",
                table: "USERS");

            migrationBuilder.DropColumn(
                name: "receipt_url",
                table: "TRANSACTIONS");

            migrationBuilder.DropColumn(
                name: "stripe_invoice_id",
                table: "TRANSACTIONS");

            migrationBuilder.DropColumn(
                name: "stripe_payment_intent_id",
                table: "TRANSACTIONS");

            migrationBuilder.DropColumn(
                name: "stripe_refund_id",
                table: "TRANSACTIONS");

            migrationBuilder.AlterColumn<int>(
                name: "amount",
                table: "TRANSACTIONS",
                type: "integer",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)",
                oldPrecision: 12,
                oldScale: 2);
        }
    }
}
