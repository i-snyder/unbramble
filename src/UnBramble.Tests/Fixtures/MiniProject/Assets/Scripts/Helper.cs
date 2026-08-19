// Referenced only as a method-group / delegate-conversion target
// (`System.Action h = Helper.Handle;` in Foo.Start) -- referenced nowhere else. Exercises ref
// extraction for delegate conversions.
public static class Helper
{
    public static void Handle()
    {
    }
}
