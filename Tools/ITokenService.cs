using Microsoft.AspNetCore.Identity;

namespace Tools;

public interface ITokenService
{
    string CreateToken(IdentityUser user);
}