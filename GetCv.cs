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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
    private sealed record TurnstileVerifyResponse(bool Success, string[]? ErrorCodes);

    [Function("GetCv")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cv")] HttpRequestData req)
    {
        try
        {
            // 1) Parse body: { "token": "..." }
            var body = await new StreamReader(req.Body, Encoding.UTF8).ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
                return CreateText(req, HttpStatusCode.BadRequest, "Missing request body.");

            TurnstileRequest? turnstileRequest;
            try
            {
                // Accept both { token: "x" } and { Token: "x" }
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var token =
                    root.TryGetProperty("token", out var t1) ? t1.GetString() :
                    root.TryGetProperty("Token", out var t2) ? t2.GetString() :
                    null;

                if (string.IsNullOrWhiteSpace(token))
                    return CreateText(req, HttpStatusCode.BadRequest, "Missing Turnstile token.");

                turnstileRequest = new TurnstileRequest(token);
            }
            catch (JsonException)
            {
                return CreateText(req, HttpStatusCode.BadRequest, "Invalid JSON body.");
            }

            // 2) Verify Turnstile
            var secret = _config["TURNSTILE_SECRET"];
            if (string.IsNullOrWhiteSpace(secret))
            {
                _logger.LogError("TURNSTILE_SECRET is not configured.");
                return CreateText(req, HttpStatusCode.InternalServerError, "Server misconfiguration.");
            }

            var http = _httpClientFactory.CreateClient("turnstile");

            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("secret", secret),
                new KeyValuePair<string,string>("response", turnstileRequest.Token),
            });

            using var verify = await http.PostAsync(
                "https://challenges.cloudflare.com/turnstile/v0/siteverify",
                form);

            if (!verify.IsSuccessStatusCode)
            {
                _logger.LogWarning("Turnstile verify HTTP {StatusCode}.", verify.StatusCode);
                return CreateText(req, HttpStatusCode.BadGateway, "Captcha verification unavailable.");
            }

            var verifyJson = await verify.Content.ReadAsStringAsync();
            TurnstileVerifyResponse? verifyObj;

            try
            {
                // Cloudflare uses snake_case; JsonDefaults.Web + these property names handle typical cases,
                // but we’ll also handle both keys safely if needed.
                using var doc = JsonDocument.Parse(verifyJson);
                var root = doc.RootElement;

                var success = root.TryGetProperty("success", out var s) && s.GetBoolean();

                string[]? errorCodes = null;
                if (root.TryGetProperty("error-codes", out var e1) && e1.ValueKind == JsonValueKind.Array)
                    errorCodes = e1.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
                else if (root.TryGetProperty("error_codes", out var e2) && e2.ValueKind == JsonValueKind.Array)
                    errorCodes = e2.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();

                verifyObj = new TurnstileVerifyResponse(success, errorCodes);
            }
            catch (JsonException)
            {
                _logger.LogWarning("Turnstile verify returned invalid JSON.");
                return CreateText(req, HttpStatusCode.BadGateway, "Captcha verification failed.");
            }

            if (verifyObj is null || !verifyObj.Success)
            {
                _logger.LogInformation("Turnstile failed. Codes: {Codes}", verifyObj?.ErrorCodes is null
                    ? "(none)"
                    : string.Join(",", verifyObj.ErrorCodes));
                return CreateText(req, HttpStatusCode.Forbidden, "Captcha verification failed.");
            }

            // 3) Stream CV from Blob
            var containerName = _config["CV_CONTAINER"] ?? "resume";
            var blobName = _config["CV_BLOB_NAME"] ?? "[CV]Mariano-Rodriguez.pdf";

            var container = _blobServiceClient.GetBlobContainerClient(containerName);
            var blob = container.GetBlobClient(blobName);

            if (!await blob.ExistsAsync())
            {
                _logger.LogWarning("CV blob not found: {Container}/{Blob}", containerName, blobName);
                return CreateText(req, HttpStatusCode.NotFound, "CV not found.");
            }

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

    private static HttpResponseData CreateText(HttpRequestData req, HttpStatusCode status, string message)
    {
        var res = req.CreateResponse(status);
        res.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        res.WriteString(message);
        return res;
    }
}
