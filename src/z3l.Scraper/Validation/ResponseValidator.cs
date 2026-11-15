namespace z3l.Scraper.Validation;

internal static class ResponseValidator
{
    public static ValidationResult Validate(ScrapeRequest request, PageResponse response, EffectiveRules rules)
    {
        var status = response.StatusCode;

        if (rules.BlockedStatusCodes.Contains(status)) return ValidationResult.Blocked($"status {status}");

        if (rules.NotFoundOnStatus404 && status is 404 or 410) return ValidationResult.NotFound($"status {status}");

        if (rules.RequireSuccessStatus && !response.IsSuccessStatusCode) return ValidationResult.Retry($"status {status}");

        if (response.Body.Length == 0)
        {
            if (!rules.AllowEmptyBody) return ValidationResult.Retry("empty body");
        }
        else if (rules.Content != ExpectedContent.Binary)
        {
            var text = response.Text;

            if (FindAny(text, rules.NotFoundMarkers) is { } notFound) return ValidationResult.NotFound($"marker \"{notFound}\"");
            if (FindAny(text, rules.BlockedMarkers) is { } blocked) return ValidationResult.Blocked($"marker \"{blocked}\"");

            switch (rules.Content)
            {
                case ExpectedContent.Html when !text.Contains("</html>", StringComparison.OrdinalIgnoreCase):
                    return ValidationResult.Retry("incomplete html: no </html>");
                case ExpectedContent.Json when !LooksLikeJson(text):
                    return ValidationResult.Retry("not json");
            }

            foreach (var part in rules.MustContain)
            {
                if (!text.Contains(part, StringComparison.OrdinalIgnoreCase)) return ValidationResult.Retry($"no \"{part}\"");
            }
            if (FindAny(text, rules.MustNotContain) is { } bad) return ValidationResult.Retry($"contains \"{bad}\"");
        }

        foreach (var custom in rules.Custom)
        {
            var result = custom(request, response);
            if (result.Outcome != ValidationOutcome.Ok) return result with { Reason = result.Reason ?? "custom check" };
        }

        return ValidationResult.Ok;
    }

    private static string? FindAny(string text, string[] parts)
    {
        foreach (var part in parts)
        {
            if (text.Contains(part, StringComparison.OrdinalIgnoreCase)) return part;
        }
        return null;
    }

    private static bool LooksLikeJson(string text)
    {
        var span = text.AsSpan().Trim();
        return span.Length >= 2 && ((span[0] == '{' && span[^1] == '}') || (span[0] == '[' && span[^1] == ']'));
    }
}
