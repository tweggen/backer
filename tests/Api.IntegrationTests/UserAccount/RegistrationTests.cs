using System.Net;
using Api.IntegrationTests.Fixtures;
using FluentAssertions;

namespace Api.IntegrationTests.UserAccount;

/// <summary>
/// REQ-001: User Registration
/// Tests for user registration via POST /api/auth/v1/register
/// </summary>
public class RegistrationTests : IClassFixture<BackerWebApplicationFactory>
{
    private readonly HttpClient _client;

    public RegistrationTests(BackerWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    #region AC1 - Registration form accepts email and password

    [Fact(Skip = "UI test - requires Blazor/browser testing. Verify /signup page renders email and password fields.")]
    [Trait("Category", "UI")]
    public void SignupPage_HasEmailAndPasswordFields() { }

    [Fact]
    [Trait("Category", "API")]
    public async Task Register_EndpointAcceptsEmailAndPassword()
    {
        var response = await _client.RegisterUserAsync("reg_ac1@test.com", "Password1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region AC2 - Password validation rules

    [Fact]
    [Trait("Category", "API")]
    public async Task Register_PasswordTooShort_ReturnsValidationError()
    {
        var response = await _client.RegisterUserAsync("reg_short@test.com", "Pa1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task Register_PasswordNoDigit_ReturnsValidationError()
    {
        var response = await _client.RegisterUserAsync("reg_nodigit@test.com", "Password");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task Register_PasswordNoUppercase_ReturnsValidationError()
    {
        var response = await _client.RegisterUserAsync("reg_noup@test.com", "password1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task Register_PasswordNoLowercase_ReturnsValidationError()
    {
        var response = await _client.RegisterUserAsync("reg_nolow@test.com", "PASSWORD1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task Register_ValidComplexPassword_Succeeds()
    {
        var response = await _client.RegisterUserAsync("reg_complex@test.com", "MyP@ss1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region AC3 - Password hint

    [Fact(Skip = "UI test - requires Blazor/browser testing. Verify hint text 'At least 6 characters with uppercase, lowercase, and a number' is displayed on signup page.")]
    [Trait("Category", "UI")]
    public void SignupPage_DisplaysPasswordHint() { }

    #endregion

    #region AC4 - Successful registration returns 200

    [Fact]
    [Trait("Category", "API")]
    public async Task Register_Success_Returns200()
    {
        var response = await _client.RegisterUserAsync("reg_success@test.com", "Password1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region AC5 - Duplicate and invalid email handling

    [Fact]
    [Trait("Category", "API")]
    public async Task Register_DuplicateEmail_ReturnsValidationError()
    {
        await _client.RegisterUserAsync("reg_dup@test.com", "Password1");

        var response = await _client.RegisterUserAsync("reg_dup@test.com", "Password1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "API")]
    public async Task Register_EmptyEmail_ReturnsValidationError()
    {
        var response = await _client.RegisterUserAsync("", "Password1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region AC6 - Login link on signup page

    [Fact(Skip = "UI test - requires Blazor/browser testing. Verify link to /login exists on signup page.")]
    [Trait("Category", "UI")]
    public void SignupPage_HasLoginLink() { }

    #endregion
}
