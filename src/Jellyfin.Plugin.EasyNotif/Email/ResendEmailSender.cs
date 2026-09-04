using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Sends email through the Resend REST API (Synthese.md section 3). Uses a named
/// <see cref="IHttpClientFactory"/> client, the shared <see cref="SendRateLimiter"/>, an optional
/// <c>Idempotency-Key</c> and one retry on a transient failure. A provider or network failure is
/// returned as a <see cref="SendResult"/>, never thrown (R10). The API key is never logged (R15).
/// </summary>
public sealed class ResendEmailSender : IEmailSender
{
    /// <summary>The name of the <see cref="IHttpClientFactory"/> client this sender uses.</summary>
    public const string HttpClientName = "EasyNotif";

    private const string EmailsEndpoint = "https://api.resend.com/emails";
    private const string BatchEndpoint = "https://api.resend.com/emails/batch";
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private const char BatchIdSeparator = '\n';
    private static readonly JsonSerializerOptions PayloadOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigAccessor _config;
    private readonly SendRateLimiter _rateLimiter;
    private readonly ILogger<ResendEmailSender> _logger;
    private readonly IEasyNotifLog _easyNotifLog;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;

    /// <summary>Initializes a new instance of the <see cref="ResendEmailSender"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="rateLimiter">The shared send rate limiter.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="easyNotifLog">The plugin's dedicated log.</param>
    public ResendEmailSender(
        IHttpClientFactory httpClientFactory,
        IConfigAccessor config,
        SendRateLimiter rateLimiter,
        ILogger<ResendEmailSender> logger,
        IEasyNotifLog easyNotifLog)
        : this(httpClientFactory, config, rateLimiter, logger, easyNotifLog, Task.Delay)
    {
    }

    internal ResendEmailSender(
        IHttpClientFactory httpClientFactory,
        IConfigAccessor config,
        SendRateLimiter rateLimiter,
        ILogger<ResendEmailSender> logger,
        IEasyNotifLog easyNotifLog,
        Func<TimeSpan, CancellationToken, Task> retryDelay)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _easyNotifLog = easyNotifLog;
        _retryDelay = retryDelay;
    }

    /// <inheritdoc/>
    public async Task<SendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var cfg = _config.Get();
        if (!IsConfigured(cfg))
        {
            _logger.LogWarning(
                "[EasyNotif] Cannot send email: the Resend transport is not configured (API key or sender address missing).");
            _easyNotifLog.Error("email.failed", new Dictionary<string, object?>
            {
                ["subject"] = message.Subject,
                ["to"] = message.To,
                ["error"] = NotConfigured.Error
            });
            return NotConfigured;
        }

        var payload = BuildPayload(message, cfg);
        var stopwatch = Stopwatch.StartNew();
        var result = await PostWithRetryAsync(EmailsEndpoint, payload, message.IdempotencyKey, cfg.ResendApiKey!, ExtractId, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        if (result.Success)
        {
            _logger.LogInformation(
                "[EasyNotif] Sent \"{Subject}\" to {Recipient} ({ResendId}).",
                message.Subject,
                EmailMasker.Mask(message.To),
                result.ResendId);
            _easyNotifLog.Info("email.sent", new Dictionary<string, object?>
            {
                ["subject"] = message.Subject,
                ["to"] = message.To,
                ["resendId"] = result.ResendId,
                ["httpStatus"] = result.StatusCode,
                ["durationMs"] = stopwatch.ElapsedMilliseconds
            });
        }
        else
        {
            _logger.LogWarning(
                "[EasyNotif] Failed to send \"{Subject}\" to {Recipient}: {Status} {Error}",
                message.Subject,
                EmailMasker.Mask(message.To),
                result.StatusCode,
                result.Error);
            _easyNotifLog.Error("email.failed", new Dictionary<string, object?>
            {
                ["subject"] = message.Subject,
                ["to"] = message.To,
                ["httpStatus"] = result.StatusCode,
                ["error"] = result.Error,
                ["durationMs"] = stopwatch.ElapsedMilliseconds
            });
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SendResult>> SendBatchAsync(IReadOnlyList<EmailMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var cfg = _config.Get();
        if (!IsConfigured(cfg))
        {
            _logger.LogWarning(
                "[EasyNotif] Cannot send email batch: the Resend transport is not configured.");
            return [.. Enumerable.Repeat(NotConfigured, messages.Count)];
        }

        var array = messages.Select(m => BuildPayload(m, cfg)).ToArray();
        var single = await PostWithRetryAsync(BatchEndpoint, array, idempotencyKey: null, cfg.ResendApiKey!, ExtractBatchIds, cancellationToken)
            .ConfigureAwait(false);

        if (!single.Success)
        {
            return [.. Enumerable.Repeat(single with { ResendId = null }, messages.Count)];
        }

        var ids = single.ResendId?.Split(BatchIdSeparator) ?? [];
        return [.. messages.Select((_, i) => new SendResult(true, i < ids.Length ? ids[i] : null, single.StatusCode, null))];
    }

    private static readonly SendResult NotConfigured = new(false, null, 0, "Resend is not configured.");

    private static bool IsConfigured(PluginConfiguration cfg)
        => !string.IsNullOrWhiteSpace(cfg.ResendApiKey) && !string.IsNullOrWhiteSpace(cfg.FromEmail);

    private static Dictionary<string, object?> BuildPayload(EmailMessage message, PluginConfiguration cfg)
    {
        var from = string.IsNullOrWhiteSpace(cfg.FromName)
            ? cfg.FromEmail!
            : $"{cfg.FromName} <{cfg.FromEmail}>";

        var text = message.Text ?? (message.Html is not null ? HtmlToText.Convert(message.Html) : null);
        var replyTo = message.ReplyTo ?? cfg.ReplyTo;

        var payload = new Dictionary<string, object?>
        {
            ["from"] = from,
            ["to"] = new[] { message.To },
            ["subject"] = message.Subject,
            ["html"] = message.Html,
            ["text"] = string.IsNullOrEmpty(text) ? null : text,
            ["reply_to"] = string.IsNullOrWhiteSpace(replyTo) ? null : replyTo
        };

        if (message.Headers is { Count: > 0 } headers)
        {
            payload["headers"] = headers;
        }

        if (message.Tags is { Count: > 0 } tags)
        {
            payload["tags"] = tags.Select(t => new Dictionary<string, string> { ["name"] = t.Name, ["value"] = t.Value }).ToArray();
        }

        if (message.Attachments is { Count: > 0 } attachments)
        {
            payload["attachments"] = attachments.Select(a =>
            {
                var entry = new Dictionary<string, object?>
                {
                    ["filename"] = a.FileName,
                    ["content"] = System.Convert.ToBase64String(a.Content)
                };
                if (a.ContentType is not null)
                {
                    entry["content_type"] = a.ContentType;
                }

                return entry;
            }).ToArray();
        }

        return payload;
    }

    private async Task<SendResult> PostWithRetryAsync(
        string url,
        object payload,
        string? idempotencyKey,
        string apiKey,
        Func<string, string?> idExtractor,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, PayloadOptions);

        for (var attempt = 0; ; attempt++)
        {
            await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            int status;
            string body;
            RetryConditionHeaderValue? retryAfter;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                }

                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                status = (int)response.StatusCode;
                retryAfter = response.Headers.RetryAfter;
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return new SendResult(true, idExtractor(body), status, null);
                }
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                return new SendResult(false, null, 0, ex.Message);
            }

            var transient = status == 429 || status >= 500;
            if (transient && attempt == 0)
            {
                await _retryDelay(RetryDelay(retryAfter), cancellationToken).ConfigureAwait(false);
                continue;
            }

            return new SendResult(false, null, status, ExtractError(body));
        }
    }

    private static TimeSpan RetryDelay(RetryConditionHeaderValue? retryAfter)
    {
        var delay = TimeSpan.FromSeconds(1);
        if (retryAfter?.Delta is { } delta)
        {
            delay = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }

        return delay < TimeSpan.Zero ? TimeSpan.Zero : (delay > MaxRetryDelay ? MaxRetryDelay : delay);
    }

    private static string? ExtractId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractBatchIds(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var ids = data.EnumerateArray()
                .Select(e => e.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty);
            return string.Join(BatchIdSeparator, ids);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
