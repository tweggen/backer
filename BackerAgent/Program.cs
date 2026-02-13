using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using System.Runtime.InteropServices;
using Hannibal;
using Hannibal.Client;
using BackerAgent;
using Microsoft.AspNetCore.Http.HttpResults;
using WorkerRClone;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Serilog;
using Tools;
using WorkerRClone.Configuration;
using WorkerRClone.Services;
using WorkerRClone.Services.Utils;

var builder = WebApplication.CreateBuilder(args);

// Detect platform at runtime
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "BackerAgent";
    });
    builder.Logging.AddEventLog(settings =>
    {
        settings.SourceName = "BackerService";
    });
}

string appName = "Backer";
var env = builder.Environment;
Tools.EnvironmentDetector.IsDevelopment = builder.Environment?.IsDevelopment() ?? false;

bool isInteractiveDev = EnvironmentDetector.IsInteractiveDev();
string logDir = EnvironmentDetector.GetLogDir(appName);

// Create directory (and fallback when appropriate)
logDir = EnvironmentDetector.EnsureDirectory(logDir, appName);

// Build final file path (use rolling file)
string logPath = Path.Combine(logDir, "service-.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        path: logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true) // useful when multiple processes/threads may write
    .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None) // monochrome output
    .CreateLogger();

builder.Host.UseSerilog();

// Add basic services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opt =>
{
    opt.SwaggerDoc("v1", new OpenApiInfo { Title = "hannibal", Version = "v1"});
    opt.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "Please enter token",
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        BearerFormat = "JWT",
        Scheme = "bearer"
    });
    
    opt.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type=ReferenceType.SecurityScheme,
                    Id="Bearer"
                }
            },
            new string[]{}
        }
    });
});
builder.Services.AddHealthChecks();
builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();
builder.Services
    .AddIdentityApiClient(builder.Configuration)
    .AddHttpContextAccessor()
    ;

builder.Services.AddScoped<IStaticTokenProvider, ConstantTokenProvider>();
builder.Services.AddSingleton<INetworkIdentifier, NetworkIdentifierHostedService>();
builder.Services.AddHostedService<NetworkIdentifierHostedService>();

// Configure HTTP logging
builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields = HttpLoggingFields.All;
    logging.RequestHeaders.Add("Authorization");
    logging.ResponseHeaders.Add("Content-Type");
});

builder.Services.AddSingleton<ConfigHelper<RCloneServiceOptions>>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<ConfigHelper<RCloneServiceOptions>>>();
    var helper = new ConfigHelper<RCloneServiceOptions>(logger,
        b =>
            b
                .AddUserSecrets<Program>(optional: true)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"RCloneService:oauth2:Providers:onedrive:Client{""}Id"] = "d9b13a4a-f0bd-4793-b638-93fc1b941662",
                    [$"RCloneService:oauth2:Providers:onedrive:Client{""}Secret"] = RClonePasswordObscurer.Reveal("TrBZ6QUdn4TJqv-x0fb7Mdb4WXHcAia8-3mjt5z4RxPY4owBV7UuLUw2ihchFX08zj0JibsW3r4"),
                    [$"RCloneService:oauth2:Providers:dropbox:Client{""}Id"] = "4bpfjazat4ruy39",
                    [$"RCloneService:oauth2:Providers:dropbox:Client{""}Secret"] = RClonePasswordObscurer.Reveal("5BTX7BJ0B9lMoTeSC65QiVCM1GzYGGxm7OtrlXcuBg"),
                    
                }));

    builder.Configuration.AddConfiguration(helper.Configuration);

    return helper;
});

builder.Services.Configure<RCloneServiceOptions>(
    builder.Configuration.GetSection("RCloneService"));

builder.Services.AddDataProtection();

// Add application services
builder.Services
        // Tools
    .AddProcessManager()
        // ClientHannibalServiceClient
    .AddBackgroundHannibalServiceClient(builder.Configuration)
        // Workers - this registers OAuth2ClientFactory, storage providers, and RCloneStorages
    .AddRCloneService(builder.Configuration)
    ;


builder.Services.AddSingleton<HttpBaseUrlAccessor>();

// Enhance RCloneService registration with BackerControl callbacks
builder.Services.AddSingleton(sp =>
{
    // Get the already-registered RCloneService
    var rcloneService = sp.GetServices<IHostedService>()
        .OfType<RCloneService>()
        .First();
    
    // Wire up callbacks for BackerControl notifications
    var hubContext = sp.GetRequiredService<IHubContext<BackerAgent.Hubs.BackerControlHub>>();
    
    rcloneService.OnStateChanged = (state) =>
    {
        // Broadcast state changes to all connected BackerControl clients
        _ = hubContext.Clients.All.SendAsync("ServiceStateChanged", state);
    };
    
    rcloneService.OnTransferStatsChanged = (stats) =>
    {
        // Broadcast job transfer stats to all connected BackerControl clients
        _ = hubContext.Clients.All.SendAsync("JobTransferStatsUpdated", stats);
    };
    
    return rcloneService;
});
builder.Services.AddSingleton<HubConnectionFactory>();
builder.Services.AddSingleton(provider =>
{
    var apiOptions = new RCloneServiceOptions();
    builder.Configuration.GetSection("RCloneService").Bind(apiOptions);
    
    var factory = provider.GetRequiredService<HubConnectionFactory>();
    var hannibalConnection = factory.CreateConnection($"{apiOptions.UrlSignalR}/hannibal");
    return new Dictionary<string, HubConnection>
    {
        { "hannibal", hannibalConnection }
    };
});

builder.Services.AddHostedService<HubConnectionService>();



builder.WebHost.ConfigureKestrel(options =>
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        // On Linux, you don't need special hosting code.
        // The app will be wrapped by systemd as a daemon.
        // But you can add logging tweaks if desired:
        options.UseSystemd();
    }
    
    /*
     * Default port listener.
     * Hard coded also in client.
     */
    options.ListenAnyIP(5931);
    
    /*
     * OAuth2 rclone redirect
     */
    options.ListenAnyIP(53682);
});

// Build the application
var app = builder.Build();

app.MapPost("/quit", async (
    RCloneService rCloneService,
    HttpContext ctx,
    CancellationToken cancellationToken
) =>
{
    await rCloneService.StopAsync(cancellationToken);
});


app.MapPost("/start", async (
    RCloneService rcloneService,
    HttpContext ctx,
    CancellationToken cancellationToken
) =>
{
    await rcloneService.StartJobsAsync(cancellationToken);
});


app.MapPost("/stop", async (
    RCloneService rcloneService,
    HttpContext ctx,
    CancellationToken cancellationToken
) =>
{
    await rcloneService.StopJobsAsync(cancellationToken);
});


app.MapGet("/config", async (
    HttpContext ctx,
    IOptions<RCloneServiceOptions> options,
    CancellationToken CancellationToken
) =>
{
    return Results.Ok(options.Value);
});


app.MapGet("/jobtransfers", async (
    RCloneService rcloneService,
    HttpContext ctx,
    CancellationToken cancellationToken
) =>
{
    return Results.Ok(await rcloneService.GetJobTransferStatsAsync(cancellationToken));
});


app.MapPost("/jobs/{jobId}/abort", async (
    int jobId,
    RCloneService rcloneService,
    CancellationToken cancellationToken
) =>
{
    await rcloneService.AbortJobAsync(jobId, cancellationToken);
    return Results.Ok();
});


app.MapPut("/config", async (
    HttpContext ctx,
    [FromBody] RCloneServiceOptions rcloneServiceOptions,
    ConfigHelper<RCloneServiceOptions> configHelper,
    CancellationToken cancellationToken
) =>
{
    if (string.IsNullOrWhiteSpace(rcloneServiceOptions.UrlSignalR))
    {
        return Results.BadRequest("Cloud URL must be provided.");
    }

    if (string.IsNullOrWhiteSpace(rcloneServiceOptions.BackerUsername)
        || string.IsNullOrWhiteSpace(rcloneServiceOptions.BackerPassword))
    {
        return Results.BadRequest("Backer username and password must both be provided.");
    }

    /*
     * Changing the configuration should trigger a change in the current configuration part.
     * Avoid certain critical data.
     */
    rcloneServiceOptions.RClonePath = null;
    rcloneServiceOptions.RCloneOptions = null;
    rcloneServiceOptions.OAuth2 = null;
    configHelper.Save(rcloneServiceOptions);
    
    return Results.Ok();
});


app.MapPut("/storages", async (
    HttpContext ctx,
    WorkerRClone.Services.RCloneService rcloneService,
    [FromBody] StorageOptions storageOptions,
    ConfigHelper<RCloneServiceOptions> configHelper,
    CancellationToken cancellationToken
) =>
{
    await rcloneService.SetStorageOptions(storageOptions, cancellationToken);
    
    return Results.Ok();
});


app.MapGet("/status", async (
    HttpContext ctx,
    WorkerRClone.Services.RCloneService rcloneService,
    CancellationToken cancellationToken
) =>
{
    return Results.Ok(rcloneService.GetState());
});


/*
 * For oauth2 path for rclone
 */
app.MapGet("/", async (
    HttpRequest httpRequest,
    IHannibalServiceClient hannibalServiceClient,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (httpRequest.Query.ContainsKey("error"))
        {
            /*
             * This is an error response.
             */
            string? errorString = httpRequest.Query["error"];
            string? errorDescriptionString = httpRequest.Query["error_description"];
            
            var result = await hannibalServiceClient.ProcessOAuth2ResultAsync(httpRequest, 
                null, null,
                errorString, errorDescriptionString,
                cancellationToken);

            return Results.Redirect(result.AfterAuthUri, permanent: false);
        }
        else
        {
            /*
             * This was successful. Forward this to the server.
             */
            var code = httpRequest.Query["code"];
            var state = httpRequest.Query["state"];

            if (string.IsNullOrWhiteSpace(code))
            {
                throw new UnauthorizedAccessException("No temporary code returned.");
            }

            if (string.IsNullOrWhiteSpace(state))
            {
                throw new UnauthorizedAccessException("No state returned.");
            }

            var result = await hannibalServiceClient.ProcessOAuth2ResultAsync(
                httpRequest, code, state, null, null, cancellationToken);

            /*
             * After return, we need to have a redirect to the original url.
             */
            return Results.Redirect(result.AfterAuthUri, permanent: false);
        }
    }
    catch (Exception e)
    {
            
    }
    return Results.Ok();
})
.WithName("DropboxOAuthCallback")
.RequireHost("*:53682");
;




// Configure middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseHttpLogging();
// app.UseAntiforgery();

// Health check endpoint
app.MapHealthChecks("/health");

// Map SignalR hub for BackerControl (local desktop control panel)
app.MapHub<BackerAgent.Hubs.BackerControlHub>("/backercontrolhub");

// Global error handler
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices
            .GetRequiredService<ILogger<Program>>();
        
        logger.LogError(ex, "An unhandled exception occurred");

        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new 
        {
            error = "An unexpected error occurred",
            requestId = context.TraceIdentifier
        });
    }
});


await app.StartAsync();
await app.WaitForShutdownAsync();