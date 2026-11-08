using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    public partial class AddCardCacheVersion : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "card_cache_version",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_card_cache_version", x => x.id);
                });

            migrationBuilder.InsertData(
                table: "card_cache_version",
                columns: new[] { "id", "version" },
                values: new object[] { 1L, 0L });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "card_cache_version");
        }
    }
}
