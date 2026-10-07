using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace DeployIt.Services;

public sealed class AdminCredentials
{
    public string Username { get; }
    private readonly byte[] passwordHash;
    private readonly string version;

    public AdminCredentials(IConfiguration configuration, string dataDirectory)
    {
        Username = configuration["Admin:Username"] ?? "admin";
        var password = configuration["Admin:Password"];
        if (string.IsNullOrEmpty(password))
        {
            var path = Path.Combine(dataDirectory, "admin-password");
            if (!File.Exists(path))
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                file.Write(Encoding.UTF8.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(24))));
            }
            password = File.ReadAllText(path).Trim();
        }
        if (password.Length < 12) throw new InvalidOperationException("Admin password must have at least 12 characters.");
        passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        version = Convert.ToHexString(passwordHash);
    }
    public bool Check(string username, string password) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(password)), passwordHash)
        && string.Equals(username, Username, StringComparison.Ordinal);
    public ClaimsPrincipal Principal(DateTimeOffset expires) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.Name, Username), new Claim("credentialVersion", version),
        new Claim("expires", expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
    ], "Cookies"));
    public bool IsValid(ClaimsPrincipal? user) => user?.Identity?.IsAuthenticated == true
        && user.Identity.Name == Username && user.FindFirst("credentialVersion")?.Value == version
        && long.TryParse(user.FindFirst("expires")?.Value, out var expires)
        && expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

public sealed class SessionAuthenticationStateProvider(ILoggerFactory logger, AdminCredentials admin)
    : RevalidatingServerAuthenticationStateProvider(logger)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
        => Task.FromResult(admin.IsValid(state.User));
}
