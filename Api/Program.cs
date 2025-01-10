using Hannibal;
using Hannibal.Data;
using Hannibal.Models;
using Hannibal.Services;
using Higgins;
using Higgins.Data;
using Higgins.Services;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;

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

// Add application services
builder.Services
    .AddHannibalService(builder.Configuration)
    .AddHigginsService(builder.Configuration)
    // .AddMonitorService(builder.Configuration)
    // .AddMetadataService(builder.Configuration)
    ;

builder.Services.AddSingleton(provider =>
{
    var connection = new HubConnectionBuilder()
        .WithUrl("http://your-server-url/hubname")
        .Build();

    connection.On<string>("ReceiveMessage", (message) =>
    {
        Console.WriteLine($"Received message: {message}");
    });

    return connection;
});


var httpBaseUriAccessor = new HttpBaseUrlAccessor()
{
    SiteUrlString = builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey)
};
builder.Services.AddSingleton<IHttpBaseUrlAccessor>(httpBaseUriAccessor);

// Build the application
var app = builder.Build();

// Configure middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseHttpLogging();

// Health check endpoint
app.MapHealthChecks("/health");


app.MapGet("/api/hannibal/v1/jobs/{jobId}", async (
    IHannibalService hannibalService,
    int jobId,
    CancellationToken cancellationToken) =>
{
    var job = await hannibalService.GetJobAsync(jobId, cancellationToken);
    return job is not null ? Results.Ok(job) : Results.NotFound();
})
.WithName("GetJob")
.WithOpenApi();

app.MapHub<HannibalHub>("/hannibal");
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
        return jobs is not null ? Results.Ok(jobs) : Results.NotFound();
    })
    .WithName("GetJobs")
    .WithOpenApi();


app.MapPost("/api/hannibal/v1/acquireNextJob", async (
    IHannibalService hannibalService,
    string capabilities,
    string owner,
    CancellationToken cancellationToken) =>
{
    var result = await hannibalService.AcquireNextJobAsync(capabilities, owner, cancellationToken);
    return Results.Ok(result);
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
    IHannibalService hannibalService) =>
{
    var shutdownResult = await hannibalService.ShutdownAsync();
    return Results.Ok(shutdownResult);
})
.WithName("Shutdown")
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


app.MapPost("/api/higgins/v1/routes/create", async (
    IHigginsService higginsService,
    Higgins.Models.Route route,
    CancellationToken cancellationToken) =>
{
    var result = await higginsService.CreateRouteAsync(route, cancellationToken);
    return Results.Ok(result);
})
.WithName("CreateRoute")
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

app.Run();

