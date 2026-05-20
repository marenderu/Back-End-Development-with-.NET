using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();

var users = new List<User>();
var usersLock = new object();

bool IsValidEmail(string email)
{
    try
    {
        var _ = new MailAddress(email);
        return true;
    }
    catch
    {
        return false;
    }
}

// Simple token store for demonstration
var validTokens = new HashSet<string>(StringComparer.Ordinal) { "secrettoken1", "secrettoken2" };

// Error-handling middleware (must be first)
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled exception while processing request {Method} {Path}", context.Request.Method, context.Request.Path);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "Internal server error." });
        }
    }
});

// Authentication middleware (second)
app.Use(async (context, next) =>
{
    // Expect header: Authorization: Bearer <token>
    if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader) || StringValues.IsNullOrEmpty(authHeader))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
        return;
    }

    var header = authHeader.ToString();
    const string bearerPrefix = "Bearer ";
    if (!header.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
        return;
    }

    var token = header.Substring(bearerPrefix.Length).Trim();
    if (string.IsNullOrEmpty(token) || !validTokens.Contains(token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
        return;
    }

    // token is valid; proceed
    await next();
});

// Logging middleware (last)
app.Use(async (context, next) =>
{
    var sw = Stopwatch.StartNew();
    try
    {
        await next();
    }
    finally
    {
        sw.Stop();
        var method = context.Request.Method;
        var path = context.Request.Path + context.Request.QueryString;
        var status = context.Response.StatusCode;
        app.Logger.LogInformation("{Method} {Path} responded {Status} in {Elapsed}ms", method, path, status, sw.ElapsedMilliseconds);
    }
});

// GET: list all users
app.MapGet("/users", () =>
{
    List<User> snapshot;
    lock (usersLock)
    {
        snapshot = users.ToList();
    }

    return Results.Ok(snapshot);
});

// GET: get user by id
app.MapGet("/users/{id:guid}", (Guid id) =>
{
    lock (usersLock)
    {
        var user = users.FirstOrDefault(u => u.Id == id);
        return user is not null ? Results.Ok(user) : Results.NotFound(new { error = "User not found." });
    }
});

// POST: create new user
app.MapPost("/users", (CreateUserDto dto) =>
{
    if (dto is null) return Results.BadRequest(new { error = "Request body is required." });
    if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Email))
        return Results.BadRequest(new { error = "Name and Email are required." });

    if (!IsValidEmail(dto.Email))
        return Results.BadRequest(new { error = "Email is not a valid email address." });

    lock (usersLock)
    {
        if (users.Any(u => string.Equals(u.Email, dto.Email.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new { error = "A user with the same email already exists." });

        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            Email = dto.Email.Trim(),
            Age = dto.Age
        };

        users.Add(user);

        return Results.Created($"/users/{user.Id}", user);
    }
});

// PUT: update an existing user
app.MapPut("/users/{id:guid}", (Guid id, UpdateUserDto dto) =>
{
    if (dto is null) return Results.BadRequest(new { error = "Request body is required." });
    if (dto.Email is not null && !IsValidEmail(dto.Email))
        return Results.BadRequest(new { error = "Email is not a valid email address." });

    lock (usersLock)
    {
        var user = users.FirstOrDefault(u => u.Id == id);
        if (user is null) return Results.NotFound(new { error = "User not found." });

        if (!string.IsNullOrWhiteSpace(dto.Name)) user.Name = dto.Name.Trim();

        if (!string.IsNullOrWhiteSpace(dto.Email))
        {
            var newEmail = dto.Email.Trim();
            if (users.Any(u => u.Id != id && string.Equals(u.Email, newEmail, StringComparison.OrdinalIgnoreCase)))
                return Results.Conflict(new { error = "A user with the same email already exists." });

            user.Email = newEmail;
        }

        if (dto.Age.HasValue) user.Age = dto.Age.Value;

        return Results.Ok(user);
    }
});

// DELETE: remove user by id
app.MapDelete("/users/{id:guid}", (Guid id) =>
{
    lock (usersLock)
    {
        var user = users.FirstOrDefault(u => u.Id == id);
        if (user is null) return Results.NotFound(new { error = "User not found." });
        users.Remove(user);
        return Results.NoContent();
    }
});

app.Run();

// Simple models used by the endpoints
public class User
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string Email { get; set; } = default!;
    public int? Age { get; set; }
}

public class CreateUserDto
{
    public string Name { get; set; } = default!;
    public string Email { get; set; } = default!;
    public int? Age { get; set; }
}

public class UpdateUserDto
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    public int? Age { get; set; }
}