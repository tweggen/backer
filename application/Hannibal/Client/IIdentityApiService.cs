using System.Net.Http.Json;
using System.Security.Claims;
using Hannibal.Client.Configuration;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.Extensions.Options;

namespace Hannibal.Client;

public interface IIdentityApiService
{
    Task<Results<Ok, ValidationProblem>> RegisterUserAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task<Results<Ok<AccessTokenResponse>, EmptyHttpResult, ProblemHttpResult>> LoginUserAsync(LoginRequest loginRequest, CancellationToken cancellationToken);
    Task<Results<Ok<AccessTokenResponse>, EmptyHttpResult, ProblemHttpResult>> TokenAsync(LoginRequest loginRequest, CancellationToken cancellationToken);
    Task<Results<Ok<AccessTokenResponse>, UnauthorizedHttpResult, SignInHttpResult, ChallengeHttpResult>> RefreshAsync(RefreshRequest refreshRequest, CancellationToken cancellationToken);
    Task<Results<ContentHttpResult, UnauthorizedHttpResult>> ConfirmEmail(string userId, string code, string? changedEmail, CancellationToken cancellationToken);
    Task<Ok> ResendConfirmationEmail(ResendConfirmationEmailRequest resendConfirmationEmailRequest, CancellationToken cancellationToken);
    Task<Results<Ok, ValidationProblem>> ForgotPasswordAsync(ForgotPasswordRequest forgotPasswordRequest, CancellationToken cancellationToken);
    Task<Results<Ok, ValidationProblem>> ResetPasswordAsync(ResetPasswordRequest resetPasswordRequest, CancellationToken cancellationToken);
    Task<Results<Ok<TwoFactorResponse>, ValidationProblem, NotFound>> TwoFactorAsync(ClaimsPrincipal claimsPrincipal, TwoFactorRequest twoFactorRequest, CancellationToken cancellationToken);
    Task<Results<Ok<InfoResponse>, ValidationProblem, NotFound>> GetInfo(ClaimsPrincipal claimsPrincipal, CancellationToken cancellationToken);
    Task<Results<Ok<InfoResponse>, ValidationProblem, NotFound>> SetInfo(ClaimsPrincipal claimsPrincipal, InfoRequest infoRequest, CancellationToken cancellationToken);
    Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken);
}