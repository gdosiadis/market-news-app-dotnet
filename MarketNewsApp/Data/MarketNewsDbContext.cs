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
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<EmailRecipient> EmailRecipients => Set<EmailRecipient>();
    public DbSet<ReportTemplateConfiguration> ReportTemplates => Set<ReportTemplateConfiguration>();
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();
    public DbSet<ConfigurationAuditEntry> ConfigurationAuditEntries => Set<ConfigurationAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScrapeSourceConfiguration>(entity =>
        {
            entity.HasIndex(source => source.Name).IsUnique();
            entity.Property(source => source.Name).HasMaxLength(200);
            entity.Property(source => source.Url).HasMaxLength(2048);
            entity.Property(source => source.WaitFor).HasMaxLength(500);
            entity.Property(source => source.FollowFirstLinkSelector).HasMaxLength(1000);
            entity.Property(source => source.SourceRegion).HasMaxLength(30);
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
        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.HasIndex(user => user.Username).IsUnique();
            entity.Property(user => user.Username).HasMaxLength(100);
            entity.Property(user => user.Role).HasMaxLength(50);
        });
        modelBuilder.Entity<EmailRecipient>(entity =>
        {
            entity.HasIndex(recipient => recipient.Address).IsUnique();
            entity.Property(recipient => recipient.Address).HasMaxLength(320);
            entity.Property(recipient => recipient.DisplayName).HasMaxLength(200);
        });
        modelBuilder.Entity<ReportTemplateConfiguration>(entity =>
        {
            entity.HasIndex(template => template.Name).IsUnique();
            entity.Property(template => template.Name).HasMaxLength(100);
            entity.Property(template => template.SubjectTemplate).HasMaxLength(500);
        });
        modelBuilder.Entity<ApplicationSetting>(entity =>
        {
            entity.HasIndex(setting => setting.Key).IsUnique();
            entity.Property(setting => setting.Key).HasMaxLength(100);
        });
        modelBuilder.Entity<ConfigurationAuditEntry>(entity =>
        {
            entity.HasIndex(entry => entry.OccurredAt);
            entity.Property(entry => entry.EntityType).HasMaxLength(100);
            entity.Property(entry => entry.EntityId).HasMaxLength(100);
            entity.Property(entry => entry.Action).HasMaxLength(30);
            entity.Property(entry => entry.Actor).HasMaxLength(100);
        });

        modelBuilder.Entity<ScrapeSourceConfiguration>().HasData(ConfigurationSeed.Sources);
        modelBuilder.Entity<PromptConfiguration>().HasData(ConfigurationSeed.Prompts);
        modelBuilder.Entity<EmailConfiguration>().HasData(ConfigurationSeed.Email);
        modelBuilder.Entity<SchedulingConfiguration>().HasData(ConfigurationSeed.Schedule);
        modelBuilder.Entity<AgentConfiguration>().HasData(ConfigurationSeed.Agent);
        modelBuilder.Entity<ReportConfiguration>().HasData(ConfigurationSeed.Report);
        modelBuilder.Entity<FeatureFlag>().HasData(ConfigurationSeed.Flags);
        modelBuilder.Entity<AdminUser>().HasData(ConfigurationSeed.AdminUser);
        modelBuilder.Entity<EmailRecipient>().HasData(ConfigurationSeed.Recipients);
        modelBuilder.Entity<ReportTemplateConfiguration>().HasData(ConfigurationSeed.ReportTemplate);
        modelBuilder.Entity<ApplicationSetting>().HasData(ConfigurationSeed.ApplicationSettings);
    }
}