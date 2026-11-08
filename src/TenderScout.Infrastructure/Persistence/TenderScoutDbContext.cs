using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TenderScout.Infrastructure.Persistence;

public sealed class TenderScoutDbContext(DbContextOptions<TenderScoutDbContext> options) : DbContext(options)
{
    public DbSet<NoticeEntity> Notices => Set<NoticeEntity>();

    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();

    public DbSet<DocumentChunkEntity> DocumentChunks => Set<DocumentChunkEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureNotice(modelBuilder.Entity<NoticeEntity>());
        ConfigureDocument(modelBuilder.Entity<DocumentEntity>());
        ConfigureDocumentChunk(modelBuilder.Entity<DocumentChunkEntity>());
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

    private static void ConfigureDocument(EntityTypeBuilder<DocumentEntity> document)
    {
        document.ToTable("documents");

        document.HasKey(d => d.Id).HasName("pk_documents");
        document.Property(d => d.Id).HasColumnName("id").UseIdentityAlwaysColumn();

        document.Property(d => d.DocId).HasColumnName("doc_id").HasMaxLength(64);
        document.HasIndex(d => d.DocId).IsUnique().HasDatabaseName("ux_documents_doc_id");

        // No foreign key to notices: most documents are older than the polling window.
        document.Property(d => d.PublicationNumber).HasColumnName("publication_number").HasMaxLength(32);
        document.HasIndex(d => d.PublicationNumber).HasDatabaseName("ix_documents_publication_number");

        document.Property(d => d.Kind).HasColumnName("kind").HasMaxLength(32);
        document.Property(d => d.Path).HasColumnName("path");
        document.Property(d => d.Sha256).HasColumnName("sha256").HasMaxLength(64);
        document.Property(d => d.SkipReason).HasColumnName("skip_reason").HasConversion<string>().HasMaxLength(32);
        document.Property(d => d.CreatedAt).HasColumnName("created_at");
        document.Property(d => d.UpdatedAt).HasColumnName("updated_at");

        document.HasMany(d => d.Chunks)
            .WithOne()
            .HasForeignKey(c => c.DocumentId)
            .HasConstraintName("fk_document_chunks_documents")
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureDocumentChunk(EntityTypeBuilder<DocumentChunkEntity> chunk)
    {
        chunk.ToTable("document_chunks");

        chunk.HasKey(c => c.Id).HasName("pk_document_chunks");
        chunk.Property(c => c.Id).HasColumnName("id").UseIdentityAlwaysColumn();

        chunk.Property(c => c.DocumentId).HasColumnName("document_id");
        chunk.Property(c => c.Position).HasColumnName("position");
        chunk.HasIndex(c => new { c.DocumentId, c.Position }).IsUnique().HasDatabaseName("ux_document_chunks_document_id_position");

        chunk.Property(c => c.PageNumber).HasColumnName("page_number");
        chunk.Property(c => c.Text).HasColumnName("text");
    }
}
