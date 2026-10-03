using SIRIAUTOPOST.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApi(builder.Configuration);

var app = builder.Build();

await app.MigrateDatabaseAsync();
app.UseApi();

app.Run();

// Lets SIRIAUTOPOST.Api.IntegrationTests start the app with WebApplicationFactory<Program>.
public partial class Program;
