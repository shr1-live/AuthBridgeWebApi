using ModelContextProtocol.Client;

namespace AuthBridge.SmokeClient;

// Usage:
//   dotnet run --project tools/AuthBridge.SmokeClient -- stdio [--server <path to AuthBridge.Mcp.dll>]
//   dotnet run --project tools/AuthBridge.SmokeClient -- http --url http://localhost:5243/mcp
// For http, the bearer access token is read from AUTHBRIDGE_ACCESS_TOKEN, never from arguments.
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "stdio";
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        IClientTransport transport;
        if (mode == "http")
        {
            var url = Option(args, "--url") ?? "http://localhost:5243/mcp";
            var token = Environment.GetEnvironmentVariable("AUTHBRIDGE_ACCESS_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("Set AUTHBRIDGE_ACCESS_TOKEN to a valid access token for a tenant A user.");
                return 2;
            }
            transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(url),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            });
        }
        else
        {
            var server = Option(args, "--server") ?? DefaultServerPath();
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "authbridge-local",
                Command = "dotnet",
                Arguments = [server],
                EnvironmentVariables = new Dictionary<string, string?> { ["DOTNET_ENVIRONMENT"] = "Development" },
            });
        }

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: cts.Token);
        var results = await SmokeChecks.RunAsync(client, cts.Token);
        foreach (var r in results)
        {
            Console.WriteLine($"{(r.Passed ? "PASS" : "FAIL")}  {r.Name}");
            if (!r.Passed)
                Console.WriteLine("      " + r.Detail);
        }
        var failed = results.Count(r => !r.Passed);
        Console.WriteLine(failed == 0 ? $"All {results.Count} checks passed over {mode}." : $"{failed} of {results.Count} checks failed over {mode}.");
        return failed == 0 ? 0 : 1;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string DefaultServerPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AuthBridge.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not find the repository root; pass --server.");
        return Path.Combine(dir.FullName, "src", "AuthBridge.Mcp", "bin", "Debug", "net10.0", "AuthBridge.Mcp.dll");
    }
}
