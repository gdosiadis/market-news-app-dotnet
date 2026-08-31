namespace MarketNewsApp.Data;

public sealed class ScrapeSourceConfiguration
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public required string SelectorsJson { get; set; }
    public required string WaitFor { get; set; }
    public int TimeoutMs { get; set; }
    public int ExtraSettleMs { get; set; }
    public string? ExpandButtonTextsJson { get; set; }
    public string? ExcludeSelectorsJson { get; set; }
    public string? ScreenshotSelectorsJson { get; set; }
    public string? FollowFirstLinkSelector { get; set; }
    public string SourceRegion { get; set; } = "International";
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
}

public sealed class PromptConfiguration
{
    public int Id { get; set; }
    public required string Key { get; set; }
    public required string Template { get; set; }
    public bool IsEnabled { get; set; }
}

public sealed class EmailConfiguration
{
    public int Id { get; set; }
    public required string Recipients { get; set; }
    public required string FromDisplayName { get; set; }
    public required string SubjectTemplate { get; set; }
}

public sealed class SchedulingConfiguration
{
    public int Id { get; set; }
    public required string DailySendTime { get; set; }
    public bool IsEnabled { get; set; }
}

public sealed class AgentConfiguration
{
    public int Id { get; set; }
    public required string Provider { get; set; }
    public string? CopilotModel { get; set; }
    public string? AzureEndpoint { get; set; }
    public string? AzureDeployment { get; set; }
    public string? AzureApiVersion { get; set; }
}

public sealed class ReportConfiguration
{
    public int Id { get; set; }
    public int LookbackDays { get; set; }
    public int MaxSummarySourceCharacters { get; set; }
    public int MaxTranslationSourceCharacters { get; set; }
    public bool IncludeTranslatedContent { get; set; }
    public bool IncludeSourceList { get; set; }
}

public sealed class FeatureFlag
{
    public int Id { get; set; }
    public required string Key { get; set; }
    public bool IsEnabled { get; set; }
}

public sealed class AdminUser
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public required string Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class EmailRecipient
{
    public int Id { get; set; }
    public required string Address { get; set; }
    public string? DisplayName { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed class ReportTemplateConfiguration
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string SubjectTemplate { get; set; }
    public required string BodyTemplate { get; set; }
    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed class ApplicationSetting
{
    public int Id { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public string? Description { get; set; }
}

public sealed class ConfigurationAuditEntry
{
    public long Id { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public required string Action { get; set; }
    public required string Actor { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class PipelineCheckpoint
{
    public required string RunId { get; set; }
    public required string RunDate { get; set; }
    public required string Stage { get; set; }
    public required string SourceName { get; set; }
    public string? ContentHash { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}