using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace P2SVpn;

public sealed class ProfileException(string message) : Exception(message);

/// <summary>
/// An Azure VPN Client profile (azurevpnconfig.xml) with Entra ID authentication.
/// Parsing is strict on purpose: the service renders an OpenVPN config from these
/// fields and runs it as SYSTEM, so nothing free-form from the file reaches it.
/// </summary>
public sealed class AzureProfile
{
    public string VpnName { get; private init; } = "";
    public IReadOnlyList<string> Gateways { get; private init; } = [];
    public string CertificateName { get; private init; } = "";
    public string Tenant { get; private init; } = "";
    public string ClientId { get; private init; } = "";
    public string Scope { get; private init; } = "";
    public string ServerSecret { get; private init; } = "";

    private const string Guid = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    // Roots Azure P2S gateways chain to, pinned by SHA-256 (same set as OpenP2S).
    private static readonly string[] AzureRootSha256 =
    [
        "cb3ccbb76031e5e0138f8dd39a23f9de47ffc35e43c1144cea27d46a5ab1cb5f", // DigiCert Global Root G2
        "4348a0e9444c78cb265e058d5e8944b4d84f9662bd26db257f8934a443c70161", // DigiCert Global Root CA
    ];

    public static AzureProfile Parse(string xmlText)
    {
        if (xmlText.Length > 512 * 1024) throw new ProfileException("The profile file is too large to be an Azure VPN profile.");

        var doc = new XmlDocument { XmlResolver = null };
        try
        {
            using var sr = new StringReader(xmlText);
            using var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc.Load(xr);
        }
        catch (XmlException e)
        {
            throw new ProfileException("This is not a valid XML file: " + e.Message);
        }

        static string LN(string n) => $"*[local-name()='{n}']";
        string? T(string xp)
        {
            var t = doc.SelectSingleNode(xp)?.InnerText.Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }

        var authType = T($"//{LN("clientauth")}/{LN("type")}");
        if (authType is not null && !authType.Equals("aad", StringComparison.OrdinalIgnoreCase))
            throw new ProfileException($"This profile uses '{authType}' authentication. Only Microsoft Entra ID (Azure AD) profiles are supported.");

        var aad = $"//{LN("clientauth")}//{LN("aad")}";
        var tenantUrl = T($"{aad}/{LN("tenant")}") ?? throw new ProfileException("The profile has no Entra tenant (<tenant>). Is it an Entra ID profile?");
        var audience = T($"{aad}/{LN("audience")}") ?? throw new ProfileException("The profile has no <audience>.");
        var appId = T($"{aad}/{LN("applicationid")}");
        var secret = T($"//{LN("servervalidation")}/{LN("serversecret")}") ?? throw new ProfileException("The profile has no <serversecret>.");
        var name = T($"/*/{LN("name")}") ?? "Azure VPN";
        var transport = T($"//{LN("transportprotocol")}");

        var gateways = new List<string>();
        foreach (XmlNode n in doc.SelectNodes($"//{LN("serverlist")}//{LN("fqdn")}")!)
        {
            var g = n.InnerText.Trim().ToLowerInvariant();
            if (g.Length == 0) continue;
            if (!Regex.IsMatch(g, "^[a-z0-9.-]{1,253}$")) throw new ProfileException($"Gateway name '{g}' is not a valid host name.");
            if (!gateways.Contains(g)) gateways.Add(g);
        }
        if (gateways.Count == 0) throw new ProfileException("The profile has no gateway (<fqdn>).");
        if (transport is not null && !transport.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            throw new ProfileException($"The profile asks for '{transport}' transport; Entra P2S uses TCP.");

        if (!Regex.IsMatch(secret, "^[0-9a-fA-F]{512}$")) throw new ProfileException("<serversecret> is not 512 hex characters.");

        if (!Uri.TryCreate(tenantUrl, UriKind.Absolute, out var tu) || tu.Host != "login.microsoftonline.com")
            throw new ProfileException($"Unsupported sign-in authority '{tenantUrl}' (only login.microsoftonline.com).");
        var tenant = tu.AbsolutePath.Trim('/');
        if (!Regex.IsMatch(tenant, $"^{Guid}$")) throw new ProfileException($"Cannot read the tenant id from '{tenantUrl}'.");
        if (!Regex.IsMatch(audience, $"^{Guid}$")) throw new ProfileException($"Audience '{audience}' is not an application id.");
        if (appId is not null && !Regex.IsMatch(appId, $"^{Guid}$")) throw new ProfileException($"Application id '{appId}' is not valid.");

        var certNames = gateways.Select(ExpectedCertificateName).Distinct().ToList();
        if (certNames.Count != 1 || certNames[0] is null)
            throw new ProfileException("Cannot work out the gateway certificate name; only standard *.vpn.azure.com gateways are supported.");

        return new AzureProfile
        {
            VpnName = Regex.Replace(name, @"[^\w .()-]", "").Trim() is { Length: > 0 } clean ? clean : "Azure VPN",
            Gateways = gateways,
            CertificateName = certNames[0]!,
            Tenant = tenant.ToLowerInvariant(),
            ClientId = (appId ?? audience).ToLowerInvariant(),
            Scope = audience.ToLowerInvariant() + "/.default",
            ServerSecret = secret.ToLowerInvariant(),
        };
    }

    public static string? ExpectedCertificateName(string host)
    {
        var h = host.ToLowerInvariant();
        var m = Regex.Match(h, $"^azuregateway-({Guid})-[0-9a-z]+\\.((?:ln\\.)?vpn\\.azure\\.com)$");
        if (m.Success) return $"{m.Groups[1].Value.ToLowerInvariant()}.{m.Groups[2].Value}";
        return Regex.IsMatch(h, "^[a-z0-9-]+\\.vpn\\.azure\\.com$") ? "*.vpn.azure.com" : null;
    }

    /// <summary>A short profile id from the VPN name, e.g. MY_DEV_VNET -> my-dev-vnet.</summary>
    public string SuggestedId()
    {
        var s = Regex.Replace(VpnName.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (s.Length == 0) s = "vpn";
        return s.Length > 24 ? s[..24].TrimEnd('-') : s;
    }

    public static string AzureRootsPem()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = new Dictionary<string, string>();
        foreach (var c in store.Certificates)
        {
            var h = Convert.ToHexStringLower(c.GetCertHash(HashAlgorithmName.SHA256));
            if (AzureRootSha256.Contains(h) && !found.ContainsKey(h)) found[h] = c.ExportCertificatePem();
        }
        foreach (var h in AzureRootSha256)
            if (!found.ContainsKey(h)) throw new ProfileException($"Azure gateway root certificate {h[..16]}... is missing from the Windows root store.");
        return string.Join("\n", AzureRootSha256.Select(h => found[h].Replace("\r", "").Trim()));
    }

    /// <summary>OpenVPN config. Token, management and device options go on the command line.</summary>
    public string RenderOpenVpnConfig()
    {
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append('\n');
        L("# Generated by P2S VPN from an Azure VPN Client profile. Contains the tls-auth key.");
        L("client");
        L("dev tun");
        L("proto tcp-client");
        foreach (var g in Gateways) L($"remote {g} 443");
        L("nobind");
        L("resolv-retry infinite");
        L("remote-cert-tls server");
        L($"verify-x509-name {CertificateName} name");
        L("cipher AES-256-GCM");
        L("data-ciphers AES-256-GCM");
        L("data-ciphers-fallback AES-256-GCM");
        L("auth SHA256");
        L("reneg-sec 0");
        L("auth-nocache");
        L("auth-user-pass");
        L("management-query-passwords");
        L("management-hold");
        L("verb 3");
        L("<ca>");
        L(AzureRootsPem());
        L("</ca>");
        L("key-direction 1");
        L("<tls-auth>");
        L("-----BEGIN OpenVPN Static key V1-----");
        for (int i = 0; i < ServerSecret.Length; i += 32) L(ServerSecret.Substring(i, 32));
        L("-----END OpenVPN Static key V1-----");
        L("</tls-auth>");
        return sb.ToString();
    }
}
