using UnityEngine;

// An unconditional entry point (RuntimeInitializeOnLoadMethod fires regardless of any asset
// reference) -- referenced by nothing via guid or C#. Its declaring file must be a root in its
// own right (entry_reason='attribute'), never a dead candidate, with no inbound edge needed to
// prove it.
public static class Bootstrap
{
    [RuntimeInitializeOnLoadMethod]
    public static void OnLoad()
    {
    }
}
