namespace SkarpSharp.Agent;

/// <summary>
/// When the Claude hook's <c>About Skarp:</c> line (and the duplicate-code note) is printed
/// to an agent: on its first printed message in the session, on its first printed message after a compaction (the
/// session's <see cref="TrackingState.Compactions"/> passed the count at its last About line), and when
/// <see cref="Every"/> printed messages went by since its last About line. A subagent is its own agent.
/// </summary>
public static class AboutRhythm
{
    /// <summary>Printed messages (the one with the About line included) after which it is printed again.</summary>
    public const int Every = 20;

    /// <summary>Whether the About line is due on the agent's next printed message.</summary>
    public static bool Due(TrackedAgent agent, int compactions) =>
        agent.AboutCompactions is not int at || compactions > at || agent.SinceAbout >= Every;

    /// <summary>Records a printed message; <paramref name="fits"/>: the About line can be printed on it (the budget is more
    /// than one line), so a due line counts as printed. Returns whether it was due.</summary>
    public static bool Printed(TrackedAgent agent, int compactions, bool fits)
    {
        bool due = Due(agent, compactions);
        if (due && fits)
        {
            agent.AboutCompactions = compactions;
            agent.SinceAbout = 1;
        }
        else agent.SinceAbout++;
        return due;
    }
}
