using Hannibal;
using Hannibal.Services;
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

app.MapPost("/api/hannibal/acquireNextJob", async (
    IHannibalService hannibalService,
    string capabilities,
    CancellationToken cancellationToken) =>
{
    var result = await hannibalService.AcquireNextJobAsync(capabilities, cancellationToken);
    return Results.Ok(result);
})
.WithName("AcquireNextJob")
.WithOpenApi();


app.MapPost("/api/hannibal/shutdown", async (
    IHannibalService hannibalService) =>
{
    var shutdownResult = await hannibalService.ShutdownAsync();
    return Results.Ok(shutdownResult);
})
.WithName("Shutdown")
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