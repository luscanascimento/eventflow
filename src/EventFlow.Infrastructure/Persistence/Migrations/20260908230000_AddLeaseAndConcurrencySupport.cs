using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseAndConcurrencySupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "lock_token",
                table: "outbox_messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "locked_until_utc",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lock_token",
                table: "processed_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "locked_until_utc",
                table: "processed_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "processed_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Completed");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_status_next_retry_at_created_at",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_status_locked_retry_created",
                table: "outbox_messages",
                columns: new[] { "status", "locked_until_utc", "next_retry_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_processed_events_status_locked_until",
                table: "processed_events",
                columns: new[] { "status", "locked_until_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_status_locked_retry_created",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_processed_events_status_locked_until",
                table: "processed_events");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "locked_until_utc",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "processed_events");

            migrationBuilder.DropColumn(
                name: "locked_until_utc",
                table: "processed_events");

            migrationBuilder.DropColumn(
                name: "status",
                table: "processed_events");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_status_next_retry_at_created_at",
                table: "outbox_messages",
                columns: new[] { "status", "next_retry_at", "created_at" });
        }
    }
}
