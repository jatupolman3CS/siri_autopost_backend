using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Infrastructure.Data.Configurations;

public sealed class PostConfiguration : IEntityTypeConfiguration<Post>
{
    public void Configure(EntityTypeBuilder<Post> b)
    {
        b.ToTable("posts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Content).HasMaxLength(Post.MaxContentLength).IsRequired();
        b.Property(x => x.GroupUrl)
            .HasConversion(v => v.Value, v => GroupUrl.Create(v))
            .HasMaxLength(300)
            .IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.FailureReason).HasMaxLength(1000);
        b.HasIndex(x => x.Status);
    }
}
