using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TenderScout.Infrastructure.Persistence;

public sealed class TenderScoutDbContext(DbContextOptions<TenderScoutDbContext> options) : DbContext(options)
{
    public DbSet<NoticeEntity> Notices => Set<NoticeEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureNotice(modelBuilder.Entity<NoticeEntity>());
    }

    private static void ConfigureNotice(EntityTypeBuilder<NoticeEntity> notice)
    {
        notice.ToTable("notices");

        notice.HasKey(n => n.Id).HasName("pk_notices");
        notice.Property(n => n.Id).HasColumnName("id").UseIdentityAlwaysColumn();

        notice.Property(n => n.PublicationNumber).HasColumnName("publication_number").HasMaxLength(32);
        notice.HasIndex(n => n.PublicationNumber).IsUnique().HasDatabaseName("ux_notices_publication_number");

        notice.Property(n => n.Title).HasColumnName("title");
        notice.Property(n => n.BuyerName).HasColumnName("buyer_name");
        notice.Property(n => n.MainCpvCode).HasColumnName("main_cpv_code").HasMaxLength(16);
        notice.Property(n => n.CpvCodes).HasColumnName("cpv_codes");
        notice.Property(n => n.PublicationDate).HasColumnName("publication_date");
        notice.HasIndex(n => n.PublicationDate).HasDatabaseName("ix_notices_publication_date");
        notice.Property(n => n.SubmissionDeadline).HasColumnName("submission_deadline");
        notice.Property(n => n.Link).HasColumnName("link");
        notice.Property(n => n.CreatedAt).HasColumnName("created_at");
        notice.Property(n => n.UpdatedAt).HasColumnName("updated_at");
    }
}
