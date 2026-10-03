using Microsoft.Extensions.Options;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Infrastructure.Payments;

namespace SIRIAUTOPOST.Api.Auth;

/// <summary>
/// Where Stripe sends the customer back to: Stripe:ReturnBaseUrl when set, else the origin of the browser
/// that asked (a bearer-token call from the web app always carries it), else the host the request came to.
/// </summary>
public sealed class AppUrls(IHttpContextAccessor http, IOptions<StripeOptions> options) : IAppUrls
{
    public string WebBase
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(options.Value.ReturnBaseUrl)) return options.Value.ReturnBaseUrl.Trim().TrimEnd('/');
            var request = http.HttpContext?.Request ?? throw new InvalidOperationException("No request to take the web address from");
            if (Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) && origin.Scheme is "http" or "https")
                return origin.GetLeftPart(UriPartial.Authority);
            return $"{request.Scheme}://{request.Host}";
        }
    }
}
