using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace BlazorSMTPForwarder.Web.Services;

public class CustomAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly LoginService _loginService;

    public CustomAuthenticationStateProvider(LoginService loginService)
    {
        _loginService = loginService;
        _loginService.OnChange += StateChanged;
    }

    private void StateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var identity = new ClaimsIdentity();

        // If no password is set, or user is logged in, they are authenticated
        if (!_loginService.CheckIfPasswordIsSet() || _loginService.IsLoggedIn)
        {
            identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "Admin")
            }, "Custom Authentication");
        }

        var user = new ClaimsPrincipal(identity);
        return Task.FromResult(new AuthenticationState(user));
    }

    public void Dispose()
    {
        _loginService.OnChange -= StateChanged;
    }
}
