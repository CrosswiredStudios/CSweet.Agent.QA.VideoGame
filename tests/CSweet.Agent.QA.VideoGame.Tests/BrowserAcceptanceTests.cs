using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class BrowserAcceptanceTests
{
    [Fact]
    public void A_claimed_browser_pass_needs_current_source_completed_browser_evidence()
    {
        var planning = new WorkItemPlanningSpecification(["Chromium desktop"], ["A visible Phaser canvas renders"]);
        var report = new GameQaOutcome(new('a',40), "Observed", true, [], []);
        var browser = new GameQaBrowserTesting.BrowserState(report.SourceCommitSha, "exact-files", new('b',64), 10, [1280], false,
            Report:new(Guid.NewGuid(), report.SourceCommitSha, new('b',64), "Chromium", true, [new(1280,720,true,true,false,[])], new('c',64), 10), Complete:true);
        BrowserAcceptance.Validate(planning, report, "exact-files", browser);
        foreach(var invalid in new[] { null, browser with { Complete=false }, browser with { Commit=new('d',40) },
            browser with { SourceIdentity="stale" }, browser with { Report=browser.Report! with {Passed=false} } })
            Assert.Throws<InvalidOperationException>(()=>BrowserAcceptance.Validate(planning,report,"exact-files",invalid));
        BrowserAcceptance.Validate(planning, report with {Passed=false}, "exact-files", null); // Missing proof is allowed in an honest failed report.
        Assert.Throws<InvalidOperationException>(()=>BrowserAcceptance.Validate(planning,report,"exact-files",browser with {
            Report=browser.Report! with {Viewports=[new(1280,720,true,false,false,[])]} }));
        Assert.False(BrowserAcceptance.Required(new(["Phaser pin"],["Commit the exact version and lockfile"])));
    }

    [Fact]
    public void Handoff_names_the_supplied_commit_and_story_target_instead_of_selecting_main()
    {
        var guidance=BrowserAcceptance.Handoff(new('a',40),"csweet/story/example");
        Assert.Contains("immutable commit "+new string('a',40),guidance);
        Assert.Contains("assigned target is csweet/story/example",guidance);
        Assert.Contains("notes naming main do not select another branch",guidance);
        Assert.Contains("downstream evidence as pending",guidance);
    }
}
