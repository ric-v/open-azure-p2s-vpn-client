using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace P2SVpn;

public sealed class IpcRequest
{
    public string Cmd { get; set; } = "";
    public string? Name { get; set; }
    public string? ProfileXml { get; set; }
    public string? Token { get; set; }
    public int Lines { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Version { get; set; }
    public List<TunnelStatus>? Tunnels { get; set; }
    public List<string>? Log { get; set; }
}

public sealed class TunnelStatus
{
    public string Name { get; set; } = "";
    public string VpnName { get; set; } = "";
    public string Adapter { get; set; } = "";
    public string Gateway { get; set; } = "";
    /// <summary>STARTING, CONNECTING, WAIT, AUTH, GET_CONFIG, ASSIGN_IP, ADD_ROUTES, CONNECTED, RECONNECTING, EXITING, DISCONNECTED, FAILED</summary>
    public string State { get; set; } = "";
    public string? Detail { get; set; }
    public bool Running { get; set; }
    public string? LocalIp { get; set; }
    public DateTime? ConnectedSinceUtc { get; set; }
    public DateTime StartedUtc { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public List<string> Routes { get; set; } = [];
    public List<string> Dns { get; set; } = [];
    public int DnsRules { get; set; }
    public string? LastError { get; set; }
    public bool NeedToken { get; set; }
}

internal static class Ipc
{
    public const string PipeName = "P2SVpn.Service.v1";
    public const int MaxMessage = 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var buf = new List<byte>(4096);
        var one = new byte[4096];
        while (true)
        {
            int n = await s.ReadAsync(one, ct);
            if (n <= 0) return buf.Count > 0 ? Encoding.UTF8.GetString(buf.ToArray()) : null;
            for (int i = 0; i < n; i++)
            {
                if (one[i] == (byte)'\n') return Encoding.UTF8.GetString(buf.ToArray());
                buf.Add(one[i]);
            }
            if (buf.Count > MaxMessage) throw new InvalidDataException("message too large");
        }
    }

    public static async Task WriteLineAsync(Stream s, string line, CancellationToken ct)
    {
        var b = Encoding.UTF8.GetBytes(line + "\n");
        await s.WriteAsync(b, ct);
        await s.FlushAsync(ct);
    }

    /// <summary>One request per connection.</summary>
    public static async Task<IpcResponse> CallAsync(IpcRequest req, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(Math.Min(timeoutMs, 3000), cts.Token);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException)
        {
            return new IpcResponse { Ok = false, Error = "SERVICE_UNAVAILABLE" };
        }
        await WriteLineAsync(pipe, JsonSerializer.Serialize(req, Json), cts.Token);
        var line = await ReadLineAsync(pipe, cts.Token);
        if (line is null) return new IpcResponse { Ok = false, Error = "The service closed the connection." };
        return JsonSerializer.Deserialize<IpcResponse>(line, Json) ?? new IpcResponse { Ok = false, Error = "Bad reply from the service." };
    }
}
