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
    internal static string For(WorkItemPlanningSpecification planning)
    {
        var notes = new List<string>();
        if (Regex.IsMatch(string.Join("\n", planning.AcceptanceCriteria ?? []), @"\b(audio|sound|tones?|music|sfx)\b", RegexOptions.IgnoreCase))
            notes.Add("Web Audio: a native OscillatorNode has no state property. Observe audio through the application's own hooks or an AnalyserNode rather than private internals.");
        return notes.Count == 0 ? "" :
            "Engine notes (hints from earlier runs; not requirements, and not a reason to test beyond the criteria):\n- " + string.Join("\n- ", notes) + "\n";
    }
}
