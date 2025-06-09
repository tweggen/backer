using Api;
using Api.Configuration;
using Hannibal;
using Hannibal.Client;
using Hannibal.Data;
using Hannibal.Models;
using Hannibal.Services;
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
        // Client
    .AddHannibalServiceClient(builder.Configuration)
        // Workers
    .AddRCloneService(builder.Configuration)
    .AddHannibalBackofficeService(builder.Configuration)
    ;


builder.Services.AddSingleton<HttpBaseUrlAccessor>();

builder.Services.AddSingleton<HubConnectionFactory>();
builder.Services.AddSingleton(provider =>
{
    var apiOptions = new ApiOptions();
    builder.Configuration.GetSection("Api").Bind(apiOptions);
    
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

app.UseStaticFiles();

app.MapHub<HannibalHub>("/hannibal");


app.MapGet("/api/hannibal/v1/rules/{ruleId}", async (
    IHannibalService hannibalService,
    int ruleId,
    CancellationToken cancellationToken) =>
{
    try
    {
        var rule = await hannibalService.GetRuleAsync(ruleId, cancellationToken);
        return Results.Ok(rule);
    }
    catch (KeyNotFoundException e)
    {
        return Results.NotFound();
    }
})
.WithName("GetRule")
.WithOpenApi();


app.MapPost("/api/hannibal/v1/rules", async (
    IHannibalService hannibalService,
    Hannibal.Models.Rule rule,
    CancellationToken cancellationToken) =>
{
    var result = await hannibalService.CreateRuleAsync(rule, cancellationToken);
    return Results.Ok(result);
})
.WithName("CreateRule")
.WithOpenApi();


app.MapGet("/api/hannibal/v1/rules", async (
    IHannibalService hannibalService,
    [FromQuery] int? page,
    [FromQuery] int? minState,
    [FromQuery] int? maxState,
    CancellationToken cancellationToken) =>
{
    var rules = await hannibalService.GetRulesAsync(
        new ResultPage
        {
            Offset = 20*(page ?? 0), 
            Length = 20
        }, 
        new RuleFilter
        {
        }, 
        cancellationToken);
    return rules is not null ? Results.Ok(rules) : Results.Ok(new List<Rule>());
})
.WithName("GetRules")
.WithOpenApi();


app.MapPut("/api/hannibal/v1/rules/{id}", async (
        IHannibalService hannibalService,
        int id,
        Hannibal.Models.Rule rule,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await hannibalService.UpdateRuleAsync(id, rule, cancellationToken);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    })
    .WithName("UpdateRule")
    .WithOpenApi();


app.MapDelete("/api/hannibal/v1/rules/{id}", async (
        IHannibalService hannibalService,
        int id,
        CancellationToken cancellationToken) =>
    {
        try
        {
            await hannibalService.DeleteRuleAsync(id, cancellationToken);
            return Results.Ok();
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    })
    .WithName("DeleteRule")
    .WithOpenApi();


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


app.MapGet("/api/higgins/v1/users/{id}", async (
        IHannibalService higginsService,
        int id,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.GetUserAsync(id, cancellationToken);
        return Results.Ok(result);
    })
    .WithName("GetUser")
    .WithOpenApi();

    
app.MapGet("/api/higgins/v1/storages", async (
        IHannibalService higginsService,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.GetStoragesAsync(cancellationToken);
        return Results.Ok(result);
    })
    .WithName("GetStorages")
    .WithOpenApi();

    
app.MapGet("/api/higgins/v1/storages/{id}", async (
        IHannibalService higginsService,
        int id,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.GetStorageAsync(id, cancellationToken);
        return Results.Ok(result);
    })
    .WithName("GetStorage")
    .WithOpenApi();

    
app.MapGet("/api/higgins/v1/endpoints", async (
    IHannibalService higginsService,
    CancellationToken cancellationToken) =>
{
    var result = await higginsService.GetEndpointsAsync(cancellationToken);
    return Results.Ok(result);
})
.WithName("GetEndpoints")
.WithOpenApi();

    
app.MapGet("/api/higgins/v1/endpoints/{name}", async (
    IHannibalService higginsService,
    string name,
    CancellationToken cancellationToken) =>
{
    var result = await higginsService.GetEndpointAsync(name, cancellationToken);
    return Results.Ok(result);
})
.WithName("GetEndpoint")
.WithOpenApi();

    
app.MapPut("/api/higgins/v1/endpoints/{id}", async (
    IHannibalService higginsService,
    int id,
    Hannibal.Models.Endpoint endpoint,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await higginsService.UpdateEndpointAsync(id, endpoint, cancellationToken);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
})
.WithName("UpdateEndpoint")
.WithOpenApi();


app.MapPost("/api/higgins/v1/endpoints", async (
    IHannibalService higginsService,
    Hannibal.Models.Endpoint endpoint,
    CancellationToken cancellationToken) =>
{
    var result = await higginsService.CreateEndpointAsync(endpoint, cancellationToken);
    return Results.Ok(result);
})
.WithName("CreateEndpoint")
.WithOpenApi();


app.MapDelete("/api/higgins/v1/endpoints/{id}", async (
    IHannibalService higginsService,
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



app.MapGet("/api/higgins/v1/dump", async (
        IHannibalService higginsService,
        [FromQuery] bool includeInactive,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.ExportConfig(includeInactive, cancellationToken);
        return Results.Ok(result);
    })
    .WithName("ExportConfig")
    .WithOpenApi();

    
app.MapPost("/api/higgins/v1/dump", async (
        IHannibalService higginsService,
        string configJson,
        MergeStrategy mergeStrategy,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.ImportConfig(configJson, mergeStrategy, cancellationToken);
        return Results.Ok(result);
    })
    .WithName("ImportConfig")
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
}


await app.StartAsync();
await app.WaitForShutdownAsync();
