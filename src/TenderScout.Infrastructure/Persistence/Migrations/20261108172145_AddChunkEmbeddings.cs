using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace TenderScout.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<Vector>(
                name: "embedding",
                table: "document_chunks",
                type: "vector(1024)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "embedding_model",
                table: "document_chunks",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_chunks_embedding_hnsw",
                table: "document_chunks",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_document_chunks_embedding_hnsw",
                table: "document_chunks");

            migrationBuilder.DropColumn(
                name: "embedding",
                table: "document_chunks");

            migrationBuilder.DropColumn(
                name: "embedding_model",
                table: "document_chunks");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
