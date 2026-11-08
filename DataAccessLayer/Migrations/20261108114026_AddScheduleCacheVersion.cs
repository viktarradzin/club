using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    public partial class AddScheduleCacheVersion : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "schedule_cache_version",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_cache_version", x => x.id);
                });

            migrationBuilder.InsertData(
                table: "schedule_cache_version",
                columns: new[] { "id", "version" },
                values: new object[] { 1, 0L });

            migrationBuilder.Sql("""
                CREATE FUNCTION increment_schedule_cache_version() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    UPDATE schedule_cache_version SET version = version + 1 WHERE id = 1;
                    RETURN NULL;
                END;
                $$;

                CREATE TRIGGER classes_schedule_cache_version
                AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON classes
                FOR EACH STATEMENT EXECUTE FUNCTION increment_schedule_cache_version();

                CREATE TRIGGER rooms_schedule_cache_version
                AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON rooms
                FOR EACH STATEMENT EXECUTE FUNCTION increment_schedule_cache_version();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER rooms_schedule_cache_version ON rooms;
                DROP TRIGGER classes_schedule_cache_version ON classes;
                DROP FUNCTION increment_schedule_cache_version();
                """);

            migrationBuilder.DropTable(
                name: "schedule_cache_version");
        }
    }
}
