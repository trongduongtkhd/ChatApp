var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "ChatApp.GroupService is running");

app.Run();
