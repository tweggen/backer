using Api;
using Api.Configuration;
using Hannibal;
using Hannibal.Client;
using Hannibal.Data;
using Hannibal.Models;
using Hannibal.Services;
using Higgins;
using Higgins.Client;
using Higgins.Data;
using Higgins.Services;
using WorkerRClone;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.FileProviders;
using Tools;


var builder = WebApplication.CreateBuilder(args);

// Add basic services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
builder.Services.AddSignalR();

// Configure HTTP logging
builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields = HttpLoggingFields.All;
    logging.RequestHeaders.Add("Authorization");
    logging.ResponseHeaders.Add("Content-Type");
});

builder.Services.Configure<ApiOptions>(
    builder.Configuration.GetSection("Api"));

// Add application services
builder.Services
        // Tools
    .AddProcessManager()
        // Application
    .AddHannibalService(builder.Configuration)
    .AddHigginsService(builder.Configuration)
        // Clients
    .AddHannibalServiceClient(builder.Configuration)
    .AddHigginsServiceClient(builder.Configuration)
        // Workers
    .AddRCloneService(builder.Configuration)
    .AddHannibalBackofficeService(builder.Configuration)
        // Razor
    .AddRazorComponents()
    .AddInteractiveServerComponents()
    ;


builder.Services.AddSingleton<HttpBaseUrlAccessor>();

builder.Services.AddSingleton<HubConnectionFactory>();
builder.Services.AddSingleton(provider =>
{
    var apiOptions = new ApiOptions();
    builder.Configuration.GetSection("Api").Bind(apiOptions);
    
    var factory = provider.GetRequiredService<HubConnectionFactory>();
    var hannibalConnection = factory.CreateConnection($"{apiOptions.UrlSignalR}/hannibal");
    var higginsConnection = factory.CreateConnection($"{apiOptions.UrlSignalR}/higgins");
    return new Dictionary<string, HubConnection>
    {
        { "hannibal", hannibalConnection },
        { "higgins", higginsConnection }
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
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<Poe.Components.App>()
    .AddInteractiveServerRenderMode();

// Health check endpoint
app.MapHealthChecks("/health");

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
        Path.Combine(builder.Environment.ContentRootPath, "..", "Frontend", "Poe", "wwwroot")),
    RequestPath = ""
});

app.MapHub<HannibalHub>("/hannibal");


app.MapGet("/api/hannibal/v1/jobs/{jobId}", async (
    IHannibalService hannibalService,
    int jobId,
    CancellationToken cancellationToken) =>
{
    try
    {
        var job = await hannibalService.GetJobAsync(jobId, cancellationToken);
        return Results.Ok(job);
    }
    catch (KeyNotFoundException e)
    {
        return Results.NotFound();
    }
})
.WithName("GetJob")
.WithOpenApi();


app.MapGet("/api/hannibal/v1/jobs", async (
    IHannibalService hannibalService,
    [FromQuery] int? page,
    [FromQuery] int? minState,
    [FromQuery] int? maxState,
    CancellationToken cancellationToken) =>
{
    var jobs = await hannibalService.GetJobsAsync(
        new ResultPage
        {
            Offset = 20*(page ?? 0), 
            Length = 20
        }, 
        new JobFilter
        {
            MinState = (Job.JobState) (minState ?? (int)Job.JobState.Preparing),
            MaxState = (Job.JobState) (maxState ?? (int)Job.JobState.DoneSuccess)
        }, 
        cancellationToken);
    return jobs is not null ? Results.Ok(jobs) : Results.Ok(new List<Job>());
})
.WithName("GetJobs")
.WithOpenApi();


app.MapPost("/api/hannibal/v1/acquireNextJob", async (
    IHannibalService hannibalService,
    AcquireParams acquireParams,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await hannibalService.AcquireNextJobAsync(acquireParams, cancellationToken);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException e)
    {
        return Results.NotFound();
    }
})
.WithName("AcquireNextJob")
.WithOpenApi();


app.MapPost("/api/hannibal/v1/reportJob", async (
    IHannibalService hannibalService,
    JobStatus jobStatus,
    CancellationToken cancellationToken) =>
{
    var result = await hannibalService.ReportJobAsync(jobStatus, cancellationToken);
    return Results.Ok(result);
})
.WithName("ReportJob")
.WithOpenApi();


app.MapPost("/api/hannibal/v1/shutdown", async (
    IHannibalService hannibalService, CancellationToken cancellationToken) =>
{
    var shutdownResult = await hannibalService.ShutdownAsync(cancellationToken);
    return Results.Ok(shutdownResult);
})
.WithName("Shutdown")
.WithOpenApi();


app.MapHub<HigginsHub>("/higgins");


app.MapGet("/api/higgins/v1/users/{id}", async (
        IHigginsService higginsService,
        int id,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.GetUserAsync(id, cancellationToken);
        return Results.Ok(result);
    })
    .WithName("GetUser")
    .WithOpenApi();

    
app.MapGet("/api/higgins/v1/storages", async (
        IHigginsService higginsService,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.GetStoragesAsync(cancellationToken);
        return Results.Ok(result);
    })
    .WithName("GetStorages")
    .WithOpenApi();

    
app.MapGet("/api/higgins/v1/storages/{id}", async (
        IHigginsService higginsService,
        int id,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.GetStorageAsync(id, cancellationToken);
        return Results.Ok(result);
    })
    .WithName("GetStorage")
    .WithOpenApi();

    
app.MapGet("/api/higgins/v1/endpoints", async (
    IHigginsService higginsService,
    CancellationToken cancellationToken) =>
{
    var result = await higginsService.GetEndpointsAsync(cancellationToken);
    return Results.Ok(result);
})
.WithName("GetEndpoints")
.WithOpenApi();

    
app.MapGet("/api/higgins/v1/endpoints/{name}", async (
    IHigginsService higginsService,
    string name,
    CancellationToken cancellationToken) =>
{
    var result = await higginsService.GetEndpointAsync(name, cancellationToken);
    return Results.Ok(result);
})
.WithName("GetEndpoint")
.WithOpenApi();

    
app.MapPost("/api/higgins/v1/endpoints/create", async (
    IHigginsService higginsService,
    Higgins.Models.Endpoint endpoint,
    CancellationToken cancellationToken) =>
{
    var result = await higginsService.CreateEndpointAsync(endpoint, cancellationToken);
    return Results.Ok(result);
})
.WithName("CreateEndpoint")
.WithOpenApi();


app.MapDelete("/api/higgins/v1/endpoints/{id}", async (
    IHigginsService higginsService,
    int id,
    CancellationToken cancellationToken) =>
{
    try
    {
        await higginsService.DeleteEndpointAsync(id, cancellationToken);
        return Results.Ok();
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
})
.WithName("DeleteEndpoint")
.WithOpenApi();


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


// Initialize database right after building the application
using (var scope = app.Services.CreateScope())
{
    {
        var hannibalContext = scope.ServiceProvider.GetRequiredService<HannibalContext>();
        await hannibalContext.InitializeDatabaseAsync();
    }
    {
        var higginsContext = scope.ServiceProvider.GetRequiredService<HigginsContext>();
        await higginsContext.InitializeDatabaseAsync();
    }
}


await app.StartAsync();
await app.WaitForShutdownAsync();
