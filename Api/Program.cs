using System.Text;
using Api;
using Api.Configuration;
using Hannibal;
using Hannibal.Client;
using Hannibal.Data;
using Hannibal.Models;
using Hannibal.Services;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Tools;


var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>();

builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Debug);

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


// Configure HTTP logging
builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields = HttpLoggingFields.All;
    logging.RequestHeaders.Add("Authorization");
    logging.ResponseHeaders.Add("Content-Type");
});

builder.Services.Configure<ApiOptions>(
    builder.Configuration.GetSection("Api"));

builder.Services.AddDataProtection();

// Add application services
builder.Services
        // Application 
    .AddHannibalService(builder.Configuration)
        // Server side backoffice to process and maintain jobs
    .AddHannibalBackofficeService(builder.Configuration)
    ;

builder.Services.AddScoped<ITokenService, TokenService>();

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

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = false, // TXWTODO: This is bad
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
        // 🔍 Hook into JWT events for debugging
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                Console.WriteLine($"❌ Authentication failed: {context.Exception.Message}");
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                Console.WriteLine($"✅ Token validated for: {context.Principal.Identity?.Name}");
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                Console.WriteLine($"⚠️ Challenge triggered: {context.Error}, {context.ErrorDescription}");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITokenProvider, HttpContextTokenProvider>();

builder.Services.AddAuthorization();

// Build the application
var app = builder.Build();

//app.UsePathBase("/api/v1");
app.UseRouting();           // Enables endpoint routing
app.UseAuthentication();    // Parses and validates tokens
app.UseAuthorization();     // Applies authorization policies


app.MapGroup("/api/auth/v1/").MapIdentityApi<IdentityUser>();

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

#region Users
/*
 * Users
 */

app.MapPost("/api/authb/v1/token", async (
    SignInManager<IdentityUser> signInManager,
    UserManager<IdentityUser> userManager,
    ITokenService tokenService,
    LoginRequest loginRequest) =>
    {
        var user = await userManager.FindByEmailAsync(loginRequest.Email);
        if (user == null || !await userManager.CheckPasswordAsync(user, loginRequest.Password))
        {
            return TypedResults.Unauthorized();
        }

        var token = tokenService.CreateToken(user); // Your custom JWT logic
        Results<Ok<AccessTokenResponse>, EmptyHttpResult, ProblemHttpResult, UnauthorizedHttpResult>
            result = TypedResults.Ok(new AccessTokenResponse
        {
            AccessToken = token,
            ExpiresIn = 3600,
            RefreshToken = ""
        });
        
        return result;
    })
    .DisableAntiforgery()
    .WithName("Token")
    .WithOpenApi();


app.MapGet("/api/hannibal/v1/users/{id}", async (
        IHannibalService higginsService,
        int id,
        CancellationToken cancellationToken) =>
    {
        if (-1 != id)
        {
            return Results.Unauthorized();
        }
        IdentityUser? result = await higginsService.GetUserAsync(id, cancellationToken);
        if (null != result)
        {
            return Results.Ok(result);
        }
        else
        {
            return Results.NotFound();
        }
    })
    .RequireAuthorization()
    .WithName("GetUser")
    .WithOpenApi();


app.MapDelete("/api/authb/v1/deleteUser", async (
        SignInManager<IdentityUser> signInManager,
        UserManager<IdentityUser> userManager,
        ITokenService tokenService,
        string userId) =>
    {
        Results<Ok, NotFound, InternalServerError> callResult;
        
        var user = await userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return TypedResults.NotFound();
        }
        var deleteResult = await userManager.DeleteAsync(user);
        if (deleteResult.Succeeded)
        {
            callResult = TypedResults.Ok();
        }
        else
        {
            callResult = TypedResults.InternalServerError();
        }

        return callResult;
    })
    .DisableAntiforgery()
    .WithName("DeleteUser")
    .WithOpenApi();


app.MapPost("/api/hannibal/v1/users/triggerOAuth2", async (
    IHannibalService hannibalService,
    OAuth2Params oauth2Params,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await hannibalService.TriggerOAuth2Async(oauth2Params, cancellationToken);
        return Results.Ok(result);
    }
    catch (Exception e)
    {
        return Results.NotFound();
    }
})
    .RequireAuthorization()
    .WithName("TriggerOAuth2")
    .WithOpenApi();


#region OAuth2
app.MapGet("/api/hannibal/v1/oauth2/microsoft", async (
        HttpRequest request,
        IHannibalService higginsService,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await higginsService.ProcessOAuth2ResultAsync(
                request, "onedrive", cancellationToken);
            
            /*
             * After return, we need to have a redirect to the original url.
             */
            return Results.Redirect(result.AfterAuthUri, permanent: false);
        }
        catch (Exception e)
        {
            
        }
        return Results.Ok();
    })
    .WithName("MicrosoftOAuthCallback");

    app.MapGet("/api/hannibal/v1/oauth2/dropbox", async (
            HttpRequest request,
            IHannibalService higginsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await higginsService.ProcessOAuth2ResultAsync(
                    request, "dropbox", cancellationToken);
                
                /*
                 * After return, we need to have a redirect to the original url.
                 */
                return Results.Redirect(result.AfterAuthUri, permanent: false);
            }
            catch (Exception e)
            {
                
            }
            return Results.Ok();
        })
        .WithName("DropboxOAuthCallback");

#endregion

#endregion

#region Rules
/*
 * Rules
 */

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
.RequireAuthorization()
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
.RequireAuthorization()
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
.RequireAuthorization()
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
    .RequireAuthorization()
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
    .RequireAuthorization()
    .WithName("DeleteRule")
    .WithOpenApi();

#endregion


#region Jobs
/*
 * Jobs
 */

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
.RequireAuthorization()
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
.RequireAuthorization()
.WithName("GetJobs")
.WithOpenApi();


app.MapDelete("/api/hannibal/v1/jobs", async (
    IHannibalService hannibalService,
    CancellationToken cancellationToken) =>
{
    await hannibalService.DeleteJobsAsync(cancellationToken);
});


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
.RequireAuthorization()
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
.RequireAuthorization()
.WithName("ReportJob")
.WithOpenApi();

#endregion


#region Lifecycle
/*
 * Lifecycle
 */

app.MapPost("/api/hannibal/v1/shutdown", async (
    IHannibalService hannibalService, CancellationToken cancellationToken) =>
{
    var shutdownResult = await hannibalService.ShutdownAsync(cancellationToken);
    return Results.Ok(shutdownResult);
})
.RequireAuthorization()
.WithName("Shutdown")
.WithOpenApi();

#endregion


#region Storages
/*
 * Storages
 */

app.MapGet("/api/hannibal/v1/storages", async (
        IHannibalService higginsService,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.GetStoragesAsync(cancellationToken);
        return Results.Ok(result);
    })
    .RequireAuthorization()
    .WithName("GetStorages")
    .WithOpenApi();

    
app.MapGet("/api/hannibal/v1/storages/{id}", async (
        IHannibalService higginsService,
        int id,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.GetStorageAsync(id, cancellationToken);
        return Results.Ok(result);
    })
    .RequireAuthorization()
    .WithName("GetStorage")
    .WithOpenApi();

    
app.MapPut("/api/hannibal/v1/storages/{id}", async (
        IHannibalService higginsService,
        int id,
        Hannibal.Models.Storage storage,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await higginsService.UpdateStorageAsync(id, storage, cancellationToken);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    })
    .RequireAuthorization()
    .WithName("UpdateStorage")
    .WithOpenApi();


app.MapPost("/api/hannibal/v1/storages", async (
        IHannibalService higginsService,
        Hannibal.Models.Storage storage,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.CreateStorageAsync(storage, cancellationToken);
        return Results.Ok(result);
    })
    .RequireAuthorization()
    .WithName("CreateStorage")
    .WithOpenApi();


app.MapDelete("/api/hannibal/v1/storages/{id}", async (
        IHannibalService higginsService,
        int id,
        CancellationToken cancellationToken) =>
    {
        try
        {
            await higginsService.DeleteStorageAsync(id, cancellationToken);
            return Results.Ok();
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    })
    .RequireAuthorization()
    .WithName("DeleteStorage")
    .WithOpenApi();

#endregion


#region Endpoints
/*
 * Endpoints
 */

app.MapGet("/api/hannibal/v1/endpoints", async (
    IHannibalService higginsService,
    CancellationToken cancellationToken) =>
{
    var result = await higginsService.GetEndpointsAsync(cancellationToken);
    return Results.Ok(result);
})
.RequireAuthorization()
.WithName("GetEndpoints")
.WithOpenApi();

    
app.MapGet("/api/hannibal/v1/endpoints/{name}", async (
    IHannibalService higginsService,
    string name,
    CancellationToken cancellationToken) =>
{
    var result = await higginsService.GetEndpointAsync(name, cancellationToken);
    return Results.Ok(result);
})
.RequireAuthorization()
.WithName("GetEndpoint")
.WithOpenApi();

    
app.MapPut("/api/hannibal/v1/endpoints/{id}", async (
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
.RequireAuthorization()
.WithName("UpdateEndpoint")
.WithOpenApi();


app.MapPost("/api/hannibal/v1/endpoints", async (
    IHannibalService higginsService,
    Hannibal.Models.Endpoint endpoint,
    CancellationToken cancellationToken) =>
{
    var result = await higginsService.CreateEndpointAsync(endpoint, cancellationToken);
    return Results.Ok(result);
})
.RequireAuthorization()
.WithName("CreateEndpoint")
.WithOpenApi();


app.MapDelete("/api/hannibal/v1/endpoints/{id}", async (
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
.RequireAuthorization()
.WithName("DeleteEndpoint")
.WithOpenApi();

#endregion


#region Config
/*
 * Config
 */

app.MapGet("/api/hannibal/v1/dump", async (
        IHannibalService higginsService,
        [FromQuery] bool includeInactive,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.ExportConfig(includeInactive, cancellationToken);
        return Results.Ok(result);
    })
    .RequireAuthorization()
    .WithName("ExportConfig")
    .WithOpenApi();

    
app.MapPost("/api/hannibal/v1/dump", async (
        IHannibalService higginsService,
        string configJson,
        MergeStrategy mergeStrategy,
        CancellationToken cancellationToken) =>
    {
        var result = await higginsService.ImportConfig(configJson, mergeStrategy, cancellationToken);
        return Results.Ok(result);
    })
    .RequireAuthorization()
    .WithName("ImportConfig")
    .WithOpenApi();

#endregion


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
        hannibalContext.Database.Migrate();
        await hannibalContext.InitializeDatabaseAsync();
    }
}


await app.StartAsync();
await app.WaitForShutdownAsync();