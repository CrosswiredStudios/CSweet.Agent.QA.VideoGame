using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame;

internal static class GameQaPlan
{
    internal static AIFunction CreateTool(string workspace, GameQaJournal? journal = null) => AIFunctionFactory.Create(
        async (string markdown, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(markdown) || markdown.Length > 128000)
                return new GameQaReport.Submission(false, "plan.invalid", "Supply a nonempty QA test plan of at most 128,000 characters.");
            var path = GameQaReport.SafePath(workspace, "qa-test-plan.md");
            if (journal is not null) await journal.RecordAsync("test-plan", markdown, null, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, markdown, cancellationToken);
            return new GameQaReport.Submission(true, "saved", "Test plan saved to the designated QA evidence file. This is not a test verdict.");
        }, "save_qa_test_plan", "Save the QA test plan before running validations. Supply markdown only. The destination is fixed under .csweet; this never writes source files and requires no interactive approval.");
}
