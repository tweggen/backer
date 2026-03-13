# Authentication Architecture

## Overview

Backer uses JWT (JSON Web Token) authentication across all components. This document describes how authentication works in the Frontend (Poe/Blazor), BackerAgent, and the API (Hannibal).

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              API (Hannibal)                                  │
│                                                                             │
│  ┌─────────────────────┐    ┌─────────────────────┐                        │
│  │  /api/authb/v1/token │    │  JWT Validation     │                        │
│  │  (Token Endpoint)    │    │  Middleware         │                        │
│  │                      │    │                     │                        │
│  │  - Validates creds   │    │  - ValidateIssuer   │                        │
│  │  - Returns JWT       │    │  - ValidateAudience │                        │
│  │                      │    │  - ValidateSigningKey│                        │
│  └─────────────────────┘    └─────────────────────┘                        │
│            ↑                          ↑                                     │
└────────────│──────────────────────────│─────────────────────────────────────┘
             │                          │
             │ POST credentials         │ Bearer token
             │                          │
┌────────────│──────────────────────────│─────────────────────────────────────┐
│            │                          │                                     │
│  ┌─────────┴───────────┐    ┌────────┴────────┐                            │
│  │   AutoAuthHandler   │───▶│  HTTP Requests  │                            │
│  │                     │    │  with JWT       │                            │
│  │  - Intercepts 401   │    │                 │                            │
│  │  - Auto-refreshes   │    │  Authorization: │                            │
│  │  - Retries request  │    │  Bearer eyJ...  │                            │
│  └─────────────────────┘    └─────────────────┘                            │
│                                                                             │
│                         BackerAgent                                         │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│                                                                             │
│  ┌─────────────────────┐    ┌─────────────────────┐                        │
│  │  Cookie Auth        │    │  AddTokenHandler    │                        │
│  │  (ASP.NET Identity) │───▶│                     │                        │
│  │                     │    │  - Extracts token   │                        │
│  │  - Login via UI     │    │  - Adds to requests │                        │
│  │  - Session cookie   │    │                     │                        │
│  └─────────────────────┘    └─────────────────────┘                        │
│                                                                             │
│                      Frontend (Poe/Blazor)                                  │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Components

### 1. API (Hannibal) - Token Issuer & Validator

The API serves as both the **token issuer** and the **resource server**.

#### Token Endpoint

**Endpoint:** `POST /api/authb/v1/token`

**Purpose:** Issues JWT tokens in exchange for valid credentials.

**Location:** `Api/Program.cs`

```csharp
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

    var token = tokenService.CreateToken(user);
    return TypedResults.Ok(new AccessTokenResponse
    {
        AccessToken = token,
        ExpiresIn = 3600,
        RefreshToken = ""
    });
});
```

#### Token Creation

**Location:** `Tools/TokenService.cs`

```csharp
public string CreateToken(IdentityUser user)
{
    var claims = new List<Claim>
    {
        new Claim(JwtRegisteredClaimNames.Sub, user.Id),
        new Claim(JwtRegisteredClaimNames.Email, user.Email ?? ""),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Name, user.UserName ?? "")
    };

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer: _configuration["Jwt:Issuer"],
        audience: _configuration["Jwt:Audience"],
        claims: claims,
        expires: DateTime.UtcNow.AddHours(1),
        signingCredentials: creds
    );

    return new JwtSecurityTokenHandler().WriteToken(token);
}
```

#### JWT Validation Middleware

**Location:** `Api/Program.cs`

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = false,  // TODO: Enable in production
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });
```

#### Configuration

**Location:** `Api/appsettings.json`

```json
{
  "Jwt": {
    "Key": "pzPm/u01w76NOGz0Vf2UDLqa/d8OyCNOg2Ml9jWl00A=",
    "Issuer": "your-app-name",
    "Audience": "your-app-client"
  }
}
```

**Important:** The same `Key`, `Issuer`, and `Audience` values must be used for both token creation and validation.

---

### 2. BackerAgent - Background Service Authentication

BackerAgent runs as a background service without user interaction. It authenticates automatically using stored credentials.

#### Key Components

| Component | Purpose |
|-----------|---------|
| `AutoAuthHandler` | HTTP message handler that intercepts 401s and auto-refreshes tokens |
| `ConstantTokenProvider` | Stores the current JWT token in memory |
| `IIdentityApiService` | Client for the token endpoint |

#### Authentication Flow

```
1. BackerAgent starts
   ↓
2. Makes API request (no token yet)
   ↓
3. API returns 401 Unauthorized
   ↓
4. AutoAuthHandler intercepts 401
   ↓
5. Calls /api/authb/v1/token with stored credentials
   ↓
6. Receives JWT token
   ↓
7. Stores token in ConstantTokenProvider
   ↓
8. Retries original request WITH token
   ↓
9. API validates token → 200 OK
```

#### AutoAuthHandler

**Location:** `Tools/AutoAuthHandler.cs`

**Purpose:** Automatically handles token acquisition and refresh.

```csharp
protected override async Task<HttpResponseMessage> SendAsync(
    HttpRequestMessage request, 
    CancellationToken cancellationToken)
{
    // 1. Add existing token if available
    var token = await _staticTokenProvider.GetToken();
    if (!string.IsNullOrEmpty(token))
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    // 2. Send request
    var response = await base.SendAsync(request, cancellationToken);

    // 3. If 401, obtain new token and retry
    if (response.StatusCode == HttpStatusCode.Unauthorized)
    {
        var newToken = await _obtainTokenAsync(_serviceProvider, cancellationToken);
        if (string.IsNullOrWhiteSpace(newToken))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        
        _staticTokenProvider.SetToken(newToken);
        
        // Clone and retry with new token
        var newRequest = await CloneHttpRequestMessageAsync(request);
        newRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
        
        return await base.SendAsync(newRequest, cancellationToken);
    }

    return response;
}
```

#### DI Registration

**Location:** `BackerAgent/DependencyInjection.cs`

```csharp
services
    .AddHttpClient<IHannibalServiceClient, HannibalServiceClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<HannibalServiceClientOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
    })
    .AddHttpMessageHandler(sp =>
    {
        return new AutoAuthHandler(
            sp,
            sp.GetRequiredService<IStaticTokenProvider>(),
            new HttpClient(),
            async (serviceProvider, cancellationToken) =>
            {
                // Obtain token using stored credentials
                var apiOptions = new RCloneServiceOptions();
                configuration.GetSection("RCloneService").Bind(apiOptions);
                
                var identityApiService = /* ... */;
                var loginRes = await identityApiService.TokenAsync(new()
                {
                    Email = apiOptions.BackerUsername,
                    Password = apiOptions.BackerPassword
                }, cancellationToken);

                return loginRes.Result is Ok<AccessTokenResponse> okResult
                    ? okResult.Value!.AccessToken
                    : "";
            });
    });
```

#### Configuration

**Location:** BackerAgent's `appsettings.json` or user secrets

```json
{
  "RCloneService": {
    "BackerUsername": "user@example.com",
    "BackerPassword": "password123"
  },
  "HannibalServiceClient": {
    "BaseUrl": "http://localhost:5288"
  }
}
```

---

### 3. Frontend (Poe/Blazor) - User-Initiated Authentication

The frontend uses ASP.NET Identity for cookie-based authentication, then extracts tokens for API calls.

#### Key Components

| Component | Purpose |
|-----------|---------|
| Cookie Authentication | User logs in via UI, session maintained via cookie |
| `HttpContextTokenProvider` | Extracts JWT from authenticated user's claims |
| `AddTokenHandler` | Adds token to outgoing HTTP requests |

#### Authentication Flow

```
1. User visits login page
   ↓
2. Enters email/password
   ↓
3. ASP.NET Identity validates credentials
   ↓
4. Session cookie set in browser
   ↓
5. User makes API request via Blazor
   ↓
6. HttpContextTokenProvider extracts token from claims
   ↓
7. AddTokenHandler adds Bearer token to request
   ↓
8. API validates token → 200 OK
```

#### HttpContextTokenProvider

**Location:** `Tools/HttpContextTokenProvider.cs`

```csharp
public class HttpContextTokenProvider : ITokenProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextTokenProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<string?> GetToken()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user != null && user.Identity?.IsAuthenticated == true)
        {
            var tokenClaim = user.Claims.FirstOrDefault(c => c.Type == "access_token");
            return tokenClaim?.Value;
        }
        return null;
    }
}
```

#### AddTokenHandler

**Location:** `Tools/AddTokenHandler.cs`

```csharp
public class AddTokenHandler : DelegatingHandler
{
    private readonly ITokenProvider _tokenProvider;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetToken();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
```

---

## Token Providers

The system uses different token providers depending on context:

| Provider | Used By | Storage | Purpose |
|----------|---------|---------|---------|
| `ConstantTokenProvider` | BackerAgent | In-memory | Stores token obtained via AutoAuthHandler |
| `HttpContextTokenProvider` | Frontend | HTTP Context Claims | Extracts token from authenticated user |

### ITokenProvider Interface

```csharp
public interface ITokenProvider
{
    Task<string?> GetToken();
}
```

### IStaticTokenProvider Interface

```csharp
public interface IStaticTokenProvider : ITokenProvider
{
    void SetToken(string token);
}
```

---

## JWT Token Structure

### Claims

| Claim | Description |
|-------|-------------|
| `sub` | User ID (GUID) |
| `email` | User's email address |
| `jti` | Unique token identifier |
| `name` | Username |
| `role` | User roles (if any) |
| `exp` | Expiration timestamp |
| `iss` | Token issuer |
| `aud` | Token audience |

### Example Decoded Token

```json
{
  "sub": "88d28f79-7ca1-4c6c-ab02-a74723e974a4",
  "email": "user@example.com",
  "jti": "ece64956-a247-45a8-adcf-04903b16748d",
  "name": "user@example.com",
  "exp": 1768838647,
  "iss": "your-app-name",
  "aud": "your-app-client"
}
```

---

## Security Considerations

### Current Limitations

1. **Lifetime Validation Disabled**
   ```csharp
   ValidateLifetime = false  // TODO: Enable in production
   ```
   Tokens never expire. Should be enabled for production.

2. **No Refresh Tokens**
   Current implementation doesn't use refresh tokens. When a token expires (if lifetime validation enabled), user must re-authenticate.

3. **Credentials in Config**
   BackerAgent stores credentials in appsettings. Consider using:
   - User secrets (development)
   - Environment variables (production)
   - Azure Key Vault or similar (cloud)

### Recommendations

1. **Enable Token Expiration**
   ```csharp
   ValidateLifetime = true
   ```

2. **Implement Refresh Tokens**
   - Store refresh token alongside access token
   - Auto-refresh before expiration
   - Revoke refresh tokens on logout

3. **Secure Credential Storage**
   - Never commit credentials to source control
   - Use secrets management in production

4. **HTTPS Only**
   - Always use HTTPS in production
   - Tokens are sent in headers and can be intercepted

---

## Troubleshooting

### Common Issues

#### 1. "401 Unauthorized" After Token Obtained

**Symptom:** Token endpoint returns 200, but subsequent requests still fail with 401.

**Causes:**
- Token not added to retry request (Bug fixed in AutoAuthHandler)
- Issuer/Audience mismatch between token creation and validation
- Different JWT signing keys

**Solution:** Verify `Jwt:Key`, `Jwt:Issuer`, and `Jwt:Audience` match in all appsettings.

#### 2. "Token validation failed"

**Symptom:** API logs show `❌ Authentication failed: ...`

**Causes:**
- Expired token (if lifetime validation enabled)
- Invalid signature (wrong key)
- Clock skew between servers

**Solution:** Check API logs for specific validation error.

#### 3. Infinite 401 Loop

**Symptom:** Agent keeps retrying authentication forever.

**Causes:**
- Invalid credentials
- Token endpoint returning 401
- Wrong token endpoint URL

**Solution:** 
- Verify credentials are correct
- Check token endpoint is accessible
- Review retry logic limits (now max 10 retries)

---

## Files Reference

| File | Component | Purpose |
|------|-----------|---------|
| `Tools/TokenService.cs` | API | Creates JWT tokens |
| `Tools/AutoAuthHandler.cs` | BackerAgent | Auto-refresh on 401 |
| `Tools/ConstantTokenProvider.cs` | BackerAgent | In-memory token storage |
| `Tools/HttpContextTokenProvider.cs` | Frontend | Extract token from claims |
| `Tools/AddTokenHandler.cs` | Frontend | Add token to requests |
| `Tools/ITokenProvider.cs` | Shared | Token provider interface |
| `Tools/IStaticTokenProvider.cs` | Shared | Settable token provider interface |
| `Api/Program.cs` | API | JWT validation config |
| `BackerAgent/DependencyInjection.cs` | BackerAgent | HTTP client setup |

---

## Future Enhancements

1. **OAuth2 for External Services**
   - Already partially implemented for Dropbox/OneDrive
   - Separate from user authentication

2. **API Keys for Service-to-Service**
   - Alternative to JWT for background services
   - Simpler, no expiration concerns

3. **Multi-Factor Authentication**
   - Add TOTP support for frontend login
   - Integrate with ASP.NET Identity MFA

4. **Token Revocation**
   - Maintain blacklist of revoked tokens
   - Check on each request (performance consideration)
