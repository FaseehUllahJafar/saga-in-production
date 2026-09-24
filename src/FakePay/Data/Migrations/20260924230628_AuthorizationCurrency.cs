using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FakePay.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuthorizationCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                schema: "fakepay",
                table: "Authorizations",
                type: "nchar(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "USD");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Currency",
                schema: "fakepay",
                table: "Authorizations");
        }
    }
}
