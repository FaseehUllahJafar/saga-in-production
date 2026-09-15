using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "Reservations",
                schema: "inventory",
                columns: table => new
                {
                    SagaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservations", x => new { x.SagaId, x.OrderLineId });
                });

            migrationBuilder.CreateTable(
                name: "Steps",
                schema: "inventory",
                columns: table => new
                {
                    SagaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Step = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RejectedSku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Steps", x => new { x.SagaId, x.Step });
                });

            migrationBuilder.CreateTable(
                name: "Stock",
                schema: "inventory",
                columns: table => new
                {
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Available = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stock", x => x.Sku);
                    table.CheckConstraint("CK_Stock_Available_NonNegative", "[Available] >= 0");
                });

            migrationBuilder.InsertData(
                schema: "inventory",
                table: "Stock",
                columns: new[] { "Sku", "Available" },
                values: new object[,]
                {
                    { "BOOK-DDD", 1000 },
                    { "BOOK-EIP", 1000 },
                    { "LIMITED-01", 1 },
                    { "MUG-SAGA", 50 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Reservations",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Steps",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Stock",
                schema: "inventory");
        }
    }
}
