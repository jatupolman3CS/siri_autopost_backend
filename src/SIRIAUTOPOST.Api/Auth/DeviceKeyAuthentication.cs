using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Api.Auth;

// The extension authenticates with its device key in the X-Device-Key header. The key is looked up by
// its SHA-256 hash; the device id and workspace id become claims.
public sealed class DeviceKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IDeviceRepository devices, IDeviceSecrets secrets)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DeviceKey";
    public const string Header = "X-Device-Key";
    public const string DeviceClaim = "device_id";
    public const string WorkspaceClaim = "workspace_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[Header].ToString().Trim();
        if (key.Length == 0) return AuthenticateResult.NoResult();
        var device = await devices.GetByKeyHashAsync(secrets.Hash(key), Context.RequestAborted);
        if (device is null) return AuthenticateResult.Fail("unknown device key");
        var identity = new ClaimsIdentity(
            [new Claim(DeviceClaim, device.Id.ToString()), new Claim(WorkspaceClaim, device.WorkspaceId.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public sealed class CurrentDevice(IHttpContextAccessor http) : ICurrentDevice
{
    public Guid DeviceId => Read(DeviceKeyAuthenticationHandler.DeviceClaim);
    public Guid WorkspaceId => Read(DeviceKeyAuthenticationHandler.WorkspaceClaim);

    private Guid Read(string claim) =>
        Guid.TryParse(http.HttpContext?.User.FindFirstValue(claim), out var id)
            ? id
            : throw new AuthenticationException("คีย์อุปกรณ์ไม่ถูกต้อง จับคู่ส่วนขยายใหม่");
}
