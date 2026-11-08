using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class ScheduleCacheVersionConfiguration : IEntityTypeConfiguration<ScheduleCacheVersion>
{
    public void Configure(EntityTypeBuilder<ScheduleCacheVersion> builder)
    {
        builder.ToTable("schedule_cache_version");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Version).HasColumnName("version");
        builder.HasData(new ScheduleCacheVersion { Id = 1, Version = 0 });
    }
}
