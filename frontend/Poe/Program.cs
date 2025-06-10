using Poe.Components;
using Hannibal.Client;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    // Clients
    .AddHannibalServiceClient(builder.Configuration)
    .AddIdentityApiClient(builder.Configuration);

builder.Services.AddAuthentication("Cookies")
    .AddCookie("Cookies", options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/access-denied";
    });


// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
    ;

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
#if false
{
  "tokenType": "Bearer",
  "accessToken": "CfDJ8MC0VvYWPSlElKGQXtpGGkDNDR317H1t4NWn82BjEaRFZmTSdCh5FCaASdXhJC_HRzeHt3dwpVH-Kf2QyDFWY0uEM811SktuzJ6tysCwJuk0KQ9pbVf4K2bYKyf7V9iH-o6bndL5-BIYl72URrNzJDKLO0uqzG-e1dCURg8yeXQGVdzPK4JCN_LT7qU6cyOLeu1ZPLgpVFRGK11Bifr2JSg-RrETHnCkBXzTf3bB_Z1tGC8Tie05rvtooBOhnn8VZvVTSOl2BT0QH0Gny7mpViCHGbc70iHyHdDN3Annp7VsE8k4Da8ZEQzVqMSDJJ4Eboha2aYU6BFDr4TP57H0d7syUwA_mr6cWySOVA3_sO5PsRKr8HSVn7ZV4dsi9ZseyHpzfD1_kSdpmctj4-5M1n8N_LUP_LIie-9kJMFATPsiqOK5M_CFM-MBKv1EAf_vHHV4M8FANPOlLBMgyIO4mgu9V47TVdyrXN24IYIlp0qlV-_p2ODF5GRcIzn662jVDz2-vg2LPd1FCDx0CXE68Nl0sWU1Ow-89vdiqCqG4YT4_YKXEnvVxMcnx-Dcl8uKlJUdhJuE9sYoJj52JuG2vFResj1EgZTj7v47AnfEM9APBp6mEt0cOGLxMyZvlpg7aAbfgwvStaKhXPph6loV_niSi2Se5o9q5xIuEQ-P4blnX22zN9scXAnhQWVjIAI2YN8eDbpmvTgdF6BmfXJSF0M",
  "expiresIn": 3600,
  "refreshToken": "CfDJ8MC0VvYWPSlElKGQXtpGGkA_lWdxJgI3XTpqXTgn7ZczgJEYAqTNg3MKfgHZpnLLIWtom9EyzO6xnp61vaCaD0qt9jnIuP9fdES2RPwRHGjtAI9hzDNSeLpeP8bNTsW1d0GV_0UpUlpn1_zXy-WWo1ptUxAw72Z64xaf_DfCpIZluLI5JwYZVf_Hu10jEPgAoPuiKjFRqhQ5pbzaK_oXOgjea0oAV17JDfvd95MBELKPY-Nvnbx3DpB-u0QuAbl6KtDFoT891zJNM527JTIypuApL0FXBY6tROZpedvjPsEhJTDPOrtasb-nSirwBgRPYZ6gqCkdVlg_IXtbnv_OudcV58dMKUAf1hc6lX5J7sXaBzbqlHh3KxwrtVBoXrma-Ab0vjOZkbGlzEuWpOLwUFt1VC-y8gTxtYDlfWZR3rSodqp_TZEgrw2ND5pz8c1gkejPr_XsjPCQLcoB0zNoGkwUlzsioaj1UEo9j2VW5AJOFwD7M45RPRYBbFuADwjSbDA6wNtGfYzr0sLo1dT5UsKbJ_H_wiGQ0WmM4XdA_41DqWqS4aBWpMtcPSRV6QaVexKuDVFK7WV6QmaxIrAwdriTeeZtESgg-TD6NY8RTNL8zFroohPEg7--ZDVD_Av3Hg4f1nrG0NmygTWznT_2x5oNVVhcSRdImaBbO0JcSUajJbUSKcaL4QzTRhENVFkyxbrNfvkH6DIXrh8KDF5Fmu4"
}
#endif