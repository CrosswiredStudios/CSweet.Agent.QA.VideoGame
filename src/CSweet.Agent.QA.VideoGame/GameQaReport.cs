using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame;

internal static class GameQaReport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static AIFunction CreateTool(string workspace, string commit, IReadOnlyList<string> criteria) =>
        AIFunctionFactory.Create(
            async (GameQaOutcome report, CancellationToken cancellationToken) =>
            {
                // The destination and source identity come from the assignment, never model arguments.
                GameQaExecution.ValidateOutcome(report, commit);
                GameQaExecution.ValidateCriterionCoverage(report, criteria);
                var path = ReportPath(workspace);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
                return "QA report saved. This records evidence only; source-integrity checks and delivery gates still apply.";
            },
            "submit_qa_report",
            "Save the final independent QA report for the assigned commit. Include every exact acceptance criterion and actual command results. Unmet criteria must not pass. This tool writes only the designated report, never source files.");

    internal static void Reset(string workspace)
    {
        var path = ReportPath(workspace);
        if (File.Exists(path)) File.Delete(path);
    }

    internal static async Task<GameQaOutcome> ReadAsync(string workspace, CancellationToken token)
    {
        var path = ReportPath(workspace);
        if (!File.Exists(path))
            throw new InvalidOperationException("QA stopped without submitting its structured report. No validation verdict was accepted; rerun QA with the report-submission tool.");
        try
        {
            return JsonSerializer.Deserialize<GameQaOutcome>(await File.ReadAllTextAsync(path, token), JsonOptions)
                ?? throw new InvalidOperationException("QA submitted an empty report; no validation verdict was accepted.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("QA submitted malformed report JSON; no validation verdict was accepted.");
        }
    }

    private static string ReportPath(string workspace)
    {
        var root = Path.GetFullPath(workspace);
        var directory = Path.Combine(root, ".csweet");
        var path = Path.Combine(directory, "qa-outcome.json");
        // Reject links at every existing ancestor, including a dangling report-file link.
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("QA report destination must not contain symbolic links.");
        }
        return path;
    }
}
