using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DataAccessLayer.Migrations
{
    public partial class AddIdempotencyRequests : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idemp_req",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    class_id = table.Column<long>(type: "bigint", nullable: false),
                    seats = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idemp_req", x => x.id);
                    table.ForeignKey(
                        name: "FK_idemp_req_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_idemp_req_user_id_operation_key_hash",
                table: "idemp_req",
                columns: new[] { "user_id", "operation", "key_hash" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idemp_req");
        }
    }
}
