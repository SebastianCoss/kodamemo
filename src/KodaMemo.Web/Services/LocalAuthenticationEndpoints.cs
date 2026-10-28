using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace KodaMemo.Web.Services;

/// <summary>Maps single-owner sign-in endpoints for the local web companion.</summary>
internal static class LocalAuthenticationEndpoints
{
    /// <summary>Registers form-based authentication with antiforgery validation.</summary>
    /// <param name="app">The application receiving the endpoints.</param>
    public static void MapLocalAuthentication(this WebApplication app)
    {
        app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Content(BuildForm(context, antiforgery, false), "text/html")).AllowAnonymous();

        app.MapPost("/login", async (HttpContext context, IAntiforgery antiforgery,
            WebMeetingStoreService store) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!store.ValidateWebLogin(form["username"], form["password"]))
                return Results.Content(BuildForm(context, antiforgery, true), "text/html", statusCode: 401);

            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, store.ExpectedWebUsername) },
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));
            return Results.Redirect("/");
        }).AllowAnonymous().RequireRateLimiting("login");

        app.MapGet("/logout", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Content(BuildForm(context, antiforgery, false, logout: true), "text/html"))
            .RequireAuthorization();

        app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).RequireAuthorization();
    }

    private static string BuildForm(HttpContext context, IAntiforgery antiforgery,
        bool invalid, bool logout = false)
    {
        var token = antiforgery.GetAndStoreTokens(context);
        string field = HtmlEncoder.Default.Encode(token.FormFieldName);
        string value = HtmlEncoder.Default.Encode(token.RequestToken ?? string.Empty);
        string content = logout
            ? "<h1>Cerrar sesión</h1><p>Tus reuniones seguirán guardadas.</p>"
            : """
              <h1>Tu espacio para escuchar.</h1>
              <p>Inicia sesión para consultar tus reuniones.</p>
              <label for="username">Usuario</label>
              <input id="username" name="username" autocomplete="username" required>
              <label for="password">Contraseña</label>
              <input id="password" name="password" type="password" autocomplete="current-password" required>
              """;
        string error = invalid ? "<p role='alert'>Usuario o contraseña incorrectos.</p>" : "";
        string action = logout ? "/logout" : "/login";
        string button = logout ? "Cerrar sesión" : "Entrar";
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>KodaMemo · Acceso</title>
            <style>
            *{box-sizing:border-box}body{margin:0;padding:24px;background:#f2f6f8;color:#172e3a;font:16px/1.5 system-ui}
            main{max-width:440px;margin:8vh auto;padding:32px;background:white;border:1px solid #dce5ea;border-radius:24px;box-shadow:0 12px 32px #0f4c5c12}
            strong{color:#0f4c5c}h1{font-size:28px;line-height:1.2}label{display:block;margin-top:18px}input{width:100%;padding:12px;border:1px solid #94a8b3;border-radius:10px;font:inherit}
            button{width:100%;padding:14px;margin-top:24px;border:0;border-radius:14px;background:#0f4c5c;color:white;font:inherit;cursor:pointer}
            :focus-visible{outline:3px solid #f25f4c;outline-offset:3px}[role=alert]{color:#9a3024}
            </style></head><body><main><strong>KodaMemo</strong>
            <form method="post" action="{{action}}">
            <input type="hidden" name="{{field}}" value="{{value}}">
            {{content}}{{error}}<button>{{button}}</button></form></main></body></html>
            """;
    }
}
