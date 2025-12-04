using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

public static class GetCv
{
    private static readonly HttpClient HttpClient = new HttpClient();


    private class TurnstileRequest
    {
        public string token { get; set; }
    }


    private class TurnstileVerifyResponse
    {
        public bool success { get; set; }
        public string[] error_codes { get; set; }
    }


    [Function("GetCv")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cv")] HttpRequest req,
        ILogger log)
    {
        try
        {
            // 1. Read JSON body { token: "..." }
            string body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonSerializer.Deserialize<TurnstileRequest>(body);

            if (request == null || string.IsNullOrWhiteSpace(request.token))
            {
                return new BadRequestObjectResult("Missing Turnstile token.");
            }

            // 2. Verify with Cloudflare Turnstile
            string secret = Environment.GetEnvironmentVariable("TURNSTILE_SECRET");
            if (string.IsNullOrEmpty(secret))
            {
                log.LogError("TURNSTILE_SECRET is not configured.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }

            var formContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("secret", secret),
                new KeyValuePair<string,string>("response", request.token),
                // Optional: add remoteip if you want: new("remoteip", req.HttpContext.Connection.RemoteIpAddress?.ToString())
            });

            var verifyResponse = await HttpClient.PostAsync(
                "https://challenges.cloudflare.com/turnstile/v0/siteverify",
                formContent
            );

            if (!verifyResponse.IsSuccessStatusCode)
            {
                log.LogWarning("Turnstile verify returned HTTP {StatusCode}", verifyResponse.StatusCode);
                return new StatusCodeResult(StatusCodes.Status502BadGateway);
            }

            string verifyJson = await verifyResponse.Content.ReadAsStringAsync();
            var verifyObj = JsonSerializer.Deserialize<TurnstileVerifyResponse>(verifyJson);

            if (verifyObj == null || !verifyObj.success)
            {
                log.LogWarning("Turnstile verification failed: {Errors}", verifyObj?.error_codes);
                return new StatusCodeResult(StatusCodes.Status403Forbidden);
            }

            // 3. If captcha ok, read CV from Blob
            string blobConn = Environment.GetEnvironmentVariable("BlobConnectionString");
            if (string.IsNullOrEmpty(blobConn))
            {
                log.LogError("BlobConnectionString is not configured.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }

            string containerName = Environment.GetEnvironmentVariable("CV_CONTAINER") ?? "resume";
            string blobName = Environment.GetEnvironmentVariable("CV_BLOB_NAME") ?? "[CV]Mariano-Rodriguez.pdf";

            var blobService = new BlobServiceClient(blobConn);
            var container = blobService.GetBlobContainerClient(containerName);
            var blob = container.GetBlobClient(blobName);

            if (!await blob.ExistsAsync())
            {
                log.LogError("CV blob not found: {Container}/{Blob}", containerName, blobName);
                return new NotFoundResult();
            }

            var stream = await blob.OpenReadAsync();

            // 4. Stream back as PDF
            return new FileStreamResult(stream, "application/pdf")
            {
                FileDownloadName = blobName
            };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error in GetCv function.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}
