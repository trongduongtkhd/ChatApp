var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "ChatApp.NotificationService is running");

app.Run();
