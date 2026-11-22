using System.Text.RegularExpressions;

namespace Monitoring.Application;

/// <summary>Produces short, secret-free error text safe to persist and return from the API.</summary>
public static class ErrorSanitizer
{
    private const int MaxLength = 500;
    private static readonly Regex Secrets = new(
        @"(?i)(password|pwd|secret|token|apikey|api[-_]key|authorization)\s*[=:]\s*[^;\s,]+",
        RegexOptions.Compiled);

    public static string Sanitize(Exception ex) => Sanitize($"{ex.GetType().Name}: {ex.Message}");

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var cleaned = Secrets.Replace(text, m => m.Value.Split(new[] { '=', ':' }, 2)[0] + "=***");
        cleaned = cleaned.ReplaceLineEndings(" ");
        return cleaned.Length <= MaxLength ? cleaned : cleaned[..MaxLength] + "…";
    }
}
