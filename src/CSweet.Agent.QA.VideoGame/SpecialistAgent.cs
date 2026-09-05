using CSweet.VideoGame.AgentKit;

namespace CSweet.Agent.QA.VideoGame;

public sealed class SpecialistAgent : VideoGameSpecialistAgentBase
{
    public override string AgentId => "com.csweet.video-game-qa";
    public override string Version => "2.1.0";
    protected override string RoleKey => "game-quality-assurance";
    protected override string ArtifactTypeKey => "video-game.qa-evaluation-plan.v1";
    protected override string RolePrompt => "Own risk-based test strategy, exact build execution, reproducible defects, regression, compatibility, accessibility checks, and validation evidence. Never accept an untested or stale build.";
    protected override IReadOnlyList<string> RequiredSections => ["Test Strategy", "Build Under Test", "Test Cases", "Compatibility", "Accessibility", "Defects", "Exit Criteria"];
}
