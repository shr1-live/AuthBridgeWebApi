using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace AuthBridge.Api.Assistant;

/// <summary>
/// An HTTP client for this server's own /mcp endpoint. The in-app assistant is an ordinary MCP
/// client: it goes through the same authenticated endpoint as any external AI.
/// </summary>
public interface IMcpLoopback
{
    HttpClient CreateClient();
}

public sealed class ServerAddressLoopback(IServer server, IHttpClientFactory factory) : IMcpLoopback
{
    public HttpClient CreateClient()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var address = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The server is not listening on any address.");
        var client = factory.CreateClient(nameof(ServerAddressLoopback));
        client.BaseAddress = new Uri(ToLoopback(address));
        return client;
    }

    /// <summary>"http://0.0.0.0:10000", "http://[::]:8080" or "http://+:80" become a 127.0.0.1 URL.</summary>
    public static string ToLoopback(string address)
    {
        var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            throw new ArgumentException($"Not a server address: {address}", nameof(address));
        var scheme = address[..schemeEnd];
        var rest = address[(schemeEnd + 3)..].TrimEnd('/');
        var portStart = rest.LastIndexOf(':');
        var host = portStart > 0 && !rest.EndsWith(']') ? rest[..portStart] : rest;
        var port = portStart > 0 && !rest.EndsWith(']') ? rest[(portStart + 1)..] : "";
        if (host is "0.0.0.0" or "[::]" or "+" or "*")
            host = "127.0.0.1";
        return port.Length > 0 ? $"{scheme}://{host}:{port}/" : $"{scheme}://{host}/";
    }
}
