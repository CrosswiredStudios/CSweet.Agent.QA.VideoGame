using System.Text.RegularExpressions;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame;

/// <summary>
/// Engine- and API-specific lessons from earlier runs. They are hints, included only when the project uses that
/// engine or the criteria concern that subject, so the core QA contract stays engine-neutral and the hints never read
/// as a reason to test beyond the ticket.
/// </summary>
internal static class GameQaEngineNotes
{
    internal static string For(string workspacePath, WorkItemPlanningSpecification planning)
    {
        var notes = new List<string>();
        if (UsesPhaser(workspacePath))
            notes.Add("Phaser: lifecycle events belong to each scene's sys.events, not game.scene.on; inspect the installed version's APIs rather than assuming them.");
        if (Regex.IsMatch(string.Join("\n", planning.AcceptanceCriteria ?? []), @"\b(audio|sound|tones?|music|sfx)\b", RegexOptions.IgnoreCase))
            notes.Add("Web Audio: a native OscillatorNode has no state property. Observe audio through the application's own hooks or an AnalyserNode rather than private internals.");
        return notes.Count == 0 ? "" :
            "Engine notes (hints from earlier runs; not requirements, and not a reason to test beyond the criteria):\n- " + string.Join("\n- ", notes) + "\n";
    }

    private static bool UsesPhaser(string workspacePath)
    {
        try
        {
            var manifest = Path.Combine(workspacePath, "package.json");
            return File.Exists(manifest) && new FileInfo(manifest).Length <= 256 * 1024 &&
                Regex.IsMatch(File.ReadAllText(manifest), "\"phaser\"\\s*:", RegexOptions.IgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
