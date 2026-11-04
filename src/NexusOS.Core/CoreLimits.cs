// Per-minute ceilings on Core's API. Core serves one person, so these only
// stop a runaway loop (a broken automation, a script gone wrong); ordinary
// use, including the shell polling for Guardian and pending actions every
// few seconds, stays far below them. Model requests also queue in
// InferenceConcurrencyGate rather than being turned away.
public static class CoreLimits
{
    public const int AiPerMinute = 240;
    public const int JournalPerMinute = 1200;
}
