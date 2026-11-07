using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TenderScout.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notices",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    publication_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    buyer_name = table.Column<string>(type: "text", nullable: true),
                    main_cpv_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    cpv_codes = table.Column<List<string>>(type: "text[]", nullable: false),
                    publication_date = table.Column<DateOnly>(type: "date", nullable: false),
                    submission_deadline = table.Column<DateOnly>(type: "date", nullable: true),
                    link = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notices", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notices_publication_date",
                table: "notices",
                column: "publication_date");

            migrationBuilder.CreateIndex(
                name: "ux_notices_publication_number",
                table: "notices",
                column: "publication_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notices");
        }
    }
}
