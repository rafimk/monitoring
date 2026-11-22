using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Monitoring.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController(IOptions<ApiOptions> options) : ControllerBase
{
    /// <summary>Development-only token minting. Returns 404 unless Api:EnableDevTokens is on (Development environment only).</summary>
    [HttpPost("dev-token")]
    public IActionResult DevToken(DevTokenRequest request)
    {
        var o = options.Value;
        if (!o.EnableDevTokens) return NotFound();
        if (!Roles.All.Contains(request.Role))
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
                { ["role"] = [$"Role must be one of: {string.Join(", ", Roles.All)}"] }));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.JwtKey));
        var expires = DateTime.UtcNow.AddHours(8);
        var jwt = new JwtSecurityToken(o.JwtIssuer, o.JwtAudience,
            [new Claim("name", request.Username), new Claim("role", request.Role)],
            expires: expires, signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return Ok(new { token = new JwtSecurityTokenHandler().WriteToken(jwt), expiresUtc = expires, role = request.Role });
    }
}
