var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "ChatApp.Gateway is running");

app.Run();
