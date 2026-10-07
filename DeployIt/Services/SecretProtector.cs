using Microsoft.AspNetCore.DataProtection;

namespace DeployIt.Services;

public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("DeployIt.Secrets.v1");
    public string Protect(string value) => string.IsNullOrEmpty(value) ? "" : protector.Protect(value);
    public string Unprotect(string value) => string.IsNullOrEmpty(value) ? "" : protector.Unprotect(value);
}
