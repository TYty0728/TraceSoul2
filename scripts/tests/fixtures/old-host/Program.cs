var app = WebApplication.CreateBuilder(args).Build();
// Disposable upgrade fixture: model an older binary with the maintenance contract.
// Real maintenance/work exclusion is exercised against ReviewMaintenanceGate separately.
var expectedCookie = Environment.GetEnvironmentVariable("TEST_MAINTENANCE_COOKIE");
string lease = null;
app.MapPost("/maintenance", (HttpContext context) =>
{
    if (string.IsNullOrEmpty(expectedCookie) || context.Request.Headers.Cookie.ToString() != expectedCookie)
        return Results.Unauthorized();
    if (lease != null) return Results.Conflict();
    lease = Guid.NewGuid().ToString("N");
    File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("TRACESOUL2_HOME")!, "maintenance-acquired"), "reserved");
    return Results.Json(new { ready = true, lease });
});
app.MapDelete("/maintenance/{id}", (HttpContext context, string id) =>
{
    if (context.Request.Headers.Cookie.ToString() != expectedCookie) return Results.Unauthorized();
    if (id != lease) return Results.Conflict();
    lease = null;
    return Results.Json(new { released = true });
});
Console.WriteLine("TraceSoul2 Host  v0.0.1");
app.Run("http://0.0.0.0:5080");
