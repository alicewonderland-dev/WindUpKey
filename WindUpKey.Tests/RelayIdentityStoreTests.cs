using System.Diagnostics;
using System.Text.Json.Nodes;
using WindUpKey.Protocol;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>
/// <see cref="RelayIdentityStore"/> against real files in a temp folder. File locks and sharing violations behave
/// the same here on Linux and Windows (.NET emulates <see cref="FileShare.None"/> with advisory locks on Unix);
/// tests that need Unix file modes are <see cref="UnixOnlyFactAttribute"/>, or <see cref="UnixPermissionFactAttribute"/>
/// when they need those modes to deny access (skipped as root, like <see cref="NotAsRootFactAttribute"/>).
/// </summary>
public sealed class RelayIdentityStoreTests : IDisposable
{
    private const string A = "004000174AA8BCC2";
    private const string B = "0040001111111111";
    private const string C = "0040002222222222";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "windup-identity-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _warnings = [];
    private DateTime _now = new(2026, 10, 11, 12, 34, 56, DateTimeKind.Utc);

    public RelayIdentityStoreTests()
    {
        Directory.CreateDirectory(ConfigDir);
    }

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort temp cleanup.
        }
    }

    /// <summary>pluginConfigs/</summary>
    private string ConfigsDir => Path.Combine(_root, "pluginConfigs");

    /// <summary>pluginConfigs/WindUpKey/ (what GetPluginConfigDirectory returns)</summary>
    private string ConfigDir => Path.Combine(ConfigsDir, "WindUpKey");

    private string FilePath => Path.Combine(ConfigsDir, "WindUpKey.relay-identity.json");
    private string LockPath => FilePath + ".lock";

    private RelayIdentityStore NewStore() => new(ConfigDir, "WindUpKey", _warnings.Add, () => _now);

    private JsonNode ReadFile() => JsonNode.Parse(File.ReadAllText(FilePath))!;

    private string[] Corrupted() => Directory.GetFiles(ConfigsDir, "*.corrupt-*");

    private static string Valid() => RelaySecret.Generate();

    // ---------- location ----------

    [Fact]
    public void File_LivesNextToThePluginConfigFolder_NamedAfterThePlugin()
    {
        var secret = NewStore().EnsureSecret(A, out var generated);
        Assert.True(generated);
        Assert.True(File.Exists(FilePath));
        Assert.Empty(Directory.GetFiles(ConfigDir)); // nothing inside pluginConfigs/WindUpKey/
        Assert.Equal(secret, (string?)ReadFile()["characters"]![A]!["secret"]);

        // A trailing separator on the config folder changes nothing.
        var store = new RelayIdentityStore(ConfigDir + Path.DirectorySeparatorChar, "WindUpKey", _warnings.Add);
        Assert.Equal(secret, store.GetSecret(A));
    }

    [Fact]
    public void FirstConnect_GeneratesAndSaves_ThenEveryLaterStoreReadsTheSameSecret()
    {
        var store = NewStore();
        Assert.Equal("", store.GetSecret(A));
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);

        var secret = store.EnsureSecret(A, out var generated);
        Assert.True(generated);
        Assert.True(RelaySecret.IsValid(secret));
        Assert.Equal(secret, store.EnsureSecret(A, out generated));
        Assert.False(generated);

        var reloaded = NewStore();
        Assert.Equal(secret, reloaded.EnsureSecret(A, out generated));
        Assert.False(generated);
        Assert.Equal("", reloaded.GetSecret(B));
        Assert.Equal(secret, NewStore().GetSecret(A.ToLowerInvariant()));

        var json = ReadFile();
        Assert.Equal(1, (int)json["version"]!);
        Assert.Null(json["characters"]![A]!["previous"]);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void FileInsideTheConfigFolder_IsIgnored()
    {
        // Decision 32: the sibling file is the only location; a relay-identity.json inside the folder is never read or moved.
        var content = $"{{ \"version\": 1, \"characters\": {{ \"{A}\": {{ \"secret\": \"{Valid()}\" }} }} }}";
        var inside = Path.Combine(ConfigDir, "relay-identity.json");
        File.WriteAllText(inside, content);

        var store = NewStore();
        Assert.Equal("", store.GetSecret(A));
        Assert.True(store.EnsureSecret(A, out var generated) is not null && generated);
        Assert.Equal(content, File.ReadAllText(inside));
    }

    // ---------- atomic replace + rollback ----------

    [Fact]
    public void SuccessfulSave_LeavesNoTempFile()
    {
        var store = NewStore();
        store.EnsureSecret(A, out _);
        store.Import(A, Valid());
        Assert.DoesNotContain(Directory.GetFiles(ConfigsDir), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_GoesThroughATempFile_SoAFailedWriteLeavesTheOldFileIntact_AndRollsBack()
    {
        var store = NewStore();
        var original = store.EnsureSecret(A, out _)!;
        var before = File.ReadAllBytes(FilePath);

        // Block the temp file: a write that went straight to the real file would still succeed here.
        Directory.CreateDirectory(FilePath + ".tmp");
        var replacement = Valid();

        // (A folder can't be opened as a file: that is refused for access, like a read-only file.)
        Assert.Equal(RelayIdentityImport.NotWritable, store.Import(A, replacement));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Equal(original, store.GetSecret(A));
        Assert.False(store.CanRevert(A));
        Assert.Equal(RelayIdentityStoreStatus.NotWritable, store.Status);
        Assert.Contains(_warnings, w => w.Contains("could not save", StringComparison.Ordinal));
        Assert.DoesNotContain(_warnings, w => w.Contains(original, StringComparison.Ordinal) || w.Contains(replacement, StringComparison.Ordinal));

        // Revert of a failed save also leaves everything as it was.
        Directory.Delete(FilePath + ".tmp");
        Assert.Equal(RelayIdentityImport.Replace, store.Import(A, replacement));
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);
        Directory.CreateDirectory(FilePath + ".tmp");
        var saved = File.ReadAllBytes(FilePath);
        Assert.False(store.Revert(A));
        Assert.Equal(saved, File.ReadAllBytes(FilePath));
        Assert.Equal(replacement, store.GetSecret(A));
        Assert.Equal(original, (string?)ReadFile()["characters"]![A]!["previous"]);
        // Not offered while saves are refused; offered again once the file can be saved.
        Assert.False(store.CanRevert(A));
        Directory.Delete(FilePath + ".tmp");
        Assert.Equal(replacement, store.EnsureSecret(A, out _));
        Assert.True(store.CanRevert(A));
    }

    [Fact]
    public void NewSecret_ThatCannotBeSaved_IsNotHandedOut()
    {
        Directory.CreateDirectory(FilePath + ".tmp");
        var store = NewStore();

        Assert.Null(store.EnsureSecret(A, out var generated));
        Assert.False(generated);
        Assert.Equal("", store.GetSecret(A));
    }

    // ---------- quarantine: only bad JSON / validation at a known version ----------

    /// <summary>Files that don't parse at a known version: quarantined whole.</summary>
    public static TheoryData<string> CorruptFiles => new()
    {
        "{ not json",
        "[]",
        "[{ \"version\": \"x\" }]",
        "\"version\"",
        "true",
        "{ \"characters\": {} }",
        "{ \"version\": 0, \"characters\": {} }",
        "{ \"version\": -1, \"characters\": {} }",
        "{ \"version\": 1, \"characters\": [] }",
        "{ \"version\": 1, \"characters\": \"x\" }",
    };

    /// <summary>Known-version files where only A's entry is invalid: B's valid entry survives.</summary>
    public static TheoryData<string> FilesWithAnInvalidEntry => new()
    {
        $"\"{A}\": {{ \"secret\": \"not-a-valid-secret\" }}",
        $"\"{A}\": {{ \"secret\": 5 }}",
        $"\"{A}\": {{ \"secret\": \"short\", \"previous\": [] }}",
        $"\"{A}\": {{ \"secret\": \"{Valid()}\", \"secret\": \"{Valid()}\" }}",
        $"\"{A}\": \"something\"",
        $"\"{A}\": null",
        $"\"{A}\": {{ \"secret\": \"{Valid()}\" }}, \"{A.ToLowerInvariant()}\": {{ \"secret\": \"{Valid()}\" }}",
        $"\"{A}\": {{ \"secret\": \"short\" }}, \"{A.ToLowerInvariant()}\": {{ \"secret\": \"{Valid()}\" }}",
        $"\" \": {{ \"secret\": \"{Valid()}\" }}",
    };

    [Theory]
    [MemberData(nameof(CorruptFiles))]
    public void CorruptFile_IsKeptAside_WarnedWithoutValues_AndTheStoreStartsEmpty(string content)
    {
        File.WriteAllText(FilePath, content);
        var store = NewStore();

        Assert.Equal("", store.GetSecret(A));

        var kept = FilePath + ".corrupt-20261011T123456Z";
        Assert.True(File.Exists(kept), "corrupt file not kept aside");
        Assert.Equal(content, File.ReadAllText(kept));
        Assert.False(File.Exists(FilePath));

        var warning = Assert.Single(_warnings);
        Assert.DoesNotContain(content, warning);
        Assert.DoesNotContain("not-a-valid-secret", warning);

        var secret = store.EnsureSecret(A, out var generated);
        Assert.True(generated);
        Assert.Equal(secret, NewStore().GetSecret(A));
        Assert.Equal(content, File.ReadAllText(kept));
    }

    [Theory]
    [MemberData(nameof(FilesWithAnInvalidEntry))]
    public void InvalidEntries_AreDropped_ValidOnesSurvive_AndTheOriginalIsCopiedAside(string badEntries)
    {
        var b = Valid();
        var content = $$"""
            { "version": 1, "note": "kept", "characters": { {{badEntries}},
                "{{B}}": { "secret": "{{b}}", "label": "alt" } } }
            """;
        File.WriteAllText(FilePath, content);
        var store = NewStore();

        Assert.Equal(b, store.GetSecret(B));
        Assert.Equal("", store.GetSecret(A));
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);

        // The original is kept aside, unchanged.
        var kept = FilePath + ".corrupt-20261011T123456Z";
        Assert.True(File.Exists(kept), "original not copied aside");
        Assert.Equal(content, File.ReadAllText(kept));

        // The file is rewritten with only the valid entries, unknown fields included.
        var json = ReadFile();
        var chars = json["characters"]!.AsObject();
        Assert.Equal([B], chars.Select(kv => kv.Key));
        Assert.Equal(b, (string?)chars[B]!["secret"]);
        Assert.Equal("alt", (string?)chars[B]!["label"]);
        Assert.Equal("kept", (string?)json["note"]);

        // Said once, without values; a later read doesn't copy it aside again.
        var warning = Assert.Single(_warnings);
        Assert.Contains("dropped only the invalid values", warning);
        Assert.DoesNotContain(b, warning);
        Assert.Equal(b, NewStore().GetSecret(B));
        Assert.Single(Corrupted());

        Assert.True(store.EnsureSecret(A, out var generated) is not null && generated);
        Assert.Equal(b, NewStore().GetSecret(B));
    }

    [Fact]
    public void EntryCorruptedByAnotherClient_IsRestoredFromWhatThisClientRead_OthersKept()
    {
        var store = NewStore();
        var a = store.EnsureSecret(A, out _)!;
        var b = Valid();
        File.WriteAllText(FilePath, $$"""
            { "version": 1, "characters": { "{{A}}": { "secret": "garbage" }, "{{B}}": { "secret": "{{b}}" } } }
            """);

        var c = store.EnsureSecret(C, out var generated);
        Assert.True(generated);
        Assert.Single(Corrupted());
        var reloaded = NewStore();
        Assert.Equal(a, reloaded.GetSecret(A));
        Assert.Equal(b, reloaded.GetSecret(B));
        Assert.Equal(c, reloaded.GetSecret(C));
    }

    [Fact]
    public void InvalidEntries_InANewerFile_AreNeverRewritten()
    {
        var content = $$"""
            { "version": 2, "characters": { "{{A}}": { "secret": "garbage" }, "{{B}}": { "secret": "{{Valid()}}" } } }
            """;
        File.WriteAllText(FilePath, content);
        NewStore().GetSecret(B);
        Assert.Equal(content, File.ReadAllText(FilePath));
        Assert.Empty(Corrupted());
    }

    [Fact]
    public void SecondCorruptFile_InTheSameSecond_DoesNotOverwriteTheFirst()
    {
        File.WriteAllText(FilePath, "first");
        NewStore().GetSecret(A);
        File.WriteAllText(FilePath, "second");
        NewStore().GetSecret(A);

        var stamp = FilePath + ".corrupt-20261011T123456Z";
        Assert.Equal("first", File.ReadAllText(stamp));
        Assert.Equal("second", File.ReadAllText(stamp + "-1"));
    }

    [Fact]
    public void FileCorruptedByAnotherClient_IsKeptAsideAtWriteTime_AndKnownEntriesSurvive()
    {
        var store = NewStore();
        var a = store.EnsureSecret(A, out _)!;
        File.WriteAllText(FilePath, "{ broken");

        var b = store.EnsureSecret(B, out var generated);
        Assert.True(generated);
        Assert.Single(Corrupted());
        Assert.Equal("{ broken", File.ReadAllText(Corrupted()[0]));
        var reloaded = NewStore();
        Assert.Equal(a, reloaded.GetSecret(A));
        Assert.Equal(b, reloaded.GetSecret(B));
    }

    // ---------- newer version: read-only ----------

    private string NewerFile(out string secretA, out string previousA)
    {
        secretA = Valid();
        previousA = Valid();
        var content = $$"""
            {
              "version": 2,
              "future": { "x": 1 },
              "characters": {
                "{{A}}": { "secret": "{{secretA}}", "previous": "{{previousA}}", "kind": "v2" },
                "{{B}}": { "secret": { "sealed": "abc" } },
                "{{C}}": "something new"
              }
            }
            """;
        File.WriteAllText(FilePath, content);
        return content;
    }

    [Fact]
    public void NewerFile_IsUsedReadOnly_NeverMovedOrWritten()
    {
        var content = NewerFile(out var secretA, out _);
        var store = NewStore();

        // Entries this build understands are usable.
        Assert.Equal(secretA, store.GetSecret(A));
        Assert.Equal(secretA, store.EnsureSecret(A, out var generated));
        Assert.False(generated);
        Assert.Equal(RelayIdentityStoreStatus.ReadOnly, store.Status);

        // Missing (or not understood) entries: unavailable instead of generating.
        Assert.Null(store.EnsureSecret(B, out generated));
        Assert.False(generated);
        Assert.Null(store.EnsureSecret(C, out _));
        Assert.Null(new RelayIdentityStore(ConfigDir, "WindUpKey", _ => { }).EnsureSecret("0040003333333333", out _));

        // No import, no revert.
        Assert.Equal(RelayIdentityImport.ReadOnly, store.CheckImport(A, Valid()));
        Assert.Equal(RelayIdentityImport.ReadOnly, store.Import(A, Valid()));
        Assert.Equal(RelayIdentityImport.Unchanged, store.Import(A, secretA));
        Assert.Equal(RelayIdentityImport.ReadOnly, store.Import(B, Valid()));
        Assert.False(store.CanRevert(A));
        Assert.False(store.Revert(A));

        Assert.Equal(content, File.ReadAllText(FilePath));
        Assert.Empty(Corrupted());
        Assert.DoesNotContain(Directory.GetFiles(ConfigsDir), f => f.EndsWith(".tmp", StringComparison.Ordinal));

        // Said once, without values.
        var warning = Assert.Single(_warnings);
        Assert.Contains("newer", warning);
        Assert.DoesNotContain(secretA, warning);
    }

    /// <summary>
    /// A <c>version</c> this build can't read as an integer can only come from a newer build (decision 38): read-only,
    /// like a higher version, never quarantined.
    /// </summary>
    [Theory]
    [InlineData("\"1\"")]
    [InlineData("\"2.0\"")]
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("99999999999999999999")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{ \"major\": 2 }")]
    [InlineData("[2, 0]")]
    public void VersionThatIsNotAnInteger_CountsAsNewer_ReadOnly(string version)
    {
        var secretA = Valid();
        var content = $$"""{ "version": {{version}}, "characters": { "{{A}}": { "secret": "{{secretA}}" } } }""";
        File.WriteAllText(FilePath, content);
        var store = NewStore();

        Assert.Equal(secretA, store.EnsureSecret(A, out var generated));
        Assert.False(generated);
        Assert.Equal(RelayIdentityStoreStatus.ReadOnly, store.Status);
        Assert.Null(store.EnsureSecret(B, out _));
        Assert.Equal(RelayIdentityImport.ReadOnly, store.Import(A, Valid()));

        Assert.Equal(content, File.ReadAllText(FilePath));
        Assert.Empty(Corrupted());
        Assert.Contains("newer", Assert.Single(_warnings));
    }

    [Fact]
    public void NewerFile_AppearingAfterLoad_StopsWrites()
    {
        var store = NewStore();
        store.EnsureSecret(A, out _);
        var content = NewerFile(out _, out _);

        Assert.Null(store.EnsureSecret("0040003333333333", out _));
        Assert.Equal(content, File.ReadAllText(FilePath));
        Assert.Equal(RelayIdentityStoreStatus.ReadOnly, store.Status);
        Assert.Empty(Corrupted());
    }

    // ---------- unknown fields ----------

    [Fact]
    public void UnknownFields_SurviveARewrite()
    {
        var secretA = Valid();
        File.WriteAllText(FilePath, $$"""
            { "version": 1, "note": { "by": "future" }, "characters": {
                "{{A}}": { "secret": "{{secretA}}", "label": "main", "flags": [1, 2] } } }
            """);
        var store = NewStore();
        store.EnsureSecret(B, out _);
        store.Import(A, Valid());

        var json = ReadFile();
        Assert.Equal("future", (string?)json["note"]!["by"]);
        Assert.Equal("main", (string?)json["characters"]![A]!["label"]);
        Assert.Equal(2, json["characters"]![A]!["flags"]!.AsArray().Count);
        Assert.Equal(secretA, (string?)json["characters"]![A]!["previous"]);
        Assert.Empty(_warnings);
        Assert.Empty(Corrupted());
    }

    // ---------- two clients on one file ----------

    [Fact]
    public void TwoStores_MergeInsteadOfErasingEachOther()
    {
        var p1 = NewStore();
        var a = p1.EnsureSecret(A, out _)!;

        var p2 = NewStore();
        Assert.Equal(a, p2.GetSecret(A));
        var b = p2.EnsureSecret(B, out _)!;

        var c = p1.EnsureSecret(C, out _)!;

        var chars = ReadFile()["characters"]!.AsObject();
        Assert.Equal(new[] { A, B, C }.Order(), chars.Select(kv => kv.Key).Order());
        Assert.Equal(a, (string?)chars[A]!["secret"]);
        Assert.Equal(b, (string?)chars[B]!["secret"]);
        Assert.Equal(c, (string?)chars[C]!["secret"]);
        Assert.Equal(b, p1.GetSecret(B)); // and p1's view caught up
    }

    [Fact]
    public void ImportAndRevert_FromAStaleStore_KeepOtherClientsEntries()
    {
        var p1 = NewStore();
        p1.EnsureSecret(A, out _);
        var p2 = NewStore();
        var b = p2.EnsureSecret(B, out _)!;

        var imported = Valid();
        Assert.Equal(RelayIdentityImport.Replace, p1.Import(A, imported));
        Assert.Equal(b, (string?)ReadFile()["characters"]![B]!["secret"]);

        var c = p2.EnsureSecret(C, out _)!;
        Assert.True(p1.Revert(A));
        var chars = ReadFile()["characters"]!;
        Assert.Equal(b, (string?)chars[B]!["secret"]);
        Assert.Equal(c, (string?)chars[C]!["secret"]);
        Assert.Equal(imported, (string?)chars[A]!["previous"]);
    }

    [Fact]
    public void Connect_UsesTheValueOnDisk_NotAStaleCache()
    {
        var p1 = NewStore();
        var original = p1.EnsureSecret(A, out _)!;
        var imported = Valid();
        Assert.Equal(RelayIdentityImport.Replace, NewStore().Import(A, imported)); // another client

        Assert.Equal(imported, p1.EnsureSecret(A, out var generated));
        Assert.False(generated);
        Assert.Equal(imported, p1.GetSecret(A)); // and the view caught up
        Assert.Equal(original, (string?)ReadFile()["characters"]![A]!["previous"]);
    }

    [Fact]
    public void CheckImport_ComparesAgainstTheFileOnDisk_NotAStaleCache()
    {
        var p1 = NewStore();
        var original = p1.EnsureSecret(A, out _)!;
        var imported = Valid();
        NewStore().Import(A, imported); // another client

        Assert.Equal(RelayIdentityImport.Unchanged, p1.CheckImport(A, imported));
        Assert.Equal(RelayIdentityImport.Unchanged, p1.Import(A, imported));
        Assert.Equal(RelayIdentityImport.Replace, p1.CheckImport(A, original));
        Assert.Equal(imported, p1.GetSecret(A));
    }

    [Fact]
    public void UncachedCharacter_IsReReadBeforeGenerating()
    {
        var p1 = NewStore();
        Assert.Equal("", p1.GetSecret(A)); // p1 has loaded: no A

        var fromOtherClient = NewStore().EnsureSecret(A, out _)!;

        Assert.Equal(fromOtherClient, p1.EnsureSecret(A, out var generated));
        Assert.False(generated);
        Assert.Equal(fromOtherClient, (string?)ReadFile()["characters"]![A]!["secret"]);
    }

    [Fact]
    public void ConcurrentWriters_OnParallelThreads_AllLand()
    {
        const int threads = 8, each = 12;
        using var go = new ManualResetEventSlim();
        var results = new string?[threads, each];
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            go.Wait();
            for (var i = 0; i < each; i++)
            {
                // A long lock wait: this checks mutual exclusion, not the framework-thread timeout.
                var store = new RelayIdentityStore(ConfigDir, "WindUpKey", _ => { }, lockWait: TimeSpan.FromSeconds(10));
                results[t, i] = store.EnsureSecret($"00T{t:X2}{i:X11}", out _);
            }
        })).ToList();
        workers.ForEach(w => w.Start());
        go.Set();
        workers.ForEach(w => w.Join());

        var chars = ReadFile()["characters"]!.AsObject();
        Assert.Equal(threads * each, chars.Count);
        for (var t = 0; t < threads; t++)
        {
            for (var i = 0; i < each; i++)
            {
                Assert.NotNull(results[t, i]);
                Assert.Equal(results[t, i], (string?)chars[$"00T{t:X2}{i:X11}"]!["secret"]);
            }
        }
    }

    [Fact]
    public void ConcurrentWriters_InSeparateProcesses_AllLand()
    {
        // This test assembly doubles as the worker (see Program); the CLI says which dotnet host runs it.
        var worker = typeof(Program).Assembly.Location;
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path ? path : "dotnet";
        const int processes = 4, each = 15;
        // A start barrier that doesn't depend on how long the processes take to start: each worker says it is
        // ready, and they all start writing once the "go" file appears.
        var barrier = Path.Combine(_root, "barrier");
        Directory.CreateDirectory(barrier);
        var procs = Enumerable.Range(0, processes).Select(p => Process.Start(new ProcessStartInfo(host,
            [worker, Program.StoreWorkerCommand, ConfigDir, $"00P{p:X2}", each.ToString(), barrier])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!).ToList();
        var ready = Stopwatch.StartNew();
        while (Directory.GetFiles(barrier, "ready-*").Length < processes)
        {
            Assert.True(ready.Elapsed < TimeSpan.FromSeconds(60), "workers did not start");
            Assert.DoesNotContain(procs, p => p.HasExited);
            Thread.Sleep(10);
        }

        File.WriteAllText(Path.Combine(barrier, "go"), "");
        foreach (var proc in procs)
        {
            Assert.True(proc.WaitForExit(60_000));
            Assert.True(proc.ExitCode == 0, proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd());
        }

        var chars = ReadFile()["characters"]!.AsObject();
        Assert.Equal(processes * each, chars.Count);
    }

    // ---------- lock ----------

    [Fact]
    public void LockHeldByAnotherClient_WritesWaitThenReportUnavailable_WithoutTouchingTheFile()
    {
        var store = NewStore();
        var a = store.EnsureSecret(A, out _)!;
        var before = File.ReadAllBytes(FilePath);

        // A short wait: this runs on the framework thread. The connect retry tries again later.
        Assert.True(RelayIdentityStore.DefaultLockWait <= TimeSpan.FromMilliseconds(250), RelayIdentityStore.DefaultLockWait.ToString());
        using (new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            // It does wait for the other client (a lower bound a slow machine can't break), but not for long (a
            // generous upper bound, so only a much longer wait fails it; the limit itself is checked above).
            var watch = Stopwatch.StartNew();
            Assert.Null(store.EnsureSecret(B, out _));
            Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(100), watch.Elapsed.ToString());
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
            Assert.Equal(RelayIdentityImport.StoreUnavailable, store.Import(A, Valid()));
            Assert.Equal(before, File.ReadAllBytes(FilePath));
            Assert.Equal(a, store.GetSecret(A));
            Assert.Equal(RelayIdentityStoreStatus.Unavailable, store.Status);
        }

        Assert.NotNull(store.EnsureSecret(B, out _));
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);
        var warning = Assert.Single(_warnings);
        Assert.Contains("locked by another game client", warning);
    }

    [Fact]
    public void LeftoverLockFile_FromACrash_DoesNotBlock()
    {
        // If the lock file's existence counted, each attempt would wait out the lock wait and fail: no timing needed.
        File.WriteAllText(LockPath, "");
        Assert.NotNull(NewStore().EnsureSecret(A, out _));
        Assert.NotNull(NewStore().EnsureSecret(B, out _));
        Assert.Empty(_warnings);
    }

    // ---------- a lock file or identity file that can't be written (read-only, permissions) ----------

    [NotAsRootFact]
    public void ReadOnlyLockFile_ItsAttributeIsCleared_AndEverythingWorks()
    {
        // For example after copying pluginConfigs from another PC.
        var a = NewStore().EnsureSecret(A, out _)!;
        File.SetAttributes(LockPath, File.GetAttributes(LockPath) | FileAttributes.ReadOnly);

        var store = NewStore();
        Assert.Equal(a, store.EnsureSecret(A, out _));
        Assert.True(store.EnsureSecret(B, out var generated) is not null && generated);
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);
        Assert.Equal(FileAttributes.None, File.GetAttributes(LockPath) & FileAttributes.ReadOnly);
        var warning = Assert.Single(_warnings);
        Assert.Contains("cleared the read-only attribute", warning);
        Assert.DoesNotContain(a, warning);
    }

    [UnixOnlyFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void LockFileWithMode0444_TheClientStillGetsItsSecret()
    {
        var a = NewStore().EnsureSecret(A, out _)!;
        File.SetUnixFileMode(LockPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var store = NewStore();
        Assert.Equal(a, store.EnsureSecret(A, out var generated));
        Assert.False(generated);
        Assert.NotNull(store.EnsureSecret(B, out _));
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);
    }

    /// <summary>
    /// A folder in the lock file's place: it can't be opened for writing on any OS, and has no read-only attribute to
    /// clear, like a lock file whose permissions deny writing.
    /// </summary>
    private void MakeLockFileUnopenable()
    {
        File.Delete(LockPath);
        Directory.CreateDirectory(LockPath);
    }

    [Fact]
    public void LockFileThatCannotBeOpened_ExistingSecretsWork_WithoutWaiting_AndOnlyChangesAreRefused()
    {
        var a = NewStore().EnsureSecret(A, out _)!;
        var before = File.ReadAllBytes(FilePath);
        MakeLockFileUnopenable();
        // A long lock wait: refused access must not wait for it, since no other client is involved.
        var store = new RelayIdentityStore(ConfigDir, "WindUpKey", _warnings.Add, () => _now, TimeSpan.FromSeconds(30));

        var watch = Stopwatch.StartNew();
        Assert.Equal(a, store.EnsureSecret(A, out var generated)); // read without the lock: the client still registers
        Assert.False(generated);
        Assert.Equal(a, store.GetSecret(A));
        Assert.Equal(RelayIdentityStoreStatus.NotWritable, store.Status);

        // Changes are refused (up front where possible) and nothing is written.
        Assert.Null(store.EnsureSecret(B, out generated));
        Assert.False(generated);
        Assert.Equal(RelayIdentityImport.NotWritable, store.CheckImport(A, Valid()));
        Assert.Equal(RelayIdentityImport.NotWritable, store.Import(A, Valid()));
        Assert.Equal(RelayIdentityImport.Unchanged, store.Import(A, a));
        Assert.False(store.Revert(A));
        Assert.Equal(RelayIdentityStoreStatus.NotWritable, store.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Empty(Corrupted());

        // Said once, without values, and not as "another client".
        var warning = Assert.Single(_warnings);
        Assert.Contains("could not open", warning);
        Assert.Contains("not in use by another client", warning);
        Assert.DoesNotContain("locked by another", warning);
        Assert.DoesNotContain(a, warning);

        // Once it can be opened, everything works again.
        Directory.Delete(LockPath);
        Assert.NotNull(store.EnsureSecret(B, out _));
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);
    }

    [Fact]
    public void LockFileThatCannotBeOpened_AKnownSecretMissingOnDisk_IsNotHandedOut()
    {
        var store = NewStore();
        var a = store.EnsureSecret(A, out _)!;
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{B}}": { "secret": "{{Valid()}}" } } }""");
        MakeLockFileUnopenable();

        // It would have to be written back first, and nothing can be written.
        Assert.Null(store.EnsureSecret(A, out _));
        Assert.Equal(RelayIdentityStoreStatus.NotWritable, store.Status);

        Directory.Delete(LockPath);
        Assert.Equal(a, store.EnsureSecret(A, out var generated));
        Assert.False(generated);
        Assert.Equal(a, (string?)ReadFile()["characters"]![A]!["secret"]);
    }

    [Fact]
    public void LockFileThatCannotBeOpened_DamagedFile_IsNeitherMovedNorRewritten()
    {
        var b = Valid();
        var partly = $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "garbage" }, "{{B}}": { "secret": "{{b}}" } } }""";
        File.WriteAllText(FilePath, partly);
        MakeLockFileUnopenable();

        // Partly invalid: its valid entries are used.
        var store = NewStore();
        Assert.Equal(b, store.EnsureSecret(B, out _));
        Assert.Null(store.EnsureSecret(A, out _));
        Assert.Equal(RelayIdentityStoreStatus.NotWritable, store.Status);
        Assert.Equal(partly, File.ReadAllText(FilePath));

        // Doesn't parse: left where it is.
        File.WriteAllText(FilePath, "{ broken");
        var other = NewStore();
        Assert.Equal("", other.GetSecret(B));
        Assert.Null(other.EnsureSecret(B, out _));
        Assert.Equal(RelayIdentityStoreStatus.NotWritable, other.Status);
        Assert.Equal("{ broken", File.ReadAllText(FilePath));
        Assert.Empty(Corrupted());
    }

    [Fact]
    public void ReadOnlyIdentityFile_CanStillBeSaved()
    {
        var a = NewStore().EnsureSecret(A, out _)!;
        File.SetAttributes(FilePath, File.GetAttributes(FilePath) | FileAttributes.ReadOnly);

        var store = NewStore();
        Assert.Equal(a, store.EnsureSecret(A, out _));
        var b = store.EnsureSecret(B, out _);
        Assert.NotNull(b);
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);
        Assert.Equal(b, (string?)ReadFile()["characters"]![B]!["secret"]);
    }

    [Fact]
    public void ReadOnlyLeftoverTempFile_ItsAttributeIsCleared_AndTheSaveWorks()
    {
        File.WriteAllText(FilePath + ".tmp", "left by a copy");
        File.SetAttributes(FilePath + ".tmp", FileAttributes.ReadOnly);

        var store = NewStore();
        var a = store.EnsureSecret(A, out var generated);
        Assert.True(a is not null && generated);
        Assert.Equal(a, (string?)ReadFile()["characters"]![A]!["secret"]);
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [UnixPermissionFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void IdentityFileThatCannotBeWritten_ExistingSecretsWork_AndOnlyChangesAreRefused()
    {
        var a = NewStore().EnsureSecret(A, out _)!;
        var before = File.ReadAllBytes(FilePath);
        var mode = File.GetUnixFileMode(ConfigsDir);
        // The folder refuses new files (the temp file), so nothing can be saved; the lock file already exists.
        File.SetUnixFileMode(ConfigsDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var store = NewStore();
            Assert.Equal(a, store.EnsureSecret(A, out _));
            Assert.Null(store.EnsureSecret(B, out _));
            Assert.Equal(RelayIdentityStoreStatus.NotWritable, store.Status);
            Assert.Equal(RelayIdentityImport.NotWritable, store.Import(A, Valid()));
            Assert.Null(store.EnsureSecret(B, out _));
            Assert.Equal(a, store.EnsureSecret(A, out _));
            Assert.Equal(before, File.ReadAllBytes(FilePath));

            var warning = Assert.Single(_warnings);
            Assert.Contains("could not save", warning);
            Assert.Contains("not writable", warning);
            Assert.DoesNotContain(a, warning);
        }
        finally
        {
            File.SetUnixFileMode(ConfigsDir, mode);
        }

        Assert.NotNull(NewStore().EnsureSecret(B, out _));
    }

    // ---------- read errors are not corruption ----------

    [Fact]
    public void FileHeldOpenByAnotherProgram_IsUnavailable_NotQuarantined_AndRetried()
    {
        var secret = NewStore().EnsureSecret(A, out _)!;
        var store = NewStore();

        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal("", store.GetSecret(A));
            Assert.Null(store.EnsureSecret(A, out _));
            Assert.Equal(RelayIdentityImport.StoreUnavailable, store.CheckImport(A, Valid()));
            Assert.Equal(RelayIdentityStoreStatus.Unavailable, store.Status);
        }

        Assert.Empty(Corrupted());
        Assert.True(File.Exists(FilePath));
        Assert.Equal("", store.GetSecret(A)); // waits for the retry delay

        _now = _now.AddSeconds(31);
        Assert.Equal(secret, store.GetSecret(A));
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);
    }

    [Fact]
    public void AfterAFailedRead_TheConnectPathAndResetRetryDelay_RetryWithoutWaitingTheDelay()
    {
        var secret = NewStore().EnsureSecret(A, out _)!;
        var store = NewStore();
        var other = NewStore();

        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal("", store.GetSecret(A));
            Assert.Equal("", other.GetSecret(A));
        }

        // The connect path reads the file on every attempt (on the reconnect backoff).
        Assert.Equal(secret, store.EnsureSecret(A, out _));
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);

        // Start() / Reconnect: the UI's next read retries now too.
        Assert.Equal("", other.GetSecret(A));
        other.ResetRetryDelay();
        Assert.Equal(secret, other.GetSecret(A));
    }

    [Fact]
    public void FailedConnectRead_AlsoDelaysTheUisRetry_SoTheUiDoesNotWaitOnTheLockEveryFrame()
    {
        var secret = NewStore().EnsureSecret(A, out _)!;
        var store = NewStore();
        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(store.EnsureSecret(A, out _)); // nothing loaded yet

        Assert.Equal("", store.GetSecret(A));
        _now = _now.AddSeconds(31);
        Assert.Equal(secret, store.GetSecret(A));
    }

    [UnixPermissionFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void FileWithoutReadAccess_IsUnavailable_NotQuarantined()
    {
        var secret = NewStore().EnsureSecret(A, out _)!;
        File.SetUnixFileMode(FilePath, UnixFileMode.None);
        var store = NewStore();

        Assert.Equal("", store.GetSecret(A));
        Assert.Null(store.EnsureSecret(B, out _));
        Assert.Empty(Corrupted());
        Assert.Contains(_warnings, w => w.Contains("UnauthorizedAccessException", StringComparison.Ordinal));

        File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _now = _now.AddSeconds(31);
        Assert.Equal(secret, store.GetSecret(A));
    }

    [Fact]
    public void UnavailableState_IsLoggedOnce_NotOnEveryRetry_AndAgainAfterRecovering()
    {
        NewStore().EnsureSecret(A, out _);
        var store = NewStore();

        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            for (var i = 0; i < 4; i++)
            {
                _now = _now.AddSeconds(31);
                Assert.Equal("", store.GetSecret(A));
            }
        }

        Assert.Single(_warnings);

        _now = _now.AddSeconds(31);
        Assert.NotEqual("", store.GetSecret(A));

        // Recovered, then the same failure again (a write re-reads the file): logged again, once.
        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(store.EnsureSecret(B, out _));
            Assert.Null(store.EnsureSecret(B, out _));
            Assert.Null(store.EnsureSecret(B, out _));
        }

        Assert.Equal(2, _warnings.Count);
        Assert.Equal(_warnings[0], _warnings[1]);
    }

    // ---------- import / revert ----------

    [Fact]
    public void Import_KeepsThePrevious_Persists_AndRevertSwapsBack()
    {
        var store = NewStore();
        var original = store.EnsureSecret(A, out _)!;
        var imported = Valid();
        Assert.False(store.CanRevert(A));

        Assert.Equal(RelayIdentityImport.Replace, store.CheckImport(A, imported));
        Assert.Equal(original, store.GetSecret(A));
        Assert.Equal(RelayIdentityImport.Replace, store.Import(A, imported));
        Assert.Equal(imported, store.GetSecret(A));
        Assert.True(store.CanRevert(A));

        var reloaded = NewStore();
        Assert.Equal(imported, reloaded.GetSecret(A));
        Assert.True(reloaded.CanRevert(A));
        Assert.Equal(original, (string?)ReadFile()["characters"]![A]!["previous"]);

        Assert.True(reloaded.Revert(A));
        Assert.Equal(original, reloaded.GetSecret(A));
        Assert.Equal(original, NewStore().GetSecret(A));
        Assert.Equal(imported, (string?)ReadFile()["characters"]![A]!["previous"]);

        Assert.Equal("", reloaded.GetSecret(B));
        Assert.False(reloaded.Revert(B));
    }

    [Fact]
    public void Import_OfTheCurrentSecret_ChangesNothing()
    {
        var store = NewStore();
        var current = store.EnsureSecret(A, out _)!;
        var before = File.ReadAllBytes(FilePath);

        Assert.Equal(RelayIdentityImport.Unchanged, store.Import(A, "  " + current + "=\n"));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.False(store.CanRevert(A));
    }

    [Fact]
    public void Import_RejectsInvalidValues_AndNeedsACharacter()
    {
        var store = NewStore();
        store.EnsureSecret(A, out _);
        Assert.Equal(RelayIdentityImport.Invalid, store.Import(A, "not a secret"));
        Assert.Equal(RelayIdentityImport.NoCharacter, store.Import("", Valid()));
        Assert.False(store.CanRevert(A));
    }

    [Fact]
    public void Import_NormalizesThePastedValue()
    {
        var secret = Valid();
        var pasted = " " + secret.Replace('-', '+').Replace('_', '/') + "= ";
        Assert.Equal(RelayIdentityImport.Replace, NewStore().Import(A, pasted));
        Assert.Equal(secret, NewStore().GetSecret(A));
    }

    [Fact]
    public void Import_OnACharacterWithNoSecretYet_HasNothingToRevertTo()
    {
        var store = NewStore();
        Assert.Equal(RelayIdentityImport.Replace, store.Import(A, Valid()));
        Assert.False(store.CanRevert(A));
    }

    [Fact]
    public void Import_WhenTheFileLostTheEntry_KeepsTheSecretThisClientKnowsAsThePrevious()
    {
        var store = NewStore();
        var original = store.EnsureSecret(A, out _)!;
        // Another client rewrote the file without A (or it was damaged and moved aside).
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{B}}": { "secret": "{{Valid()}}" } } }""");

        var imported = Valid();
        Assert.Equal(RelayIdentityImport.Replace, store.Import(A, imported));
        var entry = ReadFile()["characters"]![A]!;
        Assert.Equal(imported, (string?)entry["secret"]);
        Assert.Equal(original, (string?)entry["previous"]);
        Assert.True(store.CanRevert(A));
        Assert.True(store.Revert(A));
        Assert.Equal(original, NewStore().GetSecret(A));
    }

    [Fact]
    public void Import_WhenTheFileLostTheEntry_KeepsTheKnownPrevious_WhenTheKnownSecretIsTheOneImported()
    {
        var store = NewStore();
        var original = store.EnsureSecret(A, out _)!;
        var imported = Valid();
        store.Import(A, imported); // knows { imported, previous: original }
        File.Delete(FilePath);

        // Never its own previous: the known previous is kept instead.
        Assert.Equal(RelayIdentityImport.Replace, store.Import(A, imported));
        var entry = ReadFile()["characters"]![A]!;
        Assert.Equal(imported, (string?)entry["secret"]);
        Assert.Equal(original, (string?)entry["previous"]);
    }

    // ---------- revert is offered only while the file can be changed ----------

    private RelayIdentityStore StoreWithAPrevious(out string original, out string imported)
    {
        var store = NewStore();
        original = store.EnsureSecret(A, out _)!;
        imported = Valid();
        Assert.Equal(RelayIdentityImport.Replace, store.Import(A, imported));
        Assert.True(store.CanRevert(A));
        return store;
    }

    [Fact]
    public void CanRevert_IsFalse_WhileTheLockFileCannotBeOpened()
    {
        var store = StoreWithAPrevious(out var original, out var imported);
        MakeLockFileUnopenable();
        Assert.Equal(imported, store.EnsureSecret(A, out _)); // re-read without the lock
        Assert.Equal(RelayIdentityStoreStatus.NotWritable, store.Status);
        Assert.False(store.CanRevert(A));
        Assert.False(store.Revert(A));

        Directory.Delete(LockPath);
        Assert.Equal(imported, store.EnsureSecret(A, out _));
        Assert.True(store.CanRevert(A));
        Assert.True(store.Revert(A));
        Assert.Equal(original, store.GetSecret(A));
    }

    [Fact]
    public void CanRevert_IsFalse_AfterASaveWasRefusedForAccess()
    {
        var store = StoreWithAPrevious(out _, out _);
        Directory.CreateDirectory(FilePath + ".tmp"); // the temp file can't be created: refused for access
        Assert.Null(store.EnsureSecret(B, out _));
        Assert.Equal(RelayIdentityStoreStatus.NotWritable, store.Status);
        Assert.False(store.CanRevert(A));

        Directory.Delete(FilePath + ".tmp");
        Assert.NotNull(store.EnsureSecret(B, out _));
        Assert.True(store.CanRevert(A));
    }

    [Fact]
    public void CanRevert_IsFalse_ForAPartlyInvalidFileThatCannotBeCopiedAside()
    {
        string original = Valid(), imported = Valid();
        File.WriteAllText(
            FilePath,
            $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "{{imported}}", "previous": "{{original}}" }, "{{B}}": { "secret": "garbage" } } }""");
        var copyName = FilePath + ".corrupt-20261011T123456Z";
        Directory.CreateDirectory(copyName);

        var store = NewStore();
        Assert.Equal(imported, store.GetSecret(A));
        Assert.False(store.CanRevert(A));
        Assert.False(store.Revert(A));

        Directory.Delete(copyName);
        Assert.Equal(RelayIdentityImport.Unchanged, store.CheckImport(A, imported)); // re-read: copied aside, rewritten
        Assert.True(store.CanRevert(A));
        Assert.True(store.Revert(A));
        Assert.Equal(original, NewStore().GetSecret(A));
    }

    // ---------- decision 26: a secret this client knows is restored, never regenerated ----------

    [Fact]
    public void EntryDroppedByAnotherClientsRewrite_IsRestoredFromMemory_NotRegenerated()
    {
        var x = NewStore();
        var y = NewStore();
        var a = x.EnsureSecret(A, out _)!;   // x knows {A}
        var c = y.EnsureSecret(C, out _)!;   // file {A, C}; y knows both
        File.WriteAllText(FilePath, "{ not json");
        Assert.Equal(a, x.EnsureSecret(A, out _)); // x moves it aside and writes back what it knows: {A}
        Assert.Null(ReadFile()["characters"]![C]);

        Assert.Equal(c, y.EnsureSecret(C, out var generated));
        Assert.False(generated);
        Assert.Equal(c, NewStore().GetSecret(C));
        Assert.Equal(a, NewStore().GetSecret(A));
    }

    [Fact]
    public void KnownSecret_IsRestoredEvenAfterARefreshShowedItMissing()
    {
        var store = NewStore();
        var c = store.EnsureSecret(C, out _)!;
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "{{Valid()}}" } } }""");

        // A refresh makes the file the current view, but doesn't forget what this client knows.
        Assert.Equal(RelayIdentityImport.Replace, store.CheckImport(C, Valid()));
        Assert.Equal("", store.GetSecret(C));

        Assert.Equal(c, store.EnsureSecret(C, out var generated));
        Assert.False(generated);
        Assert.Equal(c, (string?)ReadFile()["characters"]![C]!["secret"]);
    }

    [Fact]
    public void FileDeletedWhileRunning_KnownSecretAndPreviousAreWrittenBack()
    {
        var store = NewStore();
        var original = store.EnsureSecret(A, out _)!;
        var imported = Valid();
        store.Import(A, imported);
        File.Delete(FilePath);

        Assert.Equal(imported, store.EnsureSecret(A, out var generated));
        Assert.False(generated);
        var entry = ReadFile()["characters"]![A]!;
        Assert.Equal(imported, (string?)entry["secret"]);
        Assert.Equal(original, (string?)entry["previous"]);

        // A character this client never knew still gets a new secret.
        Assert.NotNull(store.EnsureSecret(B, out generated));
        Assert.True(generated);
    }

    // ---------- decision 27: salvage works field by field ----------

    [Fact]
    public void InvalidPrevious_IsDropped_TheValidSecretIsKept()
    {
        var a = Valid();
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "{{a}}", "previous": "short", "label": "main" } } }""");
        var store = NewStore();

        Assert.Equal(a, store.GetSecret(A));
        Assert.False(store.CanRevert(A));
        Assert.Single(Corrupted());
        var entry = ReadFile()["characters"]![A]!;
        Assert.Equal(a, (string?)entry["secret"]);
        Assert.Null(entry["previous"]);
        Assert.Equal("main", (string?)entry["label"]);
        Assert.Equal(a, store.EnsureSecret(A, out var generated));
        Assert.False(generated);
    }

    [Fact]
    public void InvalidSecret_IsDropped_TheValidPreviousIsKept()
    {
        var previous = Valid();
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{A}}": { "secret": 7, "previous": "{{previous}}" } } }""");
        var store = NewStore();

        Assert.Equal("", store.GetSecret(A));
        Assert.True(store.CanRevert(A));
        Assert.Equal(previous, (string?)ReadFile()["characters"]![A]!["previous"]);
        Assert.Null(ReadFile()["characters"]![A]!["secret"]);

        // Reverting to it works, so the old identity isn't lost.
        Assert.True(store.Revert(A));
        Assert.Equal(previous, NewStore().GetSecret(A));
    }

    [Fact]
    public void DamagedValues_ThisClientKnows_ArePutBack()
    {
        var store = NewStore();
        var original = store.EnsureSecret(A, out _)!;
        var imported = Valid();
        store.Import(A, imported);

        // The previous is damaged: the known one is put back, since the secret is the same.
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "{{imported}}", "previous": "bad" } } }""");
        Assert.NotNull(store.EnsureSecret(B, out _));
        Assert.Equal(imported, NewStore().GetSecret(A));
        Assert.Equal(original, (string?)ReadFile()["characters"]![A]!["previous"]);

        // The secret is damaged: the known one is put back, and the valid previous on disk is kept.
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "bad", "previous": "{{original}}" } } }""");
        Assert.NotNull(store.EnsureSecret(C, out _));
        var entry = ReadFile()["characters"]![A]!;
        Assert.Equal(imported, (string?)entry["secret"]);
        Assert.Equal(original, (string?)entry["previous"]);
        Assert.Equal(2, Corrupted().Length);
    }

    [Fact]
    public void KnownPrevious_IsNotPutBesideADifferentSecret()
    {
        var store = NewStore();
        store.EnsureSecret(A, out _);
        store.Import(A, Valid()); // store knows {S, P}
        var other = Valid();
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "{{other}}", "previous": "bad" } } }""");

        Assert.Equal(other, store.EnsureSecret(A, out _));
        Assert.Null(ReadFile()["characters"]![A]!["previous"]);
    }

    [Fact]
    public void RestoredSecret_KeepsAValidPreviousOnDisk_OverTheOneThisClientKnows()
    {
        var store = NewStore();
        store.EnsureSecret(A, out _);
        var known = Valid();
        store.Import(A, known); // store knows {known, previous: the first secret}

        // Another client's import left a different previous; then the secret was damaged.
        var otherPrevious = Valid();
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "bad", "previous": "{{otherPrevious}}" } } }""");

        Assert.Equal(known, store.EnsureSecret(A, out var generated));
        Assert.False(generated);
        var entry = ReadFile()["characters"]![A]!;
        Assert.Equal(known, (string?)entry["secret"]);
        Assert.Equal(otherPrevious, (string?)entry["previous"]);
    }

    [Fact]
    public void RestoredSecret_IsNeverItsOwnPrevious()
    {
        var store = NewStore();
        store.EnsureSecret(A, out _);
        var known = Valid();
        store.Import(A, known); // store knows {known, previous: the first secret}

        // On disk the previous is the very secret this client restores (for example after a revert elsewhere).
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "bad", "previous": "{{known}}" } } }""");

        Assert.Equal(known, store.EnsureSecret(A, out _));
        var entry = ReadFile()["characters"]![A]!;
        Assert.Equal(known, (string?)entry["secret"]);
        Assert.Null(entry["previous"]);
        Assert.False(store.CanRevert(A));
    }

    // ---------- a partly invalid file that can't be copied or rewritten ----------

    private string PartlyInvalidFile(out string b)
    {
        b = Valid();
        var content = $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "garbage" }, "{{B}}": { "secret": "{{b}}" } } }""";
        File.WriteAllText(FilePath, content);
        return content;
    }

    [Fact]
    public void PartlyInvalidFile_ThatCannotBeRewritten_IsCopiedOnce_AndItsValidEntriesKeepWorking()
    {
        var content = PartlyInvalidFile(out var b);
        Directory.CreateDirectory(FilePath + ".tmp"); // the rewrite fails (like a read-only or held file)
        var warnings = new List<string>();
        var store = new RelayIdentityStore(ConfigDir, "WindUpKey", warnings.Add, () => _now);

        for (var retry = 0; retry < 5; retry++)
        {
            _now = _now.AddSeconds(31);
            Assert.Equal(b, store.EnsureSecret(B, out var generated)); // the connect path still gets it
            Assert.False(generated);
            Assert.Equal(b, store.GetSecret(B));
            Assert.Equal(RelayIdentityImport.Replace, store.CheckImport(B, Valid()));
        }

        Assert.Single(Corrupted());
        Assert.Equal(content, File.ReadAllText(FilePath));
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);

        // Only changes fail.
        Assert.Null(store.EnsureSecret(C, out _));
        Assert.Equal(RelayIdentityStoreStatus.NotWritable, store.Status);
        Assert.Equal(b, store.EnsureSecret(B, out _));

        // Said once each: the copy, and the failed rewrite. Never a value.
        Assert.Equal(2, warnings.Count);
        Assert.Contains("kept a copy", warnings[0]);
        Assert.Contains("could not save", warnings[1]);
        Assert.DoesNotContain(warnings, w => w.Contains(b, StringComparison.Ordinal));

        // Another client (or a restart) doesn't copy the same content again.
        Assert.Equal(b, NewStore().GetSecret(B));
        Assert.Single(Corrupted());
        Assert.DoesNotContain(_warnings, w => w.Contains("kept a copy", StringComparison.Ordinal));

        // Once it can be written, the next read rewrites it with the valid entries.
        Directory.Delete(FilePath + ".tmp");
        Assert.Equal(b, store.EnsureSecret(B, out _));
        Assert.Equal([B], ReadFile()["characters"]!.AsObject().Select(kv => kv.Key));
        Assert.NotNull(store.EnsureSecret(C, out _));
        Assert.Single(Corrupted());
    }

    [Fact]
    public void PartlyInvalidFile_ThatCannotBeCopiedAside_IsReadButNeverWritten()
    {
        var content = PartlyInvalidFile(out var b);
        // The copy's name is taken by a folder, so copying fails (like a folder that refuses new files).
        var copyName = FilePath + ".corrupt-20261011T123456Z";
        Directory.CreateDirectory(copyName);
        var warnings = new List<string>();
        var store = new RelayIdentityStore(ConfigDir, "WindUpKey", warnings.Add, () => _now);

        // (The clock stays put, so every attempt picks the same copy name.)
        for (var retry = 0; retry < 3; retry++)
        {
            Assert.Equal(b, store.EnsureSecret(B, out _));
            Assert.Equal(b, NewStore().GetSecret(B));
        }

        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);
        Assert.Null(store.EnsureSecret(C, out _));
        Assert.Equal(RelayIdentityStoreStatus.Unavailable, store.Status);

        // An import would fail too: said up front, before the user confirms.
        Assert.Equal(RelayIdentityImport.StoreUnavailable, store.CheckImport(B, Valid()));
        Assert.Equal(RelayIdentityImport.StoreUnavailable, store.Import(B, Valid()));
        Assert.Equal(RelayIdentityImport.Unchanged, store.CheckImport(B, b));
        Assert.Equal(content, File.ReadAllText(FilePath));
        Assert.Empty(Corrupted());
        var warning = Assert.Single(warnings); // once, not on every retry
        Assert.Contains("could not be copied aside", warning);
        Assert.DoesNotContain(b, warning);

        // Once a copy can be kept, it is copied and rewritten.
        Directory.Delete(copyName);
        _now = _now.AddSeconds(1);
        Assert.NotNull(store.EnsureSecret(C, out _));
        var kept = Assert.Single(Corrupted());
        Assert.Equal(content, File.ReadAllText(kept));
        Assert.Equal(b, (string?)ReadFile()["characters"]![B]!["secret"]);
    }

    // ---------- a restored secret is handed out only once it is on disk ----------

    /// <summary>The store knows A's secret; on disk A's secret is damaged and B is valid.</summary>
    private RelayIdentityStore StoreWithADamagedOnDisk(out string a, out string b)
    {
        var store = NewStore();
        a = store.EnsureSecret(A, out _)!;
        b = Valid();
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "garbage" }, "{{B}}": { "secret": "{{b}}" } } }""");
        return store;
    }

    [Fact]
    public void RestoredSecret_ThatCannotBeRewritten_IsNotHandedOut_UntilItIsOnDisk()
    {
        var store = StoreWithADamagedOnDisk(out var a, out var b);
        Directory.CreateDirectory(FilePath + ".tmp"); // the rewrite fails

        for (var retry = 0; retry < 2; retry++)
        {
            Assert.Null(store.EnsureSecret(A, out var generated));
            Assert.False(generated);
            Assert.Equal(RelayIdentityStoreStatus.Unavailable, store.Status); // waits as for an unreadable file
            Assert.Equal(b, store.EnsureSecret(B, out _)); // valid on disk: still handed out
        }

        Assert.DoesNotContain(a, File.ReadAllText(FilePath));

        Directory.Delete(FilePath + ".tmp");
        Assert.Equal(a, store.EnsureSecret(A, out var restoredGenerated));
        Assert.False(restoredGenerated);
        Assert.Equal(a, (string?)ReadFile()["characters"]![A]!["secret"]);
        Assert.Equal(RelayIdentityStoreStatus.Available, store.Status);
    }

    [Fact]
    public void RestoredPreviousOnly_DoesNotHoldBackTheSecretOnDisk()
    {
        var store = NewStore();
        store.EnsureSecret(A, out _);
        var imported = Valid();
        store.Import(A, imported); // store knows {imported, previous}
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "{{imported}}", "previous": "bad" } } }""");
        Directory.CreateDirectory(FilePath + ".tmp"); // the rewrite (putting the previous back) fails

        Assert.Equal(imported, store.EnsureSecret(A, out _)); // the secret itself is on disk
    }

    [Fact]
    public void RestoredSecret_InAFileThatCannotBeCopiedAside_IsNotHandedOut_UntilItIsOnDisk()
    {
        var store = StoreWithADamagedOnDisk(out var a, out var b);
        var copyName = FilePath + ".corrupt-20261011T123456Z";
        Directory.CreateDirectory(copyName); // the copy fails, so the file is never rewritten

        Assert.Null(store.EnsureSecret(A, out _));
        Assert.Equal(RelayIdentityStoreStatus.Unavailable, store.Status);
        Assert.Equal(b, store.EnsureSecret(B, out _));
        Assert.DoesNotContain(a, File.ReadAllText(FilePath));

        Directory.Delete(copyName);
        Assert.Equal(a, store.EnsureSecret(A, out _));
        Assert.Equal(a, (string?)ReadFile()["characters"]![A]!["secret"]);
    }

    // ---------- decision 28: the newest kept-aside copy, for the rejection message ----------

    [Fact]
    public void NewestBackupFileName_IsNullWithoutCopies()
    {
        Assert.Null(NewStore().NewestBackupFileName());
        File.WriteAllText(Path.Combine(ConfigsDir, "Other.relay-identity.json.corrupt-20261011T123456Z"), "x");
        File.WriteAllText(FilePath + ".tmp", "x");
        Assert.Null(NewStore().NewestBackupFileName());
    }

    [Fact]
    public void NewestBackupFileName_NamesTheLatestCopy_ByStampThenCounter()
    {
        File.WriteAllText(FilePath, "first");
        NewStore().GetSecret(A);
        Assert.Equal("WindUpKey.relay-identity.json.corrupt-20261011T123456Z", NewStore().NewestBackupFileName());

        File.WriteAllText(FilePath, "second");
        NewStore().GetSecret(A);
        Assert.Equal("WindUpKey.relay-identity.json.corrupt-20261011T123456Z-1", NewStore().NewestBackupFileName());

        // -10 is newer than -9 (whatever order the folder lists them in), and a later second wins over any counter.
        // (-10 is written first: some file systems list the most recently created file first.)
        foreach (var n in new[] { 10, 2, 3, 4, 5, 6, 7, 8, 9 })
            File.WriteAllText(FilePath + ".corrupt-20261011T123456Z-" + n, "x");
        Assert.Equal("WindUpKey.relay-identity.json.corrupt-20261011T123456Z-10", NewStore().NewestBackupFileName());
        File.WriteAllText(FilePath + ".corrupt-20261011T123457Z", "x");
        Assert.Equal("WindUpKey.relay-identity.json.corrupt-20261011T123457Z", NewStore().NewestBackupFileName());
    }

    // ---------- decision 30: entries record their pairing key ----------

    [Fact]
    public void Writes_RecordTheCharactersPairingKey()
    {
        var store = NewStore();
        var a = store.EnsureSecret(A, out _)!;
        Assert.Equal(PairingKeyUtil.FromContentIdHex(A), (string?)ReadFile()["characters"]![A]!["pairingKey"]);

        store.Import(A, Valid());
        Assert.Equal(PairingKeyUtil.FromContentIdHex(A), (string?)ReadFile()["characters"]![A]!["pairingKey"]);
        Assert.True(store.Revert(A));
        Assert.Equal(PairingKeyUtil.FromContentIdHex(A), (string?)ReadFile()["characters"]![A]!["pairingKey"]);
        Assert.Equal(a, NewStore().GetSecret(A));
        Assert.DoesNotContain(_warnings, w => w.Contains(PairingKeyUtil.FromContentIdHex(A), StringComparison.Ordinal));
    }

    [Fact]
    public void EntriesWithoutAPairingKey_StayValid_AndGetItOnTheNextWriteForThatCharacter()
    {
        var a = Valid();
        var b = Valid();
        var content = $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "{{a}}" }, "{{B}}": { "secret": "{{b}}" } } }""";
        File.WriteAllText(FilePath, content);
        var store = NewStore();

        // Reading (and a connect that finds the secret) writes nothing.
        Assert.Equal(a, store.EnsureSecret(A, out var generated));
        Assert.False(generated);
        Assert.Equal(content, File.ReadAllText(FilePath));

        // The next write for A records A's key; B's entry is left as it was.
        store.Import(A, Valid());
        var chars = ReadFile()["characters"]!;
        Assert.Equal(PairingKeyUtil.FromContentIdHex(A), (string?)chars[A]!["pairingKey"]);
        Assert.Null(chars[B]!["pairingKey"]);
        Assert.Equal(b, (string?)chars[B]!["secret"]);
        Assert.Empty(Corrupted());
        Assert.Empty(_warnings);
    }

    [Fact]
    public void AStoredPairingKey_IsInformationalOnly()
    {
        // A wrong or malformed key is neither damage nor trusted: the secret is used, and the next write corrects it.
        var a = Valid();
        var b = Valid();
        File.WriteAllText(FilePath, $$"""{ "version": 1, "characters": { "{{A}}": { "secret": "{{a}}", "pairingKey": 12 }, "{{B}}": { "secret": "{{b}}", "pairingKey": "ZZZZZZZZ" } } }""");
        var store = NewStore();

        Assert.Equal(a, store.EnsureSecret(A, out _));
        Assert.Equal(b, store.EnsureSecret(B, out _));
        Assert.Empty(Corrupted());

        store.Import(A, Valid());
        store.Import(B, Valid());
        var chars = ReadFile()["characters"]!;
        Assert.Equal(PairingKeyUtil.FromContentIdHex(A), (string?)chars[A]!["pairingKey"]);
        Assert.Equal(PairingKeyUtil.FromContentIdHex(B), (string?)chars[B]!["pairingKey"]);
        Assert.Empty(_warnings);
    }
}
