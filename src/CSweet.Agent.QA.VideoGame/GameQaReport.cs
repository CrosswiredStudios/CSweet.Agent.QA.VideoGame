using System.Text.Json;
using Microsoft.Extensions.AI;
using CSweet.Agent.SDK;

namespace CSweet.Agent.QA.VideoGame;

internal static class GameQaReport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static AIFunction CreateTool(string workspace, string commit, IReadOnlyList<string> requiredCriteria, CorrectionBudget? budget = null,
        Func<GameQaOutcome, CancellationToken, Task>? checkpoint = null) =>
        new ReportFunction(AIFunctionFactory.Create(
            async (string summary, bool passed, IReadOnlyList<GitValidationResult> validations,
                IReadOnlyList<string> findings, IReadOnlyList<GameQaCriterion> criteria, CancellationToken cancellationToken) =>
            {
                var report = new GameQaOutcome(commit, summary, passed, validations, findings) { Criteria = criteria };
                // The destination and source identity come from the assignment, never model arguments.
                GameQaExecution.ValidateOutcome(report, commit);
                var path = ReportPath(workspace);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
                if (checkpoint is not null) await checkpoint(report, cancellationToken);
                return new Submission(true, "saved", "QA report saved. Source-integrity checks and delivery gates still apply.");
            },
            "submit_qa_report",
            "Save the final independent QA report for the assigned commit. The verified workspace supplies sourceCommitSha in code; do not supply or retype it. Supply summary, passed, validations, findings and criteria directly as named arguments. Include ONLY the exact supplied acceptance criteria, once each, and actual command results. findings contains unresolved blockers only; passed=true REQUIRES findings=[]. Keep nonblocking observations and optional verification notes in summary. Never erase a real defect to pass: report passed=false instead. Set a required criterion's unverifiable=true (with satisfied=false) only when its evidence needs an unavailable environment; do not add extra criteria. This writes only the designated report, never source files."), commit, requiredCriteria, budget);

    internal sealed class CorrectionBudget(CancellationToken token) : IDisposable
    {
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        private int invalid;
        internal bool Exhausted { get; private set; }
        internal CancellationToken Token => stop.Token;
        internal string FailureMessage { get; private set; } = "";
        internal Submission? LastRejection { get; private set; }
        internal Submission RecordFailure(Submission feedback)
        {
            LastRejection = feedback;
            return feedback;
        }
        internal Submission Reject(Submission feedback)
        {
            invalid++;
            RecordFailure(feedback);
            if (invalid < 4) return feedback with { Message = feedback.Message + $" Correct all listed issues together; {4 - invalid} submission attempts remain. Preserve actual defects and executed evidence." };
            Exhausted = true;
            FailureMessage = "C-Sweet QA report validation exhausted three corrections. No QA verdict was accepted. " +
                "Escalate this report-processing issue to management for code correction. " + feedback.Message;
            stop.Cancel();
            return feedback with { Code = "report.correction-exhausted", Message = FailureMessage };
        }
        public void Dispose() => stop.Dispose();
    }

    internal sealed record Submission(bool Saved, string Code, string Message, IReadOnlyList<string>? RequiredCriteria = null);

    private sealed class ReportFunction(AIFunction inner, string commit, IReadOnlyList<string> criteria, CorrectionBudget? budget) : DelegatingAIFunction(inner)
    {
        private readonly SemaphoreSlim gate = new(1,1);
        private bool saved;
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try { return await SubmitAsync(arguments,cancellationToken); }
            finally {gate.Release();}
        }
        private async ValueTask<object?> SubmitAsync(AIFunctionArguments arguments,CancellationToken cancellationToken)
        {
            if(saved) return new Submission(true,"already-saved","The first validated report and its observations are retained. Finish this QA turn; do not replace its verdict.");
            try
            {
                var normalized = NormalizeArguments(arguments, commit);
                var report = JsonSerializer.Deserialize<GameQaOutcome>(JsonSerializer.Serialize(normalized, JsonOptions), JsonOptions);
                var issues = new List<string>();
                try { GameQaExecution.ValidateOutcome(report, commit); }
                catch (InvalidOperationException error) { issues.Add(error.Message); }
                if (report is not null)
                {
                    try { GameQaExecution.ValidateCriterionCoverage(report, criteria); }
                    catch (InvalidOperationException error) { issues.Add(error.Message); }
                }
                if (issues.Count != 0) throw new InvalidOperationException(string.Join("; ", issues));
                normalized.Remove("sourceCommitSha");
                var result=await InnerFunction.InvokeAsync(normalized, cancellationToken);
                saved=true;
                return result;
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            {
                return Reject(new Submission(false, "report.invalid-arguments",
                    $"Invalid report arguments: {error.Message} Supply summary as a string, passed as a JSON boolean, and validations, findings and criteria as JSON arrays. The assigned sourceCommitSha is supplied by code. Do not change evidence to make submission succeed.", criteria));
            }
            catch (InvalidOperationException error)
            {
                return Reject(new Submission(false, "report.invalid-evidence", error.Message, criteria));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                var failure = new Submission(false, "report.write-failed", $"The QA report could not be saved ({error.GetType().Name}). No verdict was accepted.");
                return budget?.RecordFailure(failure) ?? failure;
            }
        }
        private Submission Reject(Submission feedback) => budget?.Reject(feedback) ?? feedback;
    }

    internal static AIFunctionArguments NormalizeArguments(AIFunctionArguments arguments, string commit)
    {
        GameQaExecution.RequireCommit(commit);
        if (arguments.TryGetValue("sourceCommitSha", out var supplied))
        {
            var identity = JsonSerializer.SerializeToElement(supplied, JsonOptions);
            if (identity.ValueKind != JsonValueKind.String || !string.Equals(identity.GetString(), commit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"sourceCommitSha does not match the assigned commit {commit}. Omit sourceCommitSha; the verified workspace supplies it. No report was saved.");
        }
        var normalized = new AIFunctionArguments();
        foreach (var field in new[] { "summary", "passed", "validations", "findings", "criteria" })
        {
            if (!arguments.TryGetValue(field, out var value)) throw new ArgumentException($"Missing required report field '{field}'.");
            var json = JsonSerializer.SerializeToElement(value, JsonOptions);
            var valid = field switch
            {
                "summary" => json.ValueKind == JsonValueKind.String,
                "passed" => json.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => json.ValueKind == JsonValueKind.Array
            };
            if (!valid) throw new ArgumentException($"Report field '{field}' has an incorrect JSON type ({json.ValueKind}).");
            normalized[field] = json;
        }
        if (arguments.Keys.Any(field => field != "sourceCommitSha" && !normalized.ContainsKey(field))) throw new ArgumentException("Unknown report fields.");
        normalized["sourceCommitSha"] = commit;
        return normalized;
    }

    internal static void Reset(string workspace)
    {
        var path = ReportPath(workspace);
        if (File.Exists(path)) File.Delete(path);
    }

    internal static async Task<GameQaOutcome> ReadAsync(string workspace, CancellationToken token, Submission? rejection = null)
    {
        var path = ReportPath(workspace);
        if (!File.Exists(path))
        {
            if (rejection is not null)
                throw new InvalidOperationException($"QA report submission was rejected ({rejection.Code}): {rejection.Message} No validation verdict was accepted.");
            throw new InvalidOperationException("QA stopped without submitting its structured report. No validation verdict was accepted; rerun QA with the report-submission tool.");
        }
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

    private static string ReportPath(string workspace) => SafePath(workspace, "qa-outcome.json");

    internal static string SafePath(string workspace, string file)
    {
        if (file is not ("qa-outcome.json" or "qa-test-plan.md")) throw new ArgumentException("Only designated QA evidence files may be written.");
        var root = Path.GetFullPath(workspace);
        var directory = Path.Combine(root, ".csweet");
        var path = Path.Combine(directory, file);
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
