using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using UnBramble.Core.Config;

namespace UnBramble.Cli;

/// <summary>
/// Owns the reversible project-root changes made by <c>unbramble init</c>. The receipt follows
/// the same rule as the generated instruction markers: restore the exact prior bytes when the
/// installed file is unchanged; if a user edited around UnBramble's content later, remove only
/// the recognizable UnBramble-owned portion and preserve the edit.
/// </summary>
public static class ProjectInstallation
{
    private const int StateVersion = 1;

    private enum FileKind
    {
        Agents,
        Claude,
        GitIgnore,
        PlasticIgnore,
    }

    public sealed record UninstallResult(bool Changed, bool ExactRollback);

    /// <summary>Captures the four project files setup may touch. Call <see cref="SetupCapture.Commit"/>
    /// after the normal setup steps finish.</summary>
    public static SetupCapture CaptureSetup(string projectRoot, bool includeAgentFiles) =>
        new(projectRoot, includeAgentFiles);

    public sealed class SetupCapture
    {
        private readonly string _projectRoot;
        private readonly bool _includeAgentFiles;
        private readonly ProjectInstallState? _priorState;
        private readonly Dictionary<FileKind, byte[]?> _before;

        internal SetupCapture(string projectRoot, bool includeAgentFiles)
        {
            _projectRoot = Path.GetFullPath(projectRoot);
            _includeAgentFiles = includeAgentFiles;
            _priorState = ReadState(_projectRoot);
            _before = CandidateFiles(_projectRoot).ToDictionary(x => x.Kind, x => ReadBytes(x.Path));
        }

        public void Commit()
        {
            var priorByKind = (_priorState?.Files ?? [])
                .ToDictionary(x => ParseKind(x.Kind), x => x);
            var files = new List<ProjectInstallFileState>();

            foreach (var candidate in CandidateFiles(_projectRoot))
            {
                var before = _before[candidate.Kind];
                var installed = ReadBytes(candidate.Path);
                priorByKind.TryGetValue(candidate.Kind, out var prior);

                if (!_includeAgentFiles
                    && candidate.Kind is FileKind.Agents or FileKind.Claude
                    && prior is null)
                {
                    continue;
                }

                if (prior is null && BytesEqual(before, installed) && !ContainsOwnedContent(candidate.Kind, installed))
                {
                    continue;
                }

                byte[]? baseline;
                if (prior is not null && string.Equals(Hash(before), prior.InstalledSha256, StringComparison.Ordinal))
                {
                    baseline = DecodeOriginal(prior);
                }
                else
                {
                    baseline = RemoveOwnedContent(candidate.Kind, before);
                }

                files.Add(new ProjectInstallFileState
                {
                    RelativePath = candidate.RelativePath,
                    Kind = candidate.Kind.ToString(),
                    OriginallyExisted = baseline is not null,
                    OriginalContentBase64 = baseline is null ? null : Convert.ToBase64String(baseline),
                    InstalledSha256 = Hash(installed),
                });
            }

            if (files.Count == 0)
            {
                DeleteStateFile(_projectRoot);
                return;
            }

            WriteState(_projectRoot, new ProjectInstallState { Version = StateVersion, Files = files });
        }
    }

    /// <summary>Restores setup-owned project files and deletes all generated project state.
    /// Callers must stop live processes and successfully unwind Defender exclusions first.</summary>
    public static UninstallResult Uninstall(string projectRoot, Action<string> announce)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        var state = ReadState(projectRoot);
        var stateByKind = (state?.Files ?? []).ToDictionary(x => ParseKind(x.Kind), x => x);
        var candidates = CandidateFiles(projectRoot);
        var current = candidates.ToDictionary(x => x.Kind, x => ReadBytes(x.Path));
        var desired = new Dictionary<FileKind, byte[]?>();
        var exactRollback = state is not null;

        foreach (var candidate in candidates)
        {
            var bytes = current[candidate.Kind];
            if (stateByKind.TryGetValue(candidate.Kind, out var installedState))
            {
                if (string.Equals(Hash(bytes), installedState.InstalledSha256, StringComparison.Ordinal))
                {
                    desired[candidate.Kind] = DecodeOriginal(installedState);
                }
                else
                {
                    desired[candidate.Kind] = candidate.Kind == FileKind.Agents && !installedState.OriginallyExisted
                        ? AgentInstructionsSetup.RemoveOwnedAgentsContent(bytes, removeGeneratedContainerHeading: true)
                        : RemoveOwnedContent(candidate.Kind, bytes);
                    exactRollback = false;
                }
            }
            else
            {
                var cleaned = RemoveOwnedContent(candidate.Kind, bytes);
                desired[candidate.Kind] = cleaned;
                if (!BytesEqual(bytes, cleaned))
                {
                    exactRollback = false;
                }
            }
        }

        var changedFiles = new List<(CandidateFile File, byte[]? Before)>();
        try
        {
            foreach (var candidate in candidates)
            {
                var before = current[candidate.Kind];
                var after = desired[candidate.Kind];
                if (BytesEqual(before, after))
                {
                    continue;
                }

                WriteOrDelete(candidate.Path, after);
                changedFiles.Add((candidate, before));
                announce($"Project setup: restored {candidate.RelativePath}.");
            }
        }
        catch
        {
            foreach (var changed in changedFiles.AsEnumerable().Reverse())
            {
                WriteOrDelete(changed.File.Path, changed.Before);
            }

            throw;
        }

        var stateDir = UnBramblePaths.StateDirFor(projectRoot);
        var removedState = Directory.Exists(stateDir);
        if (removedState)
        {
            var fullStateDir = Path.GetFullPath(stateDir);
            var expectedParent = Path.TrimEndingDirectorySeparator(projectRoot);
            if (!string.Equals(Path.GetDirectoryName(fullStateDir), expectedParent, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(fullStateDir), UnBramblePaths.StateDirName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"refusing to remove unexpected state directory '{fullStateDir}'.");
            }

            TryClearHiddenAttribute(fullStateDir);
            // An in-process caller (including the CLI test harness) may have disposed its last
            // engine while Microsoft.Data.Sqlite still retains the native file handle in its
            // connection pool. All watcher processes are already stopped by the caller; clear
            // this process's idle pool before removing the database.
            SqliteConnection.ClearAllPools();
            Directory.Delete(fullStateDir, recursive: true);
            announce($"Project state: removed {UnBramblePaths.StateDirName}/.");
        }

        return new UninstallResult(changedFiles.Count > 0 || removedState, exactRollback);
    }

    private static IReadOnlyList<CandidateFile> CandidateFiles(string projectRoot) =>
    [
        new(FileKind.Agents, "AGENTS.md", Path.Combine(projectRoot, "AGENTS.md")),
        new(FileKind.Claude, "CLAUDE.md", Path.Combine(projectRoot, "CLAUDE.md")),
        new(FileKind.GitIgnore, ".gitignore", Path.Combine(projectRoot, ".gitignore")),
        new(FileKind.PlasticIgnore, "ignore.conf", Path.Combine(projectRoot, "ignore.conf")),
    ];

    private static bool ContainsOwnedContent(FileKind kind, byte[]? content) =>
        !BytesEqual(content, RemoveOwnedContent(kind, content));

    private static byte[]? RemoveOwnedContent(FileKind kind, byte[]? content) => kind switch
    {
        FileKind.Agents => AgentInstructionsSetup.RemoveOwnedAgentsContent(content),
        FileKind.Claude => AgentInstructionsSetup.RemoveGeneratedClaudeShim(content),
        FileKind.GitIgnore => RemoveIgnoreEntry(content, $"{UnBramblePaths.StateDirName}/"),
        FileKind.PlasticIgnore => RemoveIgnoreEntry(content, UnBramblePaths.StateDirName),
        _ => content,
    };

    private static byte[]? RemoveIgnoreEntry(byte[]? content, string entry)
    {
        if (content is null)
        {
            return null;
        }

        var text = DecodeText(content);
        var lines = SplitLines(text).ToList();
        var removed = lines.RemoveAll(line => string.Equals(line.Trim(), entry, StringComparison.Ordinal)) > 0;
        if (!removed)
        {
            return content;
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines.Count == 0 ? null : EncodeText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static ProjectInstallState? ReadState(string projectRoot)
    {
        try
        {
            var path = StatePath(projectRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize(File.ReadAllText(path), ProjectInstallJsonContext.Default.ProjectInstallState);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteState(string projectRoot, ProjectInstallState state)
    {
        var path = StatePath(projectRoot);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{UnBramblePaths.ProjectInstallStateFileName}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(state, ProjectInstallJsonContext.Default.ProjectInstallState) + Environment.NewLine);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void DeleteStateFile(string projectRoot)
    {
        var path = StatePath(projectRoot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string StatePath(string projectRoot) =>
        Path.Combine(UnBramblePaths.StateDirFor(projectRoot), UnBramblePaths.ProjectInstallStateFileName);

    private static byte[]? DecodeOriginal(ProjectInstallFileState state) =>
        state.OriginallyExisted && state.OriginalContentBase64 is not null
            ? Convert.FromBase64String(state.OriginalContentBase64)
            : null;

    private static FileKind ParseKind(string value) =>
        Enum.TryParse<FileKind>(value, ignoreCase: false, out var kind)
            ? kind
            : throw new InvalidDataException($"unknown project install file kind '{value}'.");

    private static byte[]? ReadBytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static void WriteOrDelete(string path, byte[]? content)
    {
        if (content is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        var temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string Hash(byte[]? content) => content is null
        ? "missing"
        : Convert.ToHexString(SHA256.HashData(content));

    private static bool BytesEqual(byte[]? left, byte[]? right) =>
        left is null && right is null
        || left is not null && right is not null && left.AsSpan().SequenceEqual(right);

    private static string DecodeText(byte[] content) => Encoding.UTF8.GetString(content).TrimStart('\uFEFF');

    private static byte[] EncodeText(string text) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

    private static IEnumerable<string> SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    private static void TryClearHiddenAttribute(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.Hidden) != 0)
        {
            info.Attributes &= ~FileAttributes.Hidden;
        }
    }

    private sealed record CandidateFile(FileKind Kind, string RelativePath, string Path);
}

public sealed class ProjectInstallFileState
{
    public required string RelativePath { get; init; }

    public required string Kind { get; init; }

    public bool OriginallyExisted { get; init; }

    public string? OriginalContentBase64 { get; init; }

    public required string InstalledSha256 { get; init; }
}

public sealed class ProjectInstallState
{
    public int Version { get; init; }

    public required List<ProjectInstallFileState> Files { get; init; }
}

[JsonSerializable(typeof(ProjectInstallState))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class ProjectInstallJsonContext : JsonSerializerContext
{
}
