using System.Text;
using CSweet.Agent.SDK;

namespace CSweet.Agent.QA.VideoGame.Tests;

/// <summary>
/// Super Awesome Games ticket 38 (2026-11-13): QA found one mismatch per round with ad hoc probes. Its first run on a
/// declared-interaction ticket is now the author's shared scenario run, compared with the generated records.
/// </summary>
public sealed class ScenarioContractTests
{
    private const string Record = """
        # Observation record

        The chain was planned as r1c0, r1c1, r2c1, r2c2.

        <!-- csweet:observed:begin -->
        generated
        ```json
        {"contract":1,"build":"abc","valid":true,"passed":true,"problems":[],"scenarios":[{"id":"shortChain","passed":true,
         "checks":[{"name":"adjacent","comparison":"equals","expected":true,"observed":true,"evidence":[0,1],"passed":true,"criterion":1}],
         "events":[{"type":"brick.hit","data":{"row":1,"col":0}},{"type":"brick.hit","data":{"row":1,"col":1}}]}]}
        ```
        <!-- csweet:observed:end -->
        """;

    private const string Run = """
        {"contract":1,"valid":true,"passed":false,"problems":[],"scenarios":[{"id":"shortChain","passed":false,
         "checks":[{"name":"adjacent","comparison":"equals","expected":true,"observed":false,"evidence":[0,1],"passed":false,"criterion":1}],
         "events":[{"type":"brick.hit","t":0,"data":{"row":2,"col":0}},{"type":"brick.hit","t":0.1,"data":{"row":2,"col":1}}]}]}
        """;

    [Fact]
    public void The_standard_run_is_the_sdk_script_within_the_script_budget_and_the_runner_offers_attach()
    {
        Assert.Equal(BrowserScenarios.PlaywrightScript, GameQaInteractionTesting.ScenarioScript);
        Assert.True(Encoding.UTF8.GetByteCount(GameQaInteractionTesting.ScenarioScript) <= 12000);
        var runner = GameQaInteractionTesting.Resource("qa-interactions.cjs");
        Assert.Contains("'check', 'attach', script)", runner);
        Assert.Contains("/evidence/scenarios.json", runner);
    }

    [Fact]
    public void A_run_that_differs_from_the_generated_record_lists_every_difference_and_failed_check()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-qa-scenarios-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "spike-notes"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
        try
        {
            File.WriteAllText(Path.Combine(root, "spike-notes", "observation.md"), Record);
            File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "README.md"), Record); // dependency trees are skipped
            var note = GameQaInteractionTesting.DescribeScenarios(BrowserScenarios.ReadEvaluation(Run)!, root);
            Assert.Contains("FAILED adjacent (criterion 1): expected true (equals), observed false", note);
            Assert.Contains("Record spike-notes/observation.md: its generated section differs from this run", note);
            Assert.Contains("shortChain/adjacent: observed false here, true in the record", note);
            Assert.Contains("event 0 is brick.hit {\"col\":0,\"row\":2} here", note);
            Assert.DoesNotContain("node_modules", note);
            Assert.Contains("Report every failed scenario check and every record difference", note);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Without_a_generated_record_QA_is_told_the_measured_values_were_written_by_hand()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-qa-scenarios-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "notes.md"), "# Notes\nAll scenarios pass.");
            var note = GameQaInteractionTesting.DescribeScenarios(BrowserScenarios.ReadEvaluation(Run)!, root);
            Assert.Contains("No observation record in this source has a generated csweet:observed section", note);
        }
        finally { Directory.Delete(root, true); }
    }
}
