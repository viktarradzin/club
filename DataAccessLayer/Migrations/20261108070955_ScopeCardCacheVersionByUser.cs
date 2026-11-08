using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    public partial class ScopeCardCacheVersionByUser : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "card_cache_version",
                keyColumn: "id",
                keyValue: 1L);

            migrationBuilder.Sql("""
                INSERT INTO card_cache_version (id, version)
                SELECT id, 0 FROM users
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_card_cache_version_users_id",
                table: "card_cache_version",
                column: "id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_card_cache_version_users_id",
                table: "card_cache_version");

            migrationBuilder.Sql("DELETE FROM card_cache_version");

            migrationBuilder.InsertData(
                table: "card_cache_version",
                columns: new[] { "id", "version" },
                values: new object[] { 1L, 0L });
        }
    }
}
