using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace TranslateFlow.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_routes",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    target_language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    input_price_per_million = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    output_price_per_million = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_routes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    routing_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "translation_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    source_language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    target_language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    input_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    result_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    character_count = table.Column<int>(type: "integer", nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_translation_jobs", x => x.id);
                });

            migrationBuilder.InsertData(
                table: "model_routes",
                columns: new[] { "id", "input_price_per_million", "model", "output_price_per_million", "source_language", "target_language" },
                values: new object[,]
                {
                    { 1, 0.30m, "qwen2.5:7b", 1.20m, "tr", "en" },
                    { 2, 0.30m, "qwen2.5:7b", 1.20m, "en", "tr" },
                    { 3, 0.25m, "qwen2.5:7b", 1.00m, "de", "en" },
                    { 4, 0.25m, "qwen2.5:7b", 1.00m, "en", "de" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_model_routes_source_language_target_language",
                table: "model_routes",
                columns: new[] { "source_language", "target_language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_created_at",
                table: "outbox_messages",
                column: "created_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_translation_jobs_email_created_at",
                table: "translation_jobs",
                columns: new[] { "email", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_routes");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "translation_jobs");
        }
    }
}
