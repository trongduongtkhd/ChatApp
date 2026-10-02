var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "ChatApp.IdentityService is running");

app.Run();
