namespace Api.IntegrationTests.UserAccount;

/// <summary>
/// REQ-003: Remember Me
/// All ACs are UI-driven. Cookie persistence is controlled by Login.razor:83-86 via
/// AuthenticationProperties.IsPersistent/ExpiresUtc, not via the API.
/// These tests require bUnit or Playwright for implementation.
/// </summary>
public class RememberMeTests
{
    #region AC1 - Remember Me checkbox

    [Fact(Skip = "UI test - requires Blazor/browser testing. Verify 'Remember me' checkbox exists on /login page.")]
    [Trait("Category", "UI")]
    public void LoginPage_HasRememberMeCheckbox() { }

    #endregion

    #region AC2 - Remember Me checked sets 30-day cookie

    [Fact(Skip = "UI test - requires Blazor/browser testing. When Remember Me is checked, cookie should have IsPersistent=true with 30-day expiry (Login.razor:83-86).")]
    [Trait("Category", "UI")]
    public void Login_RememberMeChecked_CookieExpires30Days() { }

    #endregion

    #region AC3 - Remember Me unchecked sets session cookie

    [Fact(Skip = "UI test - requires Blazor/browser testing. When Remember Me is unchecked, cookie should be a session cookie with no Expires (Login.razor:83-86).")]
    [Trait("Category", "UI")]
    public void Login_RememberMeUnchecked_SessionCookie() { }

    #endregion
}
