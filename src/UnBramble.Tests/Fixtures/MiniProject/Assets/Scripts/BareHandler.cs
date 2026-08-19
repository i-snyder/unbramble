// Referenced only via a bare (unqualified) method-group / delegate-conversion reference reached
// through `using static` (Foo.cs's `using static BareHandler;` + `System.Action b = HandleBare;`
// in Foo.Start) -- referenced nowhere else. Exercises method-group capture for unqualified
// forms, not just qualified forms like `Helper.Handle`.
public static class BareHandler
{
    public static void HandleBare()
    {
    }
}
