using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    public partial class AddScheduleIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_classes_room_id_starts_at_id",
                table: "classes",
                columns: new[] { "room_id", "starts_at", "id" },
                filter: "NOT is_cancelled");

            migrationBuilder.CreateIndex(
                name: "IX_classes_starts_at_id",
                table: "classes",
                columns: new[] { "starts_at", "id" },
                filter: "NOT is_cancelled");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_classes_room_id_starts_at_id",
                table: "classes");

            migrationBuilder.DropIndex(
                name: "IX_classes_starts_at_id",
                table: "classes");
        }
    }
}
