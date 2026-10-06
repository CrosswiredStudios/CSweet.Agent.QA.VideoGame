using CrosswiredStudios.VideoGame.AgentKit;
using CSweet.Agent.SDK;

namespace CSweet.Agent.QA.VideoGame;

public sealed partial class SpecialistAgent : VideoGameSpecialistAgentBase
{
    internal const int DefaultContextWindowTokens = 128_000;
    internal const int DefaultOutputTokens = 16_000;
    private const int MinimumOutputTokens = 1_000;
    public override string AgentId => "com.csweet.video-game-qa";
    public override string Version => "2.7.0";
    protected override AgentConfigurationBuilder Configure(AgentConfigurationBuilder builder) =>
        base.Configure(builder)
            .Number("maxContextWindowTokens", "Maximum context-window tokens", required: true,
                description: "Planning ceiling for QA model requests; set this no higher than the selected model's real context window.",
                minimum: 16_000, step: 1_000,
                defaultValue: DefaultContextWindowTokens)
            .Number("maxOutputTokens", "Maximum output tokens", required: true,
                description: "Budget for each QA model response, including reasoning. Set this within the selected model and provider's supported limits.",
                minimum: MinimumOutputTokens, step: 1_000,
                defaultValue: DefaultOutputTokens,
                lessThanFieldKey: "maxContextWindowTokens");

    internal static int ResolveOutputTokens(AgentSettings settings)
    {
        var contextWindow = Math.Max(settings.GetInt32("maxContextWindowTokens", DefaultContextWindowTokens),
            MinimumOutputTokens + 1);
        var output = Math.Max(settings.GetInt32("maxOutputTokens", DefaultOutputTokens),
            MinimumOutputTokens);
        return Math.Min(output, contextWindow - 1);
    }

    internal static int ResolveContextWindowTokens(AgentSettings settings) =>
        Math.Max(settings.GetInt32("maxContextWindowTokens", DefaultContextWindowTokens),
            MinimumOutputTokens + 1);
    protected override string RoleKey => "game-quality-assurance";
    protected override string ArtifactTypeKey => "video-game.qa-evaluation-plan.v1";
    protected override string RolePrompt => "Own risk-based test strategy, exact build execution, reproducible defects, regression, compatibility, accessibility checks, and validation evidence. Never accept an untested or stale build.";
    protected override IReadOnlyList<string> RequiredSections => ["Test Strategy", "Build Under Test", "Test Cases", "Compatibility", "Accessibility", "Defects", "Exit Criteria"];
}
