using Microsoft.Extensions.Configuration;

namespace BlazorSMTPForwarder.Web.Services;

public class LoginService
{
    private readonly IConfiguration _config;
    public bool IsLoggedIn { get; private set; }
    
    public event Action? OnChange;

    public LoginService(IConfiguration config)
    {
        _config = config;
    }

    public bool Login(string password)
    {
        var appPassword = _config["AppPassword"];
        
        // If no password is set in config, we are always logged in (or login always succeeds)
        if (string.IsNullOrEmpty(appPassword))
        {
            IsLoggedIn = true;
            NotifyStateChanged();
            return true;
        }

        if (appPassword == password)
        {
            IsLoggedIn = true;
            NotifyStateChanged();
            return true;
        }

        return false;
    }

    public void Logout()
    {
        IsLoggedIn = false;
        NotifyStateChanged();
    }
    
    public bool CheckIfPasswordIsSet()
    {
        return !string.IsNullOrEmpty(_config["AppPassword"]);
    }

    private void NotifyStateChanged() => OnChange?.Invoke();
}
