using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Evently.Modules.Events.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Add_Categories_TicketTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "category_id",
                schema: "events",
                table: "events",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "categories",
                schema: "events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_types",
                schema: "events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    price = table.Column<decimal>(type: "numeric", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_types", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_types_events_event_id",
                        column: x => x.event_id,
                        principalSchema: "events",
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_events_category_id",
                schema: "events",
                table: "events",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_types_event_id",
                schema: "events",
                table: "ticket_types",
                column: "event_id");

            migrationBuilder.AddForeignKey(
                name: "fk_events_categories_category_id",
                schema: "events",
                table: "events",
                column: "category_id",
                principalSchema: "events",
                principalTable: "categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_events_categories_category_id",
                schema: "events",
                table: "events");

            migrationBuilder.DropTable(
                name: "categories",
                schema: "events");

            migrationBuilder.DropTable(
                name: "ticket_types",
                schema: "events");

            migrationBuilder.DropIndex(
                name: "ix_events_category_id",
                schema: "events",
                table: "events");

            migrationBuilder.DropColumn(
                name: "category_id",
                schema: "events",
                table: "events");
        }
    }
}
