using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    public partial class BackfillEnrollmentRefundedVisits : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE enrollments AS e
                SET refunded_visits = CASE
                    WHEN c.is_cancelled
                        OR e.cancelled_at <= c.starts_at - INTERVAL '2 hours'
                    THEN e.seats
                    ELSE 0
                END
                FROM classes AS c
                WHERE e.class_id = c.id
                  AND e.status = 'cancelled'
                  AND e.refunded_visits IS NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
