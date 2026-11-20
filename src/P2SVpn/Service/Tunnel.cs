using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace P2SVpn.Service;

/// <summary>
/// One OpenVPN process for one profile, driven over its management interface.
/// The Entra access token is the password (username "AzureAD"), sent in the
/// multi-line base64 form; it is held in memory only.
/// </summary>
internal sealed class Tunnel
{
    private readonly object _gate = new();
    private readonly AzureProfile _profile;
    private Process? _proc;
    private TcpClient? _mgmt;
    private NetworkStream? _stream;
    private string? _token;
    private bool _tokenUsed;
    private bool _stopRequested;
    private DateTime _needTokenSinceUtc;

    public string Name { get; }
    public string Adapter { get; }
    public string LogPath { get; }
    private string RunPath => Path.Combine(AppPaths.RunDir, Name);

    public string State { get; private set; } = "STARTING";
    public string? Detail { get; private set; }
    public string? LocalIp { get; private set; }
    public DateTime? ConnectedSinceUtc { get; private set; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public long BytesIn { get; private set; }
    public long BytesOut { get; private set; }
    public List<string> Routes { get; private set; } = [];
    public List<string> Dns { get; private set; } = [];
    public int DnsRules { get; private set; }
    public string? LastError { get; private set; }
    public bool NeedToken { get; private set; }
    public bool Running => _proc is { HasExited: false };

    public Tunnel(string name, AzureProfile profile, string token)
    {
        Name = name;
        _profile = profile;
        _token = token;
        Adapter = AppPaths.AdapterName(name);
        LogPath = Path.Combine(AppPaths.LogDir, name + ".log");
    }

    public TunnelStatus Snapshot()
    {
        lock (_gate)
        {
            return new TunnelStatus
            {
                Name = Name, VpnName = _profile.VpnName, Adapter = Adapter, Gateway = _profile.Gateways[0],
                State = State, Detail = Detail, Running = Running, LocalIp = LocalIp,
                ConnectedSinceUtc = ConnectedSinceUtc, StartedUtc = StartedUtc, BytesIn = BytesIn, BytesOut = BytesOut,
                Routes = [.. Routes], Dns = [.. Dns], DnsRules = DnsRules, LastError = LastError, NeedToken = NeedToken,
            };
        }
    }

    public void Start()
    {
        Directory.CreateDirectory(RunPath);
        Directory.CreateDirectory(AppPaths.LogDir);
        Adapters.Ensure(Adapter);

        var config = Path.Combine(RunPath, "openvpn.ovpn");
        File.WriteAllText(config, _profile.RenderOpenVpnConfig(), new UTF8Encoding(false));
        var mgmtPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var pwFile = Path.Combine(RunPath, "mgmt.pw");
        File.WriteAllText(pwFile, mgmtPassword + "\n");

        int port;
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
        }

        var psi = new ProcessStartInfo(AppPaths.OpenVpnExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RunPath,
        };
        foreach (var a in new[]
                 {
                     "--config", config, "--dev-node", Adapter, "--disable-dco",
                     "--management", "127.0.0.1", port.ToString(), pwFile,
                     "--log", LogPath,
                 })
            psi.ArgumentList.Add(a);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start OpenVPN.");
        SetState("CONNECTING", "starting OpenVPN");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                _mgmt = new TcpClient();
                _mgmt.Connect(IPAddress.Loopback, port);
                break;
            }
            catch (SocketException)
            {
                _mgmt?.Dispose();
                if (_proc.HasExited) throw new InvalidOperationException($"OpenVPN exited at start (code {_proc.ExitCode}). See the log.");
                if (DateTime.UtcNow > deadline) { Kill(); throw new InvalidOperationException("OpenVPN did not open its management port."); }
                Thread.Sleep(150);
            }
        }
        _mgmt.NoDelay = true;
        _stream = _mgmt.GetStream();
        new Thread(WriterLoop) { IsBackground = true, Name = "mgmt-out-" + Name }.Start();
        new Thread(() => ReadLoop(mgmtPassword)) { IsBackground = true, Name = "mgmt-" + Name }.Start();
        new Thread(WatchLoop) { IsBackground = true, Name = "watch-" + Name }.Start();
    }

    // ---- management output ---------------------------------------------------
    // OpenVPN 2.7's management interface can process a command twice and drop the
    // next one when several commands that print a reply arrive together (seen as
    // "version 5" twice with "state on" lost, or "username" six times). So
    // commands go out one at a time from a writer thread, each waiting for its
    // SUCCESS:/ERROR: reply. Base64 password lines print nothing and are sent as is.
    private readonly System.Collections.Concurrent.BlockingCollection<(string Line, bool WaitReply)> _out = new();
    private readonly AutoResetEvent _reply = new(false);

    private void Send(string line, bool waitReply = true)
    {
        try { _out.Add((line, waitReply)); }
        catch (InvalidOperationException) { /* tunnel already ended */ }
    }

    private void WriterLoop()
    {
        foreach (var (line, waitReply) in _out.GetConsumingEnumerable())
        {
            NetworkStream? s;
            lock (_gate) s = _stream;
            if (s is null) continue;
            _reply.Reset();
            try
            {
                var b = Encoding.ASCII.GetBytes(line + "\n");
                s.Write(b, 0, b.Length);
                s.Flush();
            }
            catch { continue; }
            if (waitReply) _reply.WaitOne(5000);
        }
    }

    private void SendCredentials(string token)
    {
        Send("username \"Auth\" \"AzureAD\"");
        Send("password \"Auth\"", waitReply: false);      // enters multi-line mode, no reply
        // 256-char lines: OpenVPN reads management input 256 bytes at a time into
        // a 1024-byte buffer and silently clears it on overflow (manage.c,
        // command_line_add); longer lines can corrupt the token.
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
        for (int i = 0; i < enc.Length; i += 256) Send(enc.Substring(i, Math.Min(256, enc.Length - i)), waitReply: false);
        Send("END");                                       // replies once the password is taken
    }

    /// <summary>A fresh token from the app, for a reconnect.</summary>
    public void ProvideToken(string token)
    {
        bool send;
        lock (_gate)
        {
            _token = token;
            _tokenUsed = false;
            send = NeedToken;
            NeedToken = false;
        }
        if (send) { SendCredentials(token); lock (_gate) _tokenUsed = true; }
    }

    private void ReadLoop(string mgmtPassword)
    {
        var buf = new byte[8192];
        var pending = new StringBuilder();
        try
        {
            while (true)
            {
                int n = _stream!.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                pending.Append(Encoding.ASCII.GetString(buf, 0, n));
                string s = pending.ToString();
                int idx;
                while ((idx = s.IndexOf('\n')) >= 0)
                {
                    var line = s[..idx].TrimEnd('\r');
                    s = s[(idx + 1)..];
                    Handle(line, mgmtPassword);
                }
                pending.Clear().Append(s);
                // "ENTER PASSWORD:" has no newline.
                if (s.StartsWith("ENTER PASSWORD:")) { Send(mgmtPassword); pending.Clear(); }
            }
        }
        catch { /* socket closed: OpenVPN exited */ }
    }

    private void Handle(string line, string mgmtPassword)
    {
        if (line.StartsWith("SUCCESS:") || line.StartsWith("ERROR:"))
        {
            if (line.StartsWith("ERROR:")) lock (_gate) Detail = line;
            _reply.Set();
            return;
        }
        if (line.StartsWith("ENTER PASSWORD:")) { Send(mgmtPassword); return; }
        if (line.StartsWith(">HOLD:"))
        {
            // Version 5 enables the multi-line base64 password (OpenVPN >= 2.7.2).
            Send("version 5");
            Send("state on");
            Send("bytecount 2");
            Send("hold release");
            return;
        }
        if (line.StartsWith(">PASSWORD:Need 'Auth'"))
        {
            string? token;
            bool fresh;
            lock (_gate) { token = _token; fresh = !_tokenUsed; }
            if (token is not null && fresh)
            {
                SendCredentials(token);
                lock (_gate) _tokenUsed = true;
            }
            else
            {
                // Reconnect: tokens are short-lived, ask the app for a new one.
                lock (_gate) { NeedToken = true; _needTokenSinceUtc = DateTime.UtcNow; Detail = "waiting for sign-in"; }
            }
            return;
        }
        if (line.StartsWith(">PASSWORD:Verification Failed"))
        {
            lock (_gate) LastError = "The VPN gateway rejected the sign-in (AUTH_FAILED).";
            return;
        }
        if (line.StartsWith(">BYTECOUNT:"))
        {
            var p = line[11..].Split(',');
            if (p.Length == 2 && long.TryParse(p[0], out var bin) && long.TryParse(p[1], out var bout))
                lock (_gate) { BytesIn = bin; BytesOut = bout; }
            return;
        }
        if (line.StartsWith(">STATE:"))
        {
            var p = line[7..].Split(',');
            if (p.Length < 2) return;
            var st = p[1];
            var desc = p.Length > 2 ? p[2] : null;
            if (st == "CONNECTED")
            {
                var ip = p.Length > 3 ? p[3] : null;
                ParsePushReply();
                lock (_gate) { State = "CONNECTED"; Detail = null; LocalIp = ip; ConnectedSinceUtc = DateTime.UtcNow; LastError = null; }
                List<string> dns;
                lock (_gate) dns = [.. Dns];
                var added = dns.Count > 0 ? Nrpt.Apply(Name, dns) : 0;
                lock (_gate) DnsRules = added;
            }
            else
            {
                if (st is "RECONNECTING" or "EXITING")
                {
                    Nrpt.Remove(Name);
                    lock (_gate) { ConnectedSinceUtc = null; LocalIp = null; DnsRules = 0; }
                }
                SetState(st, string.IsNullOrEmpty(desc) ? null : desc);
            }
            return;
        }
        if (line.StartsWith(">FATAL:")) { lock (_gate) LastError = line[7..]; return; }
    }

    private void SetState(string st, string? detail)
    {
        lock (_gate) { State = st; Detail = detail; }
    }

    private void ParsePushReply()
    {
        try
        {
            string? push = null;
            using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
            {
                string? l;
                while ((l = sr.ReadLine()) is not null)
                    if (l.Contains("PUSH_REPLY")) push = l;
            }
            if (push is null) return;
            var routes = Regex.Matches(push, @"route (\d+\.\d+\.\d+\.\d+) (\d+\.\d+\.\d+\.\d+)")
                .Select(m => $"{m.Groups[1].Value}/{MaskBits(m.Groups[2].Value)}").Distinct().ToList();
            var dns = Regex.Matches(push, @"dhcp-option DNS (\d+\.\d+\.\d+\.\d+)").Select(m => m.Groups[1].Value).Distinct().ToList();
            lock (_gate) { Routes = routes; Dns = dns; }
        }
        catch { }
    }

    private static int MaskBits(string mask) =>
        IPAddress.TryParse(mask, out var m) ? m.GetAddressBytes().Sum(b => System.Numerics.BitOperations.PopCount(b)) : 32;

    private void WatchLoop()
    {
        while (_proc is { HasExited: false })
        {
            Thread.Sleep(1000);
            bool expire;
            lock (_gate) expire = NeedToken && DateTime.UtcNow - _needTokenSinceUtc > TimeSpan.FromMinutes(3);
            if (expire)
            {
                lock (_gate) { LastError = "Disconnected: the VPN needed a new sign-in and the app did not provide one."; NeedToken = false; }
                Send("signal SIGTERM");
            }
        }
        Nrpt.Remove(Name);
        lock (_gate)
        {
            State = _stopRequested || LastError is null ? "DISCONNECTED" : "FAILED";
            Detail = null; LocalIp = null; ConnectedSinceUtc = null; DnsRules = 0; NeedToken = false; _token = null;
            try { _mgmt?.Dispose(); } catch { }
            _stream = null;
        }
        _out.CompleteAdding();
        _reply.Set();
        TryDelete(Path.Combine(RunPath, "mgmt.pw"));
        TryDelete(Path.Combine(RunPath, "openvpn.ovpn"));
    }

    public void Stop(int waitMs = 10000)
    {
        lock (_gate) _stopRequested = true;
        if (_proc is null || _proc.HasExited) return;
        SetState("EXITING", "disconnecting");
        Send("signal SIGTERM");
        if (!_proc.WaitForExit(waitMs)) Kill();
    }

    private void Kill()
    {
        try { _proc?.Kill(true); } catch { }
    }

    private static void TryDelete(string f)
    {
        try { File.Delete(f); } catch { }
    }

    public List<string> TailLog(int lines)
    {
        lines = Math.Clamp(lines, 10, 2000);
        var q = new Queue<string>(lines);
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            string? l;
            while ((l = sr.ReadLine()) is not null)
            {
                // Never pass on anything that looks like a credential.
                if (Regex.IsMatch(l, "[A-Za-z0-9+/_=-]{200,}")) l = "[line hidden: long encoded value]";
                if (q.Count == lines) q.Dequeue();
                q.Enqueue(l);
            }
        }
        catch (FileNotFoundException) { }
        return [.. q];
    }
}
