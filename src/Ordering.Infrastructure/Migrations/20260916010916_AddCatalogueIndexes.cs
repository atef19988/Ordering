using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ordering.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_products_name",
                table: "products",
                columns: new[] { "name", "code" });

            migrationBuilder.CreateIndex(
                name: "ix_products_price",
                table: "products",
                columns: new[] { "price", "code" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_products_name",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_products_price",
                table: "products");
        }
    }
}
