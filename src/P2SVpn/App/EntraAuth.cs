using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace P2SVpn.App;

public sealed record TokenResult(string AccessToken, string? Account, DateTimeOffset ExpiresOn);

/// <summary>
/// Microsoft Entra sign-in for the Azure VPN application: authorization code +
/// PKCE with a loopback redirect, in the user's default browser. The refresh
/// token is cached per tenant/app, encrypted with DPAPI for the current user.
/// </summary>
internal static class EntraAuth
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private sealed class Cache
    {
        public string RefreshToken { get; set; } = "";
        public string? Account { get; set; }
    }

    private static string CacheFile(AzureProfile p) => Path.Combine(AppPaths.TokenCacheDir, $"{p.Tenant}_{p.ClientId}.bin");

    public static string? CachedAccount(AzureProfile p) => ReadCache(p)?.Account;

    public static void SignOut(AzureProfile p)
    {
        try { File.Delete(CacheFile(p)); } catch { }
    }

    private static Cache? ReadCache(AzureProfile p)
    {
        try
        {
            var f = CacheFile(p);
            if (!File.Exists(f)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(f), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Cache>(bytes);
        }
        catch { return null; }
    }

    private static void WriteCache(AzureProfile p, Cache c)
    {
        Directory.CreateDirectory(AppPaths.TokenCacheDir);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(c), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(CacheFile(p), bytes);
    }

    /// <summary>Silent refresh; returns null when interactive sign-in is needed.</summary>
    public static async Task<TokenResult?> TrySilentAsync(AzureProfile p, CancellationToken ct)
    {
        var c = ReadCache(p);
        if (c is null || c.RefreshToken.Length == 0) return null;
        try
        {
            return await RedeemAsync(p, new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = c.RefreshToken,
            }, ct);
        }
        catch (AuthException) { return null; }
    }

    public static async Task<TokenResult> GetTokenAsync(AzureProfile p, bool allowInteractive, CancellationToken ct)
    {
        var t = await TrySilentAsync(p, ct);
        if (t is not null) return t;
        if (!allowInteractive) throw new AuthException("Sign-in required.");
        return await InteractiveAsync(p, ct);
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static async Task<TokenResult> InteractiveAsync(AzureProfile p, CancellationToken ct)
    {
        var verifier = B64Url(RandomNumberGenerator.GetBytes(48));
        var state = B64Url(RandomNumberGenerator.GetBytes(24));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var l4 = new TcpListener(IPAddress.Loopback, 0);
        l4.Start();
        int port = ((IPEndPoint)l4.LocalEndpoint).Port;
        TcpListener? l6 = null;
        try { l6 = new TcpListener(IPAddress.IPv6Loopback, port); l6.Start(); } catch { l6 = null; }
        var redirect = $"http://localhost:{port}";

        var q = new Dictionary<string, string>
        {
            ["client_id"] = p.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirect,
            ["response_mode"] = "query",
            ["scope"] = $"{p.Scope} offline_access openid profile",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        };
        var url = $"https://login.microsoftonline.com/{p.Tenant}/oauth2/v2.0/authorize?" +
                  string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        string code;
        try
        {
            code = await WaitForCodeAsync(l4, l6, state, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AuthException("Timed out waiting for the browser sign-in.");
        }
        finally
        {
            l4.Stop();
            l6?.Stop();
        }

        return await RedeemAsync(p, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        }, ct);
    }

    private static async Task<string> WaitForCodeAsync(TcpListener l4, TcpListener? l6, string state, CancellationToken ct)
    {
        while (true)
        {
            var accepts = new List<Task<TcpClient>> { l4.AcceptTcpClientAsync(ct).AsTask() };
            if (l6 is not null) accepts.Add(l6.AcceptTcpClientAsync(ct).AsTask());
            var done = await Task.WhenAny(accepts);
            using var client = await done;

            var stream = client.GetStream();
            var req = new StringBuilder();
            var buf = new byte[4096];
            using (var rcts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                rcts.CancelAfter(10000);
                while (!req.ToString().Contains("\r\n\r\n") && req.Length < 65536)
                {
                    int n = await stream.ReadAsync(buf, rcts.Token);
                    if (n <= 0) break;
                    req.Append(Encoding.ASCII.GetString(buf, 0, n));
                }
            }
            var first = req.ToString().Split("\r\n")[0];
            var parts = first.Split(' ');
            if (parts.Length < 2 || parts[0] != "GET") continue;
            var uri = new Uri("http://localhost" + parts[1]);
            var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);

            if (qs["error"] is { } err)
            {
                await ReplyAsync(stream, "Sign-in failed", "You can close this tab and return to P2S VPN.");
                throw new AuthException($"{err}: {qs["error_description"]?.Split('\n')[0]}");
            }
            if (qs["code"] is not { } code)
            {
                await ReplyAsync(stream, "P2S VPN", "Waiting for sign-in...");
                continue;
            }
            if (qs["state"] != state)
            {
                await ReplyAsync(stream, "Sign-in failed", "The response did not match this sign-in.");
                throw new AuthException("Sign-in response did not match (state mismatch).");
            }
            await ReplyAsync(stream, "Signed in", "P2S VPN has your sign-in. You can close this tab.");
            return code;
        }
    }

    private static async Task ReplyAsync(NetworkStream s, string title, string text)
    {
        var html = $"<!doctype html><meta charset=utf-8><title>{title}</title><body style=\"font-family:Segoe UI,sans-serif;margin:3em\"><h2>{title}</h2><p>{text}</p></body>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    private static async Task<TokenResult> RedeemAsync(AzureProfile p, Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = p.ClientId;
        form["scope"] = $"{p.Scope} offline_access openid profile";
        using var resp = await Http.PostAsync($"https://login.microsoftonline.com/{p.Tenant}/oauth2/v2.0/token",
            new FormUrlEncodedContent(form), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!resp.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var at))
        {
            var e = root.TryGetProperty("error", out var ee) ? ee.GetString() : resp.StatusCode.ToString();
            var d = root.TryGetProperty("error_description", out var dd) ? dd.GetString()?.Split('\n')[0] : null;
            throw new AuthException($"{e}: {d}");
        }
        var access = at.GetString()!;
        if (access.Length > 8000) throw new AuthException("The access token is larger than this client supports.");
        string? account = null;
        DateTimeOffset exp = DateTimeOffset.UtcNow.AddMinutes(50);
        try
        {
            var payload = access.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
            var c = claims.RootElement;
            account = c.TryGetProperty("upn", out var upn) ? upn.GetString()
                    : c.TryGetProperty("unique_name", out var un) ? un.GetString()
                    : c.TryGetProperty("preferred_username", out var pu) ? pu.GetString() : null;
            if (c.TryGetProperty("exp", out var ex)) exp = DateTimeOffset.FromUnixTimeSeconds(ex.GetInt64());
        }
        catch { }

        if (root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } refresh)
            WriteCache(p, new Cache { RefreshToken = refresh, Account = account });

        return new TokenResult(access, account, exp);
    }
}

public sealed class AuthException(string message) : Exception(message);
