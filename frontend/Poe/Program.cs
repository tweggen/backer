using System.Text;
using Hannibal;
using Poe.Components;
using Hannibal.Client;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Tools;

var basePath =  Environment.GetEnvironmentVariable("ASPNETCORE_BASEPATH");
if (null == basePath)
{
    basePath = "";
}


var builder = WebApplication.CreateBuilder(args);

builder.Services
    // Clients
    .AddTransient<IdentityCookieHandler>()
    .AddIdentityApiClient(builder.Configuration)
    .AddHttpContextAccessor();
    
builder.Services
    .AddHttpClient("AuthenticatedClient")
        .AddHttpMessageHandler<IdentityCookieHandler>();

builder.Services.AddScoped<AddTokenHandler>();

builder.Services
    .AddFrontendHannibalServiceClient(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITokenProvider, HttpContextTokenProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie("Cookies", options =>
    {
        options.LoginPath = $"{basePath}/login";
        options.LogoutPath = $"{basePath}/logout";
        options.AccessDeniedPath = $"{basePath}/access-denied";
    })
    ;

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
    ;

builder.Services.AddSingleton(new AppBasePath(basePath));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.RequireHeaderSymmetry = false;
    options.ForwardLimit = null; // Remove limit on number of forwarders
    options.KnownNetworks.Clear(); // Optional: trust all
    options.KnownProxies.Clear();  // Optional: trust all
});

var app = builder.Build();

app.UsePathBase(basePath);
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler($"{basePath}/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.UseAntiforgery();

#if false
app.Use(async (context, next) =>
{
    if (context.Request.Query.TryGetValue("ReturnUrl", out var returnUrl))
    {
        if (!returnUrl.ToString().StartsWith("/app1"))
        {
            var corrected = $"/app1{returnUrl}";
            context.Request.QueryString = new QueryString($"?ReturnUrl={Uri.EscapeDataString(corrected)}");
        }
    }

    await next();
});
#endif

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();


app.MapGet("/debug", (HttpRequest req) =>
{
    return new
    {
        Scheme = req.Scheme,
        Headers = req.Headers["X-Forwarded-Proto"].ToString()
    };
});

app.Run();
#if false
{
  "tokenType": "Bearer",
  "accessToken": "CfDJ8GjUggLe6g5BqPAD5yeBor2q4SZ7usA9BiYzKcBrksAxUMkq5_Jg2GoHvvwK3v7NA2MFF3b6RtJQt72xdZl73nWs8DGdDS1Tvd9dATWoTR23GLGCzZhHjuWPmkg3xkMfbABYuGpvJYksrlP6gvoIZ0r3U2A8uuKG4CnzKobuXIZ7punlFmPI79zlL7cRTvkiaOo5JZnpmILSCCS_xrrbHOzpH-teKgFnviCl_PpgaZvcqqDyk4Om0gMgR2QxCsx4WJHHueoFciwUvbI4NSaAGTmxKCFfyrSfnFHDd_rtxpirqBAoK3xfaXyKdny05uwDYwP3TVjFRTRD-rbFyyQEKL4l0HBzIgmhC9ifMSBJSxsaCDliiOzH6RocgxSmIQ9Uu-SlBcqm_v2UlhxExwxcI1GEI8OSqBl6RFRPZH9n4Xu_bCR2eARdv0QWS_0o-td03ZXQpKGJjZsSiWtjrgCfLLh2PyjIeRtZyGKRSQSooPfG-CVRr9LoCGm95hIOpSBiKVrIzq5WwQusfb7ssU_4RW51OOOX4hgttr-uvQ9g4gcCbdwBuZMlu9QGQEkOUBBAwxqEesc4kP61EKCyo7GFWVflQS5l9deJ9wUXjg-GTDmYtoONFE0nxvHXCXf4ZzkOLh5kYB0U4KW7MBxbbW5U2MhkSx5jmCFKupuUrerdyDYaL_Ms4e4CKlvxJ88tUfZC88ezVgaXs5Az_lX_atPtrm4",
  "expiresIn": 3600,
  "refreshToken": "CfDJ8GjUggLe6g5BqPAD5yeBor30tE8fLffqabBAIvhpZibMR76H3OleUSy0oPQNB9B0_041Tfqskzjce6JUVmgLv--Cb5okfipCT9eh7Ed7pNyHJx53CI-i4CUDnr0QX3SRBnFrtrATMSxORZJoi9e-7W4R93y-UKU6pEoa9_JjF66MInE1dN-1emM2YIH8eHZXpyC09RWV5ljJSB6sNV52xJeFjMJIy9JAya3lcSCs78XrsvemXWwPOkJxLlSozCBVZZS--4sHErDlZ0tsGo2SQ4TXZOAgOZ16sHsAZqLwYUBcfNVan7fp64jgc3wMcRGVhTV7GNTV0JdgRNl3tKxPFxn25FPfetTZfpu_JXbIs7xxbmI7Q8cXgkaQCogIGI3RYuXKa_eTmdWRlCBhqFxP_P5HMZyPB6wejdi-PS5LzQpdnwRWzhTu4gLsT-pf7rBKSt1x6gc0xIycYxThfa0aeYwsSJBItgtNo_V1yc2kEg_GSigG4adv1JXslIT9JndJGWz3DYqczvifzEsQAjqtU058dq2ulsZ7LaY_zamsZMm5zR-oRYzw7kNEGFqxTcyztqOWIrT-xVvcjxnKvzjyPzPskDtYyHJn5pzLQrZnTgRgOMf3MhP9weUEBwfVyBYfiQInuf_5qG_THbRZvj_VxVBehJyr6O86TjBopNeGZ7gzBz-yV2wC95l-FOXABDjhCHFR_X64xcR2Y76poRoJwSg"
}
#endif