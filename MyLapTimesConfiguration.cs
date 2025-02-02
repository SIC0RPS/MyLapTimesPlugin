using AssettoServer.Server.Configuration;
using FluentValidation;

namespace MyLapTimesPlugin;

public class MyLapTimesConfiguration : IValidateConfiguration<MyLapTimesConfigurationValidator>
{
    public bool Enabled { get; init; } = true;
    public string DiscordWebhookUrl { get; init; } = "";
    public int MaxTopTimes { get; init; } = 5;
    public bool BroadcastMessages { get; init; } = true;
}

public class MyLapTimesConfigurationValidator : AbstractValidator<MyLapTimesConfiguration>
{
    public MyLapTimesConfigurationValidator()
    {
        RuleFor(cfg => cfg.MaxTopTimes)
            .GreaterThan(0)
            .LessThanOrEqualTo(100);
        RuleFor(cfg => cfg.Enabled).NotNull();
    }
}
