using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.CreateTable(
                name: "CheckoutSagas",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CardToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ShippingAddress = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CurrentStep = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    AwaitingInquiry = table.Column<bool>(type: "bit", nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    AuthorizationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TrackingNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CaptureId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Journal = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Lines = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckoutSagas", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckoutSagas",
                schema: "orders");
        }
    }
}
