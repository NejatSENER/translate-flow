using Microsoft.EntityFrameworkCore;
using TranslateFlow.Core.Domain;

namespace TranslateFlow.Core.Data;

public class TranslateFlowDbContext(DbContextOptions<TranslateFlowDbContext> options) : DbContext(options)
{
    public DbSet<TranslationJob> Jobs => Set<TranslationJob>();
    public DbSet<ModelRoute> ModelRoutes => Set<ModelRoute>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<TranslationJob>(e =>
        {
            e.ToTable("translation_jobs");
            e.Property(j => j.Email).HasMaxLength(254);
            e.Property(j => j.SourceLanguage).HasMaxLength(8);
            e.Property(j => j.TargetLanguage).HasMaxLength(8);
            e.Property(j => j.FileName).HasMaxLength(255);
            e.Property(j => j.InputKey).HasMaxLength(300);
            e.Property(j => j.ResultKey).HasMaxLength(300);
            e.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(j => j.Error).HasMaxLength(1000);
            e.Property(j => j.Model).HasMaxLength(100);
            e.Property(j => j.Cost).HasPrecision(18, 6);
            e.HasIndex(j => new { j.Email, j.CreatedAt });
        });

        model.Entity<ModelRoute>(e =>
        {
            e.HasIndex(r => new { r.SourceLanguage, r.TargetLanguage }).IsUnique();
            e.Property(r => r.SourceLanguage).HasMaxLength(8);
            e.Property(r => r.TargetLanguage).HasMaxLength(8);
            e.Property(r => r.Model).HasMaxLength(100);
            e.Property(r => r.InputPricePerMillion).HasPrecision(18, 6);
            e.Property(r => r.OutputPricePerMillion).HasPrecision(18, 6);
            e.HasData(
                new ModelRoute { Id = 1, SourceLanguage = "tr", TargetLanguage = "en", Model = "qwen2.5:7b", InputPricePerMillion = 0.30m, OutputPricePerMillion = 1.20m },
                new ModelRoute { Id = 2, SourceLanguage = "en", TargetLanguage = "tr", Model = "qwen2.5:7b", InputPricePerMillion = 0.30m, OutputPricePerMillion = 1.20m },
                new ModelRoute { Id = 3, SourceLanguage = "de", TargetLanguage = "en", Model = "qwen2.5:7b", InputPricePerMillion = 0.25m, OutputPricePerMillion = 1.00m },
                new ModelRoute { Id = 4, SourceLanguage = "en", TargetLanguage = "de", Model = "qwen2.5:7b", InputPricePerMillion = 0.25m, OutputPricePerMillion = 1.00m });
        });

        model.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.Property(o => o.Type).HasMaxLength(100);
            e.Property(o => o.RoutingKey).HasMaxLength(100);
            e.Property(o => o.Payload).HasColumnType("jsonb");
            e.Property(o => o.LastError).HasMaxLength(1000);
            e.HasIndex(o => o.CreatedAt).HasFilter("published_at IS NULL");
        });
    }
}
