namespace Poe.Services;

public class AuthState
{
    public void TriggerRedirectToLogin()
    {
        ShouldRedirectToLogin = true;
    }

    public bool ReadResetRedirectToLogin()
    {
        bool ret = ShouldRedirectToLogin;
        ShouldRedirectToLogin = false;
        return ret;
    }
    
    public bool ShouldRedirectToLogin { get; private set; }
}