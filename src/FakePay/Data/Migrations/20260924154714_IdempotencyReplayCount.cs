using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FakePay.Data.Migrations
{
    /// <inheritdoc />
    public partial class IdempotencyReplayCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReplayCount",
                schema: "fakepay",
                table: "IdempotencyKeys",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplayCount",
                schema: "fakepay",
                table: "IdempotencyKeys");
        }
    }
}
