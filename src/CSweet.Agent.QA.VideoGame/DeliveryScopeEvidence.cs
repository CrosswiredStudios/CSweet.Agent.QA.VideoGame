using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame;

internal static class DeliveryScopeEvidence
{
    internal static IReadOnlyList<WorkDeliveryValidationEvidence> Bind(GameQaOutcome report, WorkDeliveryCandidate candidate)
    {
        GameQaExecution.ValidateOutcome(report, candidate.Digest);
        var evidence = new List<WorkDeliveryValidationEvidence>();
        foreach (var validation in report.Validations)
        {
            // The immutable candidate supplies the repository and commit. Reject ambiguous commands;
            // mentioning multiple repositories cannot stand in for independently testing each one.
            var repositories = candidate.Repositories.Where(r => validation.Command.Contains(
                "repositories/" + r.RepositoryId.ToString("N"), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (repositories.Length != 1)
                throw new InvalidOperationException("Each validation command must identify exactly one candidate repository with repositories/<repositoryId N format>. Retain its actual output; do not relabel another repository's check.");
            var repository = repositories[0];
            evidence.Add(new(repository.RepositoryId, repository.CandidateCommitSha, validation.Command,
                validation.ExitCode, validation.Succeeded, validation.DiagnosticExcerpt!));
        }
        if (report.Passed && candidate.Repositories.Any(r => evidence.All(v => v.RepositoryId != r.RepositoryId)))
            throw new InvalidOperationException("QA approval requires executed validation and captured output for EVERY candidate repository. Run the missing checks; do not fabricate coverage.");
        return evidence;
    }
}
