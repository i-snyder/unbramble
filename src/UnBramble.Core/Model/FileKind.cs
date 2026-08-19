namespace UnBramble.Core.Model;

public enum FileKind
{
    Asset,
    Script,
    Folder,
    Settings,
}

public static class FileKindExtensions
{
    public static string ToDbString(this FileKind kind) => kind switch
    {
        FileKind.Asset => "asset",
        FileKind.Script => "script",
        FileKind.Folder => "folder",
        FileKind.Settings => "settings",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static FileKind FromDbString(string value) => value switch
    {
        "asset" => FileKind.Asset,
        "script" => FileKind.Script,
        "folder" => FileKind.Folder,
        "settings" => FileKind.Settings,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unrecognized files.kind value"),
    };
}
