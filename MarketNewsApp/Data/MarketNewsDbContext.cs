using Microsoft.EntityFrameworkCore;

namespace MarketNewsApp.Data;

public sealed class MarketNewsDbContext(DbContextOptions<MarketNewsDbContext> options) : DbContext(options)
{
    public DbSet<ScrapeSourceConfiguration> ScrapeSources => Set<ScrapeSourceConfiguration>();
    public DbSet<PromptConfiguration> Prompts => Set<PromptConfiguration>();
    public DbSet<EmailConfiguration> EmailSettings => Set<EmailConfiguration>();
    public DbSet<SchedulingConfiguration> SchedulingSettings => Set<SchedulingConfiguration>();
    public DbSet<AgentConfiguration> AgentSettings => Set<AgentConfiguration>();
    public DbSet<ReportConfiguration> ReportSettings => Set<ReportConfiguration>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScrapeSourceConfiguration>(entity =>
        {
            entity.HasIndex(source => source.Name).IsUnique();
            entity.Property(source => source.Name).HasMaxLength(200);
            entity.Property(source => source.Url).HasMaxLength(2048);
            entity.Property(source => source.WaitFor).HasMaxLength(500);
            entity.Property(source => source.FollowFirstLinkSelector).HasMaxLength(1000);
        });
        modelBuilder.Entity<PromptConfiguration>(entity =>
        {
            entity.HasIndex(prompt => prompt.Key).IsUnique();
            entity.Property(prompt => prompt.Key).HasMaxLength(100);
        });
        modelBuilder.Entity<FeatureFlag>(entity =>
        {
            entity.HasIndex(flag => flag.Key).IsUnique();
            entity.Property(flag => flag.Key).HasMaxLength(100);
        });
        modelBuilder.Entity<EmailConfiguration>().Property(setting => setting.Recipients).HasMaxLength(4000);
        modelBuilder.Entity<EmailConfiguration>().Property(setting => setting.FromDisplayName).HasMaxLength(200);
        modelBuilder.Entity<EmailConfiguration>().Property(setting => setting.SubjectTemplate).HasMaxLength(500);
        modelBuilder.Entity<SchedulingConfiguration>().Property(setting => setting.DailySendTime).HasMaxLength(5);
        modelBuilder.Entity<AgentConfiguration>().Property(setting => setting.Provider).HasMaxLength(30);
        modelBuilder.Entity<AgentConfiguration>().Property(setting => setting.CopilotModel).HasMaxLength(200);
        modelBuilder.Entity<AgentConfiguration>().Property(setting => setting.AzureEndpoint).HasMaxLength(2048);
        modelBuilder.Entity<AgentConfiguration>().Property(setting => setting.AzureDeployment).HasMaxLength(200);
        modelBuilder.Entity<AgentConfiguration>().Property(setting => setting.AzureApiVersion).HasMaxLength(30);

        modelBuilder.Entity<ScrapeSourceConfiguration>().HasData(ConfigurationSeed.Sources);
        modelBuilder.Entity<PromptConfiguration>().HasData(ConfigurationSeed.Prompts);
        modelBuilder.Entity<EmailConfiguration>().HasData(ConfigurationSeed.Email);
        modelBuilder.Entity<SchedulingConfiguration>().HasData(ConfigurationSeed.Schedule);
        modelBuilder.Entity<AgentConfiguration>().HasData(ConfigurationSeed.Agent);
        modelBuilder.Entity<ReportConfiguration>().HasData(ConfigurationSeed.Report);
        modelBuilder.Entity<FeatureFlag>().HasData(ConfigurationSeed.Flags);
    }
}