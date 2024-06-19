using Duplicati.Library.Logging;
using Duplicati.Server.Database;
using Duplicati.WebserverCore.Abstractions;
using Duplicati.WebserverCore.Exceptions;
using Duplicati.WebserverCore.Middlewares;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace Duplicati.WebserverCore.Endpoints.V1;

public partial class Auth : IEndpointV1
{
    private static readonly string LOGTAG = Log.LogTagFromType<Auth>();

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("auth/refresh", async ([FromServices] ILoginProvider loginProvider, [FromServices] JWTConfig jWTConfig, [FromServices] IHttpContextAccessor httpContextAccessor, CancellationToken ct) =>
        {
            if (httpContextAccessor.HttpContext!.Request.Cookies.TryGetValue("RefreshToken", out var refreshTokenString))
            {
                try
                {
                    var result = await loginProvider.PerformLoginWithRefreshToken(refreshTokenString, ct);
                    AddCookie(httpContextAccessor.HttpContext, "RefreshToken", result.RefreshToken, DateTimeOffset.UtcNow.AddMinutes(jWTConfig.RefreshTokenDurationInMinutes));
                    return new Dto.AccessTokenOutput(result.AccessToken);
                }
                catch (Exception ex)
                {
                    Log.WriteErrorMessage(LOGTAG, "RefreshTokenError", ex, "Failed to refresh token");
                    if (ex is UserReportedHttpException)
                        throw;
                    throw new UnauthorizedException("Failed to refresh token");
                }
            }

            throw new UnauthorizedException("Failed to refresh token");
        });

        group.MapPost("auth/signin", async ([FromServices] ILoginProvider loginProvider, [FromServices] JWTConfig jWTConfig, [FromServices] IHttpContextAccessor httpContextAccessor, [FromBody] Dto.SigninInputDto input, CancellationToken ct) =>
        {
            try
            {
                var result = await loginProvider.PerformLoginWithSigninToken(input.SigninToken, input.RememberMe ?? false, ct);
                if (!string.IsNullOrWhiteSpace(result.RefreshToken))
                    AddCookie(httpContextAccessor.HttpContext!, "RefreshToken", result.RefreshToken, DateTimeOffset.UtcNow.AddMinutes(jWTConfig.RefreshTokenDurationInMinutes));
                return new Dto.AccessTokenOutput(result.AccessToken);
            }
            catch (Exception ex)
            {
                Log.WriteErrorMessage(LOGTAG, "SigninTokenError", ex, "Failed to sign in");
                if (ex is UserReportedHttpException)
                    throw;
                if (ex is SecurityTokenExpiredException)
                    throw new UnauthorizedException("Signin token expired");

                throw new UnauthorizedException("Failed to sign in");
            }
        });

        group.MapPost("auth/login", async ([FromServices] ILoginProvider loginProvider, [FromServices] JWTConfig jWTConfig, [FromServices] IHttpContextAccessor httpContextAccessor, [FromBody] Dto.LoginInputDto input, CancellationToken ct) =>
        {
            try
            {
                var result = await loginProvider.PerformLoginWithPassword(input.Password, input.RememberMe ?? false, ct);
                if (!string.IsNullOrWhiteSpace(result.RefreshToken))
                    AddCookie(httpContextAccessor.HttpContext!, "RefreshToken", result.RefreshToken, DateTimeOffset.UtcNow.AddMinutes(jWTConfig.RefreshTokenDurationInMinutes));
                return new Dto.AccessTokenOutput(result.AccessToken);
            }
            catch (Exception ex)
            {
                Log.WriteErrorMessage(LOGTAG, "PasswordLoginError", ex, "Failed to log in");
                if (ex is UserReportedHttpException)
                    throw;
                throw new UnauthorizedException("Failed to log in");
            }
        });

        group.MapPost("auth/issuesignintoken", ([FromServices] Connection connection, [FromServices] IJWTTokenProvider tokenProvider, [FromBody] Dto.IssueSigninTokenInputDto input) =>
        {
            if (!connection.ApplicationSettings.VerifyWebserverPassword(input.Password))
                throw new UnauthorizedException("Incorrect password");

            var signinToken = tokenProvider.CreateSigninToken("web-api");
            return new Dto.SigninTokenOutputDto(signinToken);
        });

        group.MapPost("auth/logout", ([FromServices] ILoginProvider loginProvider, [FromServices] IHttpContextAccessor httpContextAccessor) =>
        {
            if (httpContextAccessor.HttpContext!.Request.Cookies.TryGetValue("RefreshToken", out var refreshTokenString))
            {
                try
                {
                    loginProvider.PerformLogoutWithRefreshToken(refreshTokenString, CancellationToken.None);
                }
                catch
                {
                    // Ignore invalid refresh tokens
                }
            }

            httpContextAccessor.HttpContext!.Response.Cookies.Delete("RefreshToken");
            return new { success = true };
        });
    }

    private static void AddCookie(HttpContext context, string name, string value, DateTimeOffset expires)
        => context.Response.Cookies.Append(name, value, new CookieOptions
        {
            Expires = expires,
            Path = "/api/v1/auth/refresh",
            Secure = context.Request.IsHttps,
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Domain = context.Request.Host.Host
        });



}
