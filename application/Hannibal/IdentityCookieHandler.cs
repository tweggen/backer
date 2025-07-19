using Microsoft.AspNetCore.Http;

namespace Hannibal;

public class IdentityCookieHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _accessor;

    public IdentityCookieHandler(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var cookie = _accessor.HttpContext?.Request.Cookies[".AspNetCore.Identity.Application"];
        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.Add("Cookie", $".AspNetCore.Identity.Application={cookie}");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
