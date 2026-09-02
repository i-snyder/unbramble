using System.Text;
using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Core.Freshness;
using UnBramble.Core.Parsing;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Regression coverage for Unity assets whose physical line shape, rather than total file size,
/// used to make StreamReader.ReadLine allocate until the process ran out of memory.
/// </summary>
public class LargeSerializedAssetTests
{
    private const int OversizedPayloadChars = 2 * 1024 * 1024;
    private const string FollowingGuid = "1234567890abcdef1234567890abcdef";

    [Theory]
    [InlineData("_typelessdata", 'a', "\r\n")]
    [InlineData("m_IndexBuffer", 'F', "\r")]
    public void OversizedHexYamlScalar_IsStreamed_AndFollowingReferenceKeepsItsStructure(
        string payloadKey,
        char hexCharacter,
        string newline)
    {
        using var temp = TempDir.Create();
        var path = Path.Combine(temp.Root, "Large.asset");
        using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write("%YAML 1.1");
            writer.Write(newline);
            writer.Write("--- !u!114 &42");
            writer.Write(newline);
            writer.Write("MonoBehaviour:");
            writer.Write(newline);
            writer.Write("  ");
            writer.Write(payloadKey);
            writer.Write(": ");
            WriteRepeated(writer, hexCharacter, OversizedPayloadChars);
            writer.Write(newline);
            writer.Write("  m_After: {fileID: 1, guid: ");
            writer.Write(FollowingGuid);
            writer.Write(", type: 3}");
            writer.Write(newline);
        }

        var parsed = new ReferenceParser().ParseContentSource(path, "Assets/Large.asset", ownGuid: null);

        var reference = Assert.Single(parsed.GuidRefs);
        Assert.Equal(FollowingGuid, reference.TargetGuid);
        Assert.Equal(5, reference.Line);
        Assert.Equal(114, reference.SourceClassId);
        Assert.Equal("42", reference.SourceFileId);
        Assert.Equal("m_After", reference.PropertyPath);
    }

    [Fact]
    public void OversizedHexSequenceScalar_PreservesSequenceIndexForFollowingReference()
    {
        using var temp = TempDir.Create();
        var path = Path.Combine(temp.Root, "Sequence.asset");
        using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write("%YAML 1.1\n--- !u!114 &42\nMonoBehaviour:\n  m_Data:\n  - ");
            WriteRepeated(writer, 'b', OversizedPayloadChars);
            writer.Write("\n  - m_Ref: {fileID: 1, guid: ");
            writer.Write(FollowingGuid);
            writer.Write(", type: 3}\n");
        }

        var parsed = new ReferenceParser().ParseContentSource(path, "Assets/Sequence.asset", ownGuid: null);

        var reference = Assert.Single(parsed.GuidRefs);
        Assert.Equal(6, reference.Line);
        Assert.Equal("m_Data[1].m_Ref", reference.PropertyPath);
    }

    [Fact]
    public void MaterializationBound_IsInclusive_AndOnePastIsRejected()
    {
        using var temp = TempDir.Create();
        var exactPath = Path.Combine(temp.Root, "Exact.shadergraph");
        var onePastPath = Path.Combine(temp.Root, "OnePast.shadergraph");
        WriteRepeatedFile(exactPath, 'x', 1024 * 1024);
        WriteRepeatedFile(onePastPath, 'x', 1024 * 1024 + 1);

        var exact = new ReferenceParser().ParseContentSource(exactPath, "Assets/Exact.shadergraph", ownGuid: null);
        Assert.Empty(exact.GuidRefs);
        Assert.Throws<InvalidDataException>(
            () => new ReferenceParser().ParseContentSource(onePastPath, "Assets/OnePast.shadergraph", ownGuid: null));
    }

    [Fact]
    public void OversizedHexYamlScalar_AtEofWithoutTerminator_IsStreamed()
    {
        using var temp = TempDir.Create();
        var path = Path.Combine(temp.Root, "Eof.asset");
        using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write("%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  _typelessdata: ");
            WriteRepeated(writer, 'c', OversizedPayloadChars);
        }

        var parsed = new ReferenceParser().ParseContentSource(path, "Assets/Eof.asset", ownGuid: null);
        Assert.Empty(parsed.GuidRefs);
    }

    [Fact]
    public void OversizedYamlScalar_WithNonHexCharacterPastTheBound_IsRejected()
    {
        using var temp = TempDir.Create();
        var path = Path.Combine(temp.Root, "Unknown.asset");
        using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write("%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Unknown: ");
            WriteRepeated(writer, 'a', OversizedPayloadChars);
            writer.Write("z\n");
        }

        var exception = Assert.Throws<InvalidDataException>(
            () => new ReferenceParser().ParseContentSource(path, "Assets/Unknown.asset", ownGuid: null));

        Assert.Contains("line 4", exception.Message, StringComparison.Ordinal);
        Assert.Contains("only hexadecimal payload data", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedNonYamlLine_IsRejectedInsteadOfGrowingWithoutBound()
    {
        using var temp = TempDir.Create();
        var path = Path.Combine(temp.Root, "Large.shadergraph");
        using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write("{\"payload\":\"");
            WriteRepeated(writer, 'a', OversizedPayloadChars);
            writer.Write("\"}");
        }

        Assert.Throws<InvalidDataException>(
            () => new ReferenceParser().ParseContentSource(path, "Assets/Large.shadergraph", ownGuid: null));
    }

    [Fact]
    public void BinaryAsset_IsIdentityAndMetaReferenceSource_ButContentIsSkipped()
    {
        using var fixture = FixtureCopy.Create();
        var assetPath = Path.Combine(fixture.Root, "Assets", "Data", "BinaryTerrain.asset");
        var binaryGuid = "feed0000000000000000000000000001";
        var bytes = new byte[OversizedPayloadChars];
        Array.Fill(bytes, (byte)'A');
        bytes[0] = 0x89; // invalid UTF-8, with no null byte anywhere in the sniff window
        File.WriteAllBytes(assetPath, bytes);
        File.WriteAllText(
            assetPath + ".meta",
            $$"""
            fileFormatVersion: 2
            guid: {{binaryGuid}}
            NativeFormatImporter:
              externalObjects:
              - first: {fileID: 11500000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01, type: 3}
            """);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var file = Assert.Single(engine.GetAllFiles(), f => f.Path == "Assets/Data/BinaryTerrain.asset");
        Assert.Equal(binaryGuid, file.Guid);

        var foo = engine.ResolveQueryTarget("Assets/Scripts/Foo.cs").Target;
        Assert.NotNull(foo);
        var whoUses = engine.WhoUses(foo, transitive: false, depthCap: 1);
        Assert.Contains(whoUses.Results, result => result.SourcePath == "Assets/Data/BinaryTerrain.asset");
    }

    [Fact]
    public void BinaryAsset_WithTruncatedUtf8AtEof_IsSkipped()
    {
        using var temp = TempDir.Create();
        var path = Path.Combine(temp.Root, "Truncated.asset");
        var prefix = Encoding.UTF8.GetBytes(
            $"%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Ref: {{fileID: 1, guid: {FollowingGuid}, type: 3}}\n");
        var bytes = new byte[prefix.Length + 1];
        prefix.CopyTo(bytes, 0);
        bytes[^1] = 0xc3;
        File.WriteAllBytes(path, bytes);

        var parsed = new ReferenceParser().ParseContentSource(path, "Assets/Truncated.asset", ownGuid: null);

        Assert.Empty(parsed.GuidRefs);
    }

    [Fact]
    public void Asset_WithInvalidUtf8BeyondBinarySniff_IsRejectedByBoundedReader()
    {
        using var temp = TempDir.Create();
        var path = Path.Combine(temp.Root, "LateBinary.asset");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes("%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Padding: "));
            var padding = new byte[9 * 1024];
            Array.Fill(padding, (byte)'A');
            stream.Write(padding);
            stream.Write(Encoding.UTF8.GetBytes($"\n  m_Ref: {{fileID: 1, guid: {FollowingGuid}, type: 3}}"));
            stream.WriteByte(0xff);
            stream.WriteByte((byte)'\n');
        }

        Assert.Throws<DecoderFallbackException>(
            () => new ReferenceParser().ParseContentSource(path, "Assets/LateBinary.asset", ownGuid: null));
    }

    [Fact]
    public void Utf16YamlAsset_IsNotMisclassifiedAsBinary()
    {
        using var temp = TempDir.Create();
        var path = Path.Combine(temp.Root, "Utf16.asset");
        File.WriteAllText(
            path,
            $"%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Ref: {{fileID: 1, guid: {FollowingGuid}, type: 3}}\n",
            Encoding.Unicode);

        var parsed = new ReferenceParser().ParseContentSource(path, "Assets/Utf16.asset", ownGuid: null);
        Assert.Equal(FollowingGuid, Assert.Single(parsed.GuidRefs).TargetGuid);
    }

    [Fact]
    public void OversizedHexMetaBeforeIdentity_IsStreamedByScannerAndMetaParser()
    {
        using var fixture = FixtureCopy.Create();
        var assetPath = Path.Combine(fixture.Root, "Assets", "Data", "LargeMeta.asset");
        var assetGuid = "feed0000000000000000000000000002";
        File.WriteAllText(assetPath, "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n");
        using (var writer = new StreamWriter(assetPath + ".meta", append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write("fileFormatVersion: 2\n  _typelessdata: ");
            WriteRepeated(writer, 'd', OversizedPayloadChars);
            writer.Write("\nguid: ");
            writer.Write(assetGuid);
            writer.Write("\n");
        }

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var file = Assert.Single(engine.GetAllFiles(), f => f.Path == "Assets/Data/LargeMeta.asset");
        Assert.Equal(assetGuid, file.Guid);
    }

    [Fact]
    public void FatalParseFailure_LeavesIndexIncomplete_AndFreshHeartbeatCannotVouchForIt()
    {
        using var fixture = FixtureCopy.Create();
        var assetPath = Path.Combine(fixture.Root, "Assets", "Data", "A.asset");
        string dbPath;

        using (var engine = UnBrambleEngine.Open(fixture.Root))
        {
            engine.RunIndex(full: false);
            dbPath = engine.DbPath;

            using (var writer = new StreamWriter(assetPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write("%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Unknown: ");
                WriteRepeated(writer, 'a', OversizedPayloadChars);
                writer.Write("z\n");
            }

            var failure = Record.Exception(() => engine.RunIndex(full: false));
            Assert.NotNull(failure);
            Assert.True(ContainsException<InvalidDataException>(failure), failure.ToString());
        }

        Assert.Equal("0", ReadMetaValue(dbPath, "index_complete"));

        File.WriteAllText(
            assetPath,
            """
            %YAML 1.1
            --- !u!114 &1
            MonoBehaviour:
              m_Target: {fileID: 1, guid: b0b0b0b0b0b0b0b0b0b0b0b0b0b0b020, type: 3}
            """);

        // A same-schema heartbeat would normally skip all disk work, and a live watcher holds its
        // lifetime lock even while idle. The incomplete marker must recover through the separate
        // finite writer lock instead of waiting forever for the watcher lifetime lock.
        using var watcherLock = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(watcherLock);
        HeartbeatFile.Write(fixture.Root, Environment.ProcessId, DateTime.UtcNow);

        using (var recovered = UnBrambleEngine.Open(fixture.Root))
        {
            var outcome = recovered.EnsureFresh();
            Assert.True(outcome.SweepPerformed);

            var target = recovered.ResolveQueryTarget("Assets/Data/B.asset").Target;
            Assert.NotNull(target);
            var whoUses = recovered.WhoUses(target, transitive: false, depthCap: 1);
            Assert.Contains(whoUses.Results, result => result.SourcePath == "Assets/Data/A.asset");
        }

        Assert.Equal("1", ReadMetaValue(dbPath, "index_complete"));
    }

    private static void WriteRepeated(TextWriter writer, char value, int count)
    {
        const int chunkSize = 16 * 1024;
        var chunk = new string(value, chunkSize);
        while (count >= chunk.Length)
        {
            writer.Write(chunk);
            count -= chunk.Length;
        }

        if (count > 0)
        {
            writer.Write(chunk.AsSpan(0, count));
        }
    }

    private static void WriteRepeatedFile(string path, char value, int count)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        WriteRepeated(writer, value, count);
    }

    private static bool ContainsException<T>(Exception exception) where T : Exception
    {
        if (exception is T)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions.Any(ContainsException<T>);
        }

        return exception.InnerException is not null && ContainsException<T>(exception.InnerException);
    }

    private static string? ReadMetaValue(string dbPath, string key)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta_kv WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }
}
