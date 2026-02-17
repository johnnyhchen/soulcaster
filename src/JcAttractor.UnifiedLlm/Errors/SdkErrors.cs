using System.Net;

namespace JcAttractor.UnifiedLlm;

/// <summary>
/// Base exception for all Unified LLM SDK errors.
/// </summary>
public class SdkException : Exception
{
    public SdkException(string message) : base(message) { }
    public SdkException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when the SDK is misconfigured (missing keys, unknown provider, etc.).
/// </summary>
public class ConfigurationError : SdkException
{
    public ConfigurationError(string message) : base(message) { }
    public ConfigurationError(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when a provider returns an HTTP error.
/// </summary>
public class ProviderError : SdkException
{
    public HttpStatusCode StatusCode { get; }
    public bool Retryable { get; }
    public TimeSpan? RetryAfter { get; }
    public string? ProviderName { get; }
    public string? ResponseBody { get; }

    public ProviderError(
        string message,
        HttpStatusCode statusCode,
        bool retryable = false,
        TimeSpan? retryAfter = null,
        string? providerName = null,
        string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        Retryable = retryable;
        RetryAfter = retryAfter;
        ProviderName = providerName;
        ResponseBody = responseBody;
    }

    public ProviderError(
        string message,
        HttpStatusCode statusCode,
        Exception innerException,
        bool retryable = false,
        TimeSpan? retryAfter = null,
        string? providerName = null,
        string? responseBody = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Retryable = retryable;
        RetryAfter = retryAfter;
        ProviderName = providerName;
        ResponseBody = responseBody;
    }
}

/// <summary>
/// Thrown when authentication fails (HTTP 401 / 403).
/// </summary>
public class AuthenticationError : ProviderError
{
    public AuthenticationError(string message, HttpStatusCode statusCode, string? providerName = null, string? responseBody = null)
        : base(message, statusCode, retryable: false, providerName: providerName, responseBody: responseBody) { }
}

/// <summary>
/// Thrown when rate-limited (HTTP 429).
/// </summary>
public class RateLimitError : ProviderError
{
    public RateLimitError(string message, TimeSpan? retryAfter = null, string? providerName = null, string? responseBody = null)
        : base(message, HttpStatusCode.TooManyRequests, retryable: true, retryAfter: retryAfter, providerName: providerName, responseBody: responseBody) { }
}

/// <summary>
/// Thrown when the requested resource is not found (HTTP 404).
/// </summary>
public class NotFoundError : ProviderError
{
    public NotFoundError(string message, string? providerName = null, string? responseBody = null)
        : base(message, HttpStatusCode.NotFound, retryable: false, providerName: providerName, responseBody: responseBody) { }
}

/// <summary>
/// Thrown when content is blocked by the provider's safety filters.
/// </summary>
public class ContentFilterError : ProviderError
{
    public ContentFilterError(string message, HttpStatusCode statusCode = HttpStatusCode.BadRequest, string? providerName = null, string? responseBody = null)
        : base(message, statusCode, retryable: false, providerName: providerName, responseBody: responseBody) { }
}

/// <summary>
/// Thrown when GenerateObjectAsync fails to produce a valid JSON object.
/// </summary>
public class NoObjectGeneratedError : SdkException
{
    public string? RawOutput { get; }

    public NoObjectGeneratedError(string message, string? rawOutput = null)
        : base(message)
    {
        RawOutput = rawOutput;
    }
}
