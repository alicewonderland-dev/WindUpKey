using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>
/// Not a test runner: the test host never calls this. <see cref="RelayIdentityStoreTests"/> starts this assembly
/// as a separate process (<c>dotnet WindUpKey.Tests.dll store-worker …</c>) to act as a second game client.
/// </summary>
public static class Program
{
    public const string StoreWorkerCommand = "store-worker";

    /// <summary>
    /// <c>store-worker &lt;pluginConfigDir&gt; &lt;idPrefix&gt; &lt;count&gt; &lt;barrierDir&gt;</c>: generates a secret for
    /// <c>count</c> characters, each through a fresh store instance. Writes <c>barrierDir/ready-&lt;idPrefix&gt;</c>,
    /// then waits for <c>barrierDir/go</c>, so every worker starts writing at the same time.
    /// </summary>
    public static int Main(string[] args)
    {
        if (args.Length != 5 || args[0] != StoreWorkerCommand)
        {
            Console.Error.WriteLine($"usage: {StoreWorkerCommand} <pluginConfigDir> <idPrefix> <count> <barrierDir>");
            return 64;
        }

        var configDir = args[1];
        var prefix = args[2];
        var count = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        var barrier = args[4];
        File.WriteAllText(Path.Combine(barrier, "ready-" + prefix), string.Empty);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(Path.Combine(barrier, "go")))
        {
            if (DateTime.UtcNow > deadline)
            {
                Console.WriteLine("barrier timeout");
                return 2;
            }

            Thread.Sleep(1);
        }

        var failures = 0;
        for (var i = 0; i < count; i++)
        {
            // A fresh instance every time, so writes start from a stale or empty view. A long lock wait: this
            // checks mutual exclusion between processes, not the framework-thread timeout.
            var store = new RelayIdentityStore(configDir, "WindUpKey", w => Console.Error.WriteLine(w), lockWait: TimeSpan.FromSeconds(10));
            if (store.EnsureSecret($"{prefix}{i:X4}", out _) is null)
                failures++;
        }

        Console.WriteLine($"failures={failures}");
        return failures == 0 ? 0 : 1;
    }
}
