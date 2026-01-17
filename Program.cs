using Azure.Core.Serialization;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// ✅ Ensure JSON output uses camelCase (matches your frontend types.ts)
builder.Services.Configure<WorkerOptions>(options =>
{
    options.Serializer = new JsonObjectSerializer(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Optional but often useful:
        // PropertyNameCaseInsensitive = true
    });
});

// Add configuration sources (local.settings.json is loaded by Functions runtime for local dev)
builder.Services.AddOptions();

// HttpClient via factory (best practice; avoids socket exhaustion)
builder.Services.AddHttpClient("turnstile", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

// BlobServiceClient via connection string from env/config
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var conn = config["BlobConnectionString"];

    if (string.IsNullOrWhiteSpace(conn))
        throw new InvalidOperationException("BlobConnectionString is not configured.");

    return new BlobServiceClient(conn);
});

builder.Build().Run();
