using System.Security.Cryptography;
using System.Text;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.AgentKit;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame;

/// <summary>
/// QA's own short plan, carried from one compute callback to the next. Each callback starts a fresh model session;
/// without this note the model re-derives its whole plan from the journal every time. The note is the model's working
/// memory, not evidence: it never proves a check passed.
/// </summary>
internal sealed class GameQaWorkingNote(GameQaJournal journal, PlatformCapabilityClient platform)
{
    internal const int MaximumCharacters = 3000;
    private string Key => "qa-note:" + journal.Identity;

    internal async Task<string?> ReadAsync(CancellationToken token) =>
        (await platform.ReadOperatingStateAsync<Note>(Key, token))?.Payload is { } note && note.Identity == journal.Identity ? note.Text : null;

    /// <summary>The note as prompt text for the next session; empty when none was saved.</summary>
    internal async Task<string> PromptAsync(CancellationToken token) => await ReadAsync(token) is { } text
        ? "Your working note from your previous turn on this exact source (your own plan, not evidence; trust what it marks settled unless the source changed):\n" + text
        : "";

    internal AIFunction Tool() => AIFunctionFactory.Create(async (string note, CancellationToken token) =>
    {
        note = note.Trim();
        if (note.Length is < 1 or > MaximumCharacters)
            throw new ArgumentException($"Keep the working note to 1-{MaximumCharacters} characters: criteria settled (with the evidence), open questions, and the next run you intend.");
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(note)))[..16];
        await new RevisionSafeProjectState(platform).MergeAsync<Note>(Key, "video-game.qa-working-note.v1", 1,
            _ => new Note(journal.Identity, note), new Dictionary<string, string>(), $"{Key}:{digest}", token);
        return new { saved = true, note = "Working note saved. The next QA turn on this source starts from it." };
    }, "save_qa_working_note",
        "Save your short working note for the next QA turn on this exact source (replaces the previous note). Before finishing a turn that waits on compute, record: which criteria are settled and by what evidence, open questions, and the next run you intend. This is your plan, not evidence or a verdict.");

    internal sealed record Note(string Identity, string Text);
}
