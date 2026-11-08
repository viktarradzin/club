using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class EnrollmentCardCacheVersionConfiguration
    : IEntityTypeConfiguration<EnrollmentCardCacheVersion>
{
    public void Configure(EntityTypeBuilder<EnrollmentCardCacheVersion> builder)
    {
        builder.ToTable("card_cache_version");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(x => x.Version)
            .HasColumnName("version");

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<EnrollmentCardCacheVersion>(x => x.Id);
    }
}
