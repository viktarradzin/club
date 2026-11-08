using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public sealed class IdempotencyRequestConfiguration : IEntityTypeConfiguration<IdempotencyRequest>
{
    public void Configure(EntityTypeBuilder<IdempotencyRequest> builder)
    {
        builder.ToTable("idemp_req");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.Operation).HasColumnName("operation").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Key).HasColumnName("key").UseCollation("C").IsRequired();
        builder.Property(x => x.KeyHash).HasColumnName("key_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ClassId).HasColumnName("class_id");
        builder.Property(x => x.Seats).HasColumnName("seats");
        builder.Property(x => x.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");

        builder.HasIndex(x => new { x.UserId, x.Operation, x.KeyHash });

        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
    }
}
