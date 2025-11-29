using Hannibal;
using Hannibal.Client;
using BackerAgent;
using Microsoft.AspNetCore.Http.HttpResults;
using WorkerRClone;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Tools;
using WorkerRClone.Configuration;
using WorkerRClone.Models;


var builder = WebApplication.CreateBuilder(args);

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
    var helper = new ConfigHelper<RCloneServiceOptions>();

    // Merge its configuration into the global pipeline
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
        // Workers
    // .AddRCloneService(builder.Configuration)
    ;


builder.Services.AddSingleton<HttpBaseUrlAccessor>();

builder.Services.AddSingleton<RCloneService>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RCloneService>());
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


builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5931); // Default port override
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

#if false
app.MapPost("/restart", async (
    RCloneService rcloneService,
    HttpContext ctx,
    CancellationToken cancellationToken
) =>
{
    await rcloneService.StopAsync(cancellationToken);
    await rcloneService.StartAsync(cancellationToken);
});
#endif


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


app.MapGet("/transfers", async (
    RCloneService rcloneService,
    HttpContext ctx,
    CancellationToken cancellationToken
) =>
{
    return Results.Ok(await rcloneService.GetTransferStatsAsync(cancellationToken));
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
     */
    configHelper.Save(rcloneServiceOptions);
    
    return Results.Ok();
});


app.MapGet("/status", async (
    HttpContext ctx,
    RCloneService rcloneService,
    CancellationToken cancellationToken
) =>
{
    return Results.Ok(rcloneService.GetState());
});


{
    app.Lifetime.ApplicationStarted.Register(async () =>
    {
        var connections = app.Services.GetRequiredService<Dictionary<string, HubConnection>>();
        await Task.WhenAll(connections.Values.Select(async conn =>
        {
            while (true)
            {
                try
                {
                    var t = conn.StartAsync();
                    Console.WriteLine("Connection started.");
                    return t;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error starting connection: {e.Message}");
                }
                await Task.Delay(1000);
            }
        }).ToArray());
    });

    app.Lifetime.ApplicationStopping.Register(async () =>
    {
        var connections = app.Services.GetRequiredService<Dictionary<string, HubConnection>>();
        await Task.WhenAll(connections.Values.Select(conn => conn.StopAsync()).ToArray());
    });
}


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