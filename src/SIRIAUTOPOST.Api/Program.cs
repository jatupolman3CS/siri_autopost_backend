using SIRIAUTOPOST.Api.Extensions;

LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApi(builder.Configuration);

var app = builder.Build();

await app.PrepareDatabaseAsync();
app.UseApi();

app.Run();

// KEY=value lines of .env (current folder, then parents) become env vars when running outside Docker. Variables already set win.
static void LoadDotEnv()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        var file = Path.Combine(dir.FullName, ".env");
        if (!File.Exists(file)) continue;
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (line.Length == 0 || line.StartsWith('#') || eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            if (value.Length > 0)
            {
                if (Environment.GetEnvironmentVariable(key) is null)
                    Environment.SetEnvironmentVariable(key, value);

                if (key.Equals("STRIPE_SECRET_KEY", StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("Stripe__SecretKey") is null)
                    Environment.SetEnvironmentVariable("Stripe__SecretKey", value);
                else if (key.Equals("STRIPE_PUBLISHABLE_KEY", StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("Stripe__PublishableKey") is null)
                    Environment.SetEnvironmentVariable("Stripe__PublishableKey", value);
                else if (key.Equals("STRIPE_WEBHOOK_SECRET", StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("Stripe__WebhookSecret") is null)
                    Environment.SetEnvironmentVariable("Stripe__WebhookSecret", value);
                else if (key.Equals("STRIPE_RETURN_BASE_URL", StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("Stripe__ReturnBaseUrl") is null)
                    Environment.SetEnvironmentVariable("Stripe__ReturnBaseUrl", value);
                else if (key.Equals("STRIPE_CURRENCY", StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("Stripe__Currency") is null)
                    Environment.SetEnvironmentVariable("Stripe__Currency", value);
                else if (key.Equals("STRIPE_PORTAL_CONFIGURATION_ID", StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("Stripe__PortalConfigurationId") is null)
                    Environment.SetEnvironmentVariable("Stripe__PortalConfigurationId", value);
            }
        }
        return;
    }
}

// Lets SIRIAUTOPOST.Api.IntegrationTests start the app with WebApplicationFactory<Program>.
public partial class Program;
