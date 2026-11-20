using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;

namespace P2SVpn.Service;

internal sealed class TunnelManager
{
    private readonly Dictionary<string, Tunnel> _tunnels = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IpcResponse Handle(IpcRequest req)
    {
        switch (req.Cmd)
        {
            case "ping":
                return Ok();
            case "status":
                lock (_gate) return Ok(t: _tunnels.Values.Select(t => t.Snapshot()).OrderBy(s => s.Name).ToList());
            case "connect":
                return Connect(req);
            case "disconnect":
            {
                var t = Get(req.Name);
                if (t is null) return Fail("That VPN is not running.");
                Task.Run(() => t.Stop());
                return Ok();
            }
            case "token":
            {
                var t = Get(req.Name);
                if (t is null || string.IsNullOrEmpty(req.Token)) return Fail("Nothing is waiting for a token.");
                t.ProvideToken(req.Token);
                return Ok();
            }
            case "log":
            {
                var t = Get(req.Name);
                if (t is not null) return Ok(log: t.TailLog(req.Lines));
                if (!AppPaths.IsValidProfileName(req.Name)) return Fail("Bad profile name.");
                // Not running this session: read the last log file anyway.
                var tmp = new Tunnel(req.Name!, null!, "");
                return Ok(log: tmp.TailLog(req.Lines));
            }
            case "forget":
            {
                lock (_gate)
                {
                    if (req.Name is not null && _tunnels.TryGetValue(req.Name, out var t) && !t.Running) _tunnels.Remove(req.Name);
                }
                return Ok();
            }
            default:
                return Fail("Unknown command.");
        }
    }

    private Tunnel? Get(string? name)
    {
        if (name is null) return null;
        lock (_gate) return _tunnels.GetValueOrDefault(name);
    }

    private IpcResponse Connect(IpcRequest req)
    {
        if (!AppPaths.IsValidProfileName(req.Name)) return Fail("Bad profile name.");
        if (string.IsNullOrEmpty(req.ProfileXml) || string.IsNullOrEmpty(req.Token)) return Fail("Profile and sign-in are required.");
        if (req.Token.Length > 8000) return Fail("The sign-in token is too large.");

        AzureProfile profile;
        try { profile = AzureProfile.Parse(req.ProfileXml); }
        catch (ProfileException e) { return Fail(e.Message); }

        Tunnel t;
        lock (_gate)
        {
            if (_tunnels.TryGetValue(req.Name!, out var existing) && existing.Running) return Fail("That VPN is already running.");
            t = new Tunnel(req.Name!, profile, req.Token);
            _tunnels[req.Name!] = t;
        }
        try
        {
            t.Start();
            return Ok();
        }
        catch (Exception e)
        {
            lock (_gate) _tunnels.Remove(req.Name!);
            return Fail(e.Message);
        }
    }

    public void StopAll()
    {
        List<Tunnel> all;
        lock (_gate) all = [.. _tunnels.Values];
        Parallel.ForEach(all, t => t.Stop(8000));
        Nrpt.Remove(null);
    }

    private static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
    private static IpcResponse Ok(List<TunnelStatus>? t = null, List<string>? log = null) =>
        new() { Ok = true, Version = Version, Tunnels = t, Log = log };
    private static IpcResponse Fail(string e) => new() { Ok = false, Error = e, Version = Version };
}

internal sealed class VpnService : ServiceBase
{
    private readonly TunnelManager _manager = new();
    private CancellationTokenSource? _cts;
    private Task? _server;

    public VpnService()
    {
        ServiceName = AppPaths.ServiceName;
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        Directory.CreateDirectory(AppPaths.RunDir);
        Directory.CreateDirectory(AppPaths.LogDir);
        Nrpt.Remove(null); // leftovers from a crash
        _cts = new CancellationTokenSource();
        _server = Task.Run(() => ServeAsync(_cts.Token));
    }

    protected override void OnStop() => Shutdown();
    protected override void OnShutdown() => Shutdown();

    private void Shutdown()
    {
        _cts?.Cancel();
        _manager.StopAll();
    }

    private static PipeSecurity PipeAcl()
    {
        var sec = new PipeSecurity();
        // Local signed-in users may talk to the service; nobody over the network.
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return sec;
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        var acl = PipeAcl();
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(Ipc.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, acl);
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(500, CancellationToken.None); continue; }

            _ = Task.Run(async () =>
            {
                using (pipe)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        var line = await Ipc.ReadLineAsync(pipe, timeout.Token);
                        if (line is null) return;
                        IpcResponse resp;
                        try
                        {
                            var req = JsonSerializer.Deserialize<IpcRequest>(line, Ipc.Json) ?? new IpcRequest();
                            resp = await Task.Run(() => _manager.Handle(req));
                        }
                        catch (Exception e) { resp = new IpcResponse { Ok = false, Error = e.Message }; }
                        await Ipc.WriteLineAsync(pipe, JsonSerializer.Serialize(resp, Ipc.Json), timeout.Token);
                    }
                    catch { }
                }
            });
        }
    }
}
