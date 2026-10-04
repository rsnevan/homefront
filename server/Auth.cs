using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Homefront;

public record Principal(string Role, string Name, string? GuestId = null)
{
    public bool IsOwner => Role == "owner";
}

public static class Auth
{
    public const string Cookie = "hf_session";

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 3) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    record Payload(string r, string? g, long exp);

    public static string Issue(AppConfig cfg, string role, string? guestId, TimeSpan life)
    {
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(new Payload(role, guestId, DateTimeOffset.UtcNow.Add(life).ToUnixTimeSeconds())));
        return body + "." + Sign(cfg, body);
    }

    public static Principal? Resolve(HttpContext ctx, AppConfig cfg)
    {
        // The HTPC's own kiosk windows (ambient, player) are trusted.
        if (IsLocal(ctx)) return new Principal("owner", cfg.OwnerName);

        return ResolveToken(ctx.Request.Cookies[Cookie], cfg);
    }

    /// Truly on the HTPC. Tailscale Serve also connects from loopback, but always adds forwarding headers.
    public static bool IsLocal(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip == null || !IPAddress.IsLoopback(ip)) return false;
        var h = ctx.Request.Headers;
        return !h.ContainsKey("X-Forwarded-For") && !h.ContainsKey("Tailscale-User-Login") && !h.ContainsKey("X-Forwarded-Proto");
    }

    public static Principal? ResolveToken(string? token, AppConfig cfg)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var dot = token.IndexOf('.');
        if (dot < 0) return null;
        var body = token[..dot];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(cfg, body)), Encoding.ASCII.GetBytes(token[(dot + 1)..])))
            return null;
        Payload? p;
        try { p = JsonSerializer.Deserialize<Payload>(UnB64(body)); } catch { return null; }
        if (p == null || p.exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;

        if (p.r == "guest")
        {
            var pass = cfg.Auth.Guests.FirstOrDefault(x => x.Id == p.g);
            if (pass == null || !pass.Enabled || (pass.Expires is { } e && e < DateTimeOffset.UtcNow)) return null;
            return new Principal("guest", pass.Name, pass.Id);
        }
        return new Principal("owner", cfg.OwnerName);
    }

    public static void SetCookie(HttpContext ctx, string token, TimeSpan life) =>
        ctx.Response.Cookies.Append(Cookie, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps || ctx.Request.Headers["X-Forwarded-Proto"] == "https",
            MaxAge = life,
            IsEssential = true,
        });

    static string Sign(AppConfig cfg, string body) =>
        B64(HMACSHA256.HashData(Convert.FromHexString(cfg.Auth.Secret), Encoding.ASCII.GetBytes(body)));

    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
