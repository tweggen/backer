using Hannibal;
using Hannibal.Models;
using Hannibal.Services;
using Higgins;
using Higgins.Services;
using Microsoft.AspNetCore.HttpLogging;

var builder = WebApplication.CreateBuilder(args);

// Add basic services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

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


app.MapGet("/api/hannibal/{jobId}", async (
    IHannibalService hannibalService,
    int jobId) =>
{
    var job = await hannibalService.GetJobAsync(jobId);
    return job is not null ? Results.Ok(job) : Results.NotFound();
})
.WithName("GetJob")
.WithOpenApi();


app.MapPost("/api/hannibal/acquireNextJob", async (
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


app.MapPost("/api/hannibal/reportJob", async (
    IHannibalService hannibalService,
    JobStatus jobStatus,
    CancellationToken cancellationToken) =>
{
    var result = await hannibalService.ReportJobAsync(jobStatus, cancellationToken);
    return Results.Ok(result);
})
.WithName("ReportJob")
.WithOpenApi();


app.MapPost("/api/hannibal/shutdown", async (
    IHannibalService hannibalService) =>
{
    var shutdownResult = await hannibalService.ShutdownAsync();
    return Results.Ok(shutdownResult);
})
.WithName("Shutdown")
.WithOpenApi();


app.MapPost("/api/higgins/endpoints/create", async (
        IHigginsService higginsService,
        Higgins.Models.Endpoint endpoint,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.CreateEndpointAsync(endpoint, cancellationToken);
        return Results.Ok(result);
    })
    .WithName("CreateEndpoint")
    .WithOpenApi();


app.MapPost("/api/higgins/routes/create", async (
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

app.Run();

