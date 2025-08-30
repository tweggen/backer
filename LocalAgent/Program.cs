using System.Security.Claims;
using Hannibal;
using Hannibal.Client;
using LocalAgent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WorkerRClone;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Tools;
using WorkerRClone.Configuration;


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

// Configure HTTP logging
builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields = HttpLoggingFields.All;
    logging.RequestHeaders.Add("Authorization");
    logging.ResponseHeaders.Add("Content-Type");
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
    .AddRCloneService(builder.Configuration)
    ;


builder.Services.AddSingleton<HttpBaseUrlAccessor>();

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


// Build the application
var app = builder.Build();


{
    app.Lifetime.ApplicationStarted.Register(async () =>
    {
        var connections = app.Services.GetRequiredService<Dictionary<string, HubConnection>>();
        await Task.WhenAll(connections.Values.Select(conn =>
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
                Task.Delay(1000);
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