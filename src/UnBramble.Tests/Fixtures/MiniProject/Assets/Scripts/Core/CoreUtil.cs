// Lives in Core.asmdef; called by Foo.Start() in Game.asmdef.
// Exercises a cross-assembly call edge for the Roslyn extraction fixtures.
public static class CoreUtil
{
    public static void Ping()
    {
    }
}
