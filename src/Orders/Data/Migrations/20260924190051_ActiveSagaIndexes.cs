using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActiveSagaIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CheckoutSagas_Active",
                schema: "orders",
                table: "CheckoutSagas",
                column: "LastUpdatedUtc",
                filter: "[Status] IN ('InProgress', 'Compensating')")
                .Annotation("SqlServer:Include", new[] { "Status", "CurrentStep", "AttemptCount" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutSagas_NeedsAttention",
                schema: "orders",
                table: "CheckoutSagas",
                column: "Status",
                filter: "[Status] IN ('CompensationFailed', 'NeedsManualReview')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CheckoutSagas_Active",
                schema: "orders",
                table: "CheckoutSagas");

            migrationBuilder.DropIndex(
                name: "IX_CheckoutSagas_NeedsAttention",
                schema: "orders",
                table: "CheckoutSagas");
        }
    }
}
