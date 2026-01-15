using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace func_mrodriguez_portfolio;

public sealed class GetCv
{
    private readonly ILogger<GetCv> _logger;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public GetCv(
        ILogger<GetCv> logger,
        BlobServiceClient blobServiceClient,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _logger = logger;
        _blobServiceClient = blobServiceClient;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    private sealed record TurnstileRequest(string Token);

    [Function("GetCv")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cv")] HttpRequestData req)
    {
        _logger.LogInformation("GetCv triggered.");

        try
        {
            // 1) Parse Turnstile token
            var token = await ReadTurnstileToken(req);
            if (token is null)
                return CreateText(req, HttpStatusCode.BadRequest, "Missing or invalid Turnstile token.");

            // 2) Verify Turnstile
            if (!await VerifyTurnstileAsync(token))
                return CreateText(req, HttpStatusCode.Forbidden, "Captcha verification failed.");

            // 3) Read configuration
            var containerName = _config["CV_CONTAINER"];
            var blobName = _config["CV_BLOB_NAME"];

            if (string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(blobName))
            {
                _logger.LogError("CV_CONTAINER or CV_BLOB_NAME not configured.");
                return CreateText(req, HttpStatusCode.InternalServerError, "Server misconfiguration.");
            }

            // 4) Fetch blob
            var container = _blobServiceClient.GetBlobContainerClient(containerName);
            var blob = container.GetBlobClient(blobName);

            if (!await blob.ExistsAsync())
            {
                _logger.LogWarning("CV blob not found: {Container}/{Blob}", containerName, blobName);
                return CreateText(req, HttpStatusCode.NotFound, "CV not found.");
            }

            // 5) Stream PDF
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/pdf");
            response.Headers.Add("Content-Disposition", $"inline; filename=\"{blobName}\"");
            response.Headers.Add("Cache-Control", "private, max-age=0, no-cache");

            await blob.DownloadToAsync(response.Body);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetCv failed.");
            return CreateText(req, HttpStatusCode.InternalServerError, "Internal server error.");
        }
    }

    // ---------- Helpers ----------

    private async Task<string?> ReadTurnstileToken(HttpRequestData req)
    {
        var body = await new StreamReader(req.Body, Encoding.UTF8).ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            return
                root.TryGetProperty("token", out var t1) ? t1.GetString() :
                root.TryGetProperty("Token", out var t2) ? t2.GetString() :
                null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> VerifyTurnstileAsync(string token)
    {
        var secret = _config["TURNSTILE_SECRET"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogError("TURNSTILE_SECRET not configured.");
            return false;
        }

        var http = _httpClientFactory.CreateClient("turnstile");

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("secret", secret),
            new KeyValuePair<string, string>("response", token)
        });

        using var response = await http.PostAsync(
            "https://challenges.cloudflare.com/turnstile/v0/siteverify",
            form);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Turnstile HTTP failure: {Status}", response.StatusCode);
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
        }
        catch (JsonException)
        {
            _logger.LogWarning("Invalid Turnstile JSON response.");
            return false;
        }
    }

    private static HttpResponseData CreateText(HttpRequestData req, HttpStatusCode status, string message)
    {
        var res = req.CreateResponse(status);
        res.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        res.WriteString(message);
        return res;
    }
}
