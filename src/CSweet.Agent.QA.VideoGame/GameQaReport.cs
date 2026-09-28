using System.Text.Json;
using Microsoft.Extensions.AI;
using CSweet.Agent.SDK;

namespace CSweet.Agent.QA.VideoGame;

internal static class GameQaReport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static AIFunction CreateTool(string workspace, string commit, IReadOnlyList<string> requiredCriteria) =>
        new ReportFunction(AIFunctionFactory.Create(
            async (string sourceCommitSha, string summary, bool passed, IReadOnlyList<GitValidationResult> validations,
                IReadOnlyList<string> findings, IReadOnlyList<GameQaCriterion> criteria, CancellationToken cancellationToken) =>
            {
                var report = new GameQaOutcome(sourceCommitSha, summary, passed, validations, findings) { Criteria = criteria };
                // The destination and source identity come from the assignment, never model arguments.
                GameQaExecution.ValidateOutcome(report, commit);
                var path = ReportPath(workspace);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
                return new Submission(true, "saved", "QA report saved. Source-integrity checks and delivery gates still apply.");
            },
            "submit_qa_report",
            "Save the final independent QA report for the assigned commit. Supply sourceCommitSha, summary, passed, validations, findings and criteria directly as named arguments. Include every exact acceptance criterion and actual command results. Unmet criteria must not pass. Set a criterion's unverifiable=true (with satisfied=false) only when its evidence needs a device, browser, network, registry, service or tool unavailable here; defects are never unverifiable. This writes only the designated report, never source files."), commit, requiredCriteria);

    internal sealed record Submission(bool Saved, string Code, string Message, IReadOnlyList<string>? RequiredCriteria = null);

    private sealed class ReportFunction(AIFunction inner, string commit, IReadOnlyList<string> criteria) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            try
            {
                var normalized = NormalizeArguments(arguments);
                var report = JsonSerializer.Deserialize<GameQaOutcome>(JsonSerializer.Serialize(normalized, JsonOptions), JsonOptions);
                GameQaExecution.ValidateOutcome(report, commit);
                GameQaExecution.ValidateCriterionCoverage(report!, criteria);
                return await InnerFunction.InvokeAsync(normalized, cancellationToken);
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            {
                return new Submission(false, "report.invalid-arguments",
                    "Supply sourceCommitSha and summary as strings, passed as a JSON boolean, and validations, findings and criteria as JSON arrays. Do not omit fields or change evidence to make submission succeed.", criteria);
            }
            catch (InvalidOperationException error)
            {
                return new Submission(false, "report.invalid-evidence", error.Message, criteria);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new Submission(false, "report.write-failed", $"The QA report could not be saved ({error.GetType().Name}). No verdict was accepted.");
            }
        }
    }

    internal static AIFunctionArguments NormalizeArguments(AIFunctionArguments arguments)
    {
        // Accept the previous nested form for compatibility, but reject ambiguous mixed input.
        if (arguments.TryGetValue("report", out var nested))
        {
            if (arguments.Count != 1) throw new ArgumentException("Mixed report formats.");
            var json = JsonSerializer.SerializeToElement(nested, JsonOptions);
            if (json.ValueKind != JsonValueKind.Object) throw new ArgumentException("Report must be an object.");
            arguments = new AIFunctionArguments(json.EnumerateObject().ToDictionary(x => x.Name, x => (object?)x.Value.Clone()));
        }
        var normalized = new AIFunctionArguments();
        foreach (var field in new[] { "sourceCommitSha", "summary", "passed", "validations", "findings", "criteria" })
        {
            if (!arguments.TryGetValue(field, out var value)) throw new ArgumentException("Missing report field.");
            var json = JsonSerializer.SerializeToElement(value, JsonOptions);
            if (field is not ("sourceCommitSha" or "summary") && json.ValueKind == JsonValueKind.String)
            {
                // Some provider tool parsers encode booleans/arrays as JSON strings. Decode exactly
                // one layer, without guessing values, coercing truthiness, or rewriting evidence.
                using var decoded = JsonDocument.Parse(json.GetString()!);
                json = decoded.RootElement.Clone();
            }
            var valid = field switch
            {
                "sourceCommitSha" or "summary" => json.ValueKind == JsonValueKind.String,
                "passed" => json.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => json.ValueKind == JsonValueKind.Array
            };
            if (!valid) throw new ArgumentException("Incorrect report field type.");
            normalized[field] = json;
        }
        if (arguments.Count != normalized.Count) throw new ArgumentException("Unknown report fields.");
        return normalized;
    }

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
