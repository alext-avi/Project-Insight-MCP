using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>Anonymous OAuth form posts: urlencoded only (no multipart file buffering), small, and malformed bodies are a 400.</summary>
public static class FormInput
{
    public const int MaxBytes = 16 * 1024;

    public static bool IsUrlEncoded(HttpRequest request)
        => MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
           && type.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    public static void Limit(HttpRequest request)
    {
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxBytes;
    }

    /// <summary>The parsed form, or null when the body is oversized, over the field limit or malformed.</summary>
    public static async Task<IFormCollection?> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        Limit(request);
        try
        {
            return await request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return null;
        }
    }
}
