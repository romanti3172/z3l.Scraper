namespace z3l.Scraper.Validation;

internal static class ResponseValidator
{
    /// <summary>
    /// Order matters: the markers are checked before the status, because block pages often come with 503 or 200
    /// (Cloudflare "Just a moment..." is a 503), and the loader must hear about them to change the proxy; and a site that says the
    /// page is not there with a 403 (S3: <c>AccessDenied</c> for a missing file) is not a block. A binary body (a file) is read for
    /// the markers only when it is text: the page of a proxy, or the error of the site instead of the file.
    /// </summary>
    public static ValidationResult Validate(ScrapeRequest request, PageResponse response, EffectiveRules rules)
    {
        var status = response.StatusCode;

        var hasBody = response.HasBody;
        var checkText = hasBody && rules.Content != ExpectedContent.Binary;
        var text = checkText ? response.TextMemory.Span : default;     // read in place: no string per page
        var markerText = checkText ? text
            : hasBody && (rules.BlockedMarkers.Length > 0 || rules.NotFoundMarkers.Length > 0) && FileContent.IsText(response.Body)
                ? response.TextMemory.Span : default;

        if (!markerText.IsEmpty)
        {
            if (FindAny(markerText, rules.BlockedMarkers) is { } blocked) return ValidationResult.Blocked($"marker \"{blocked}\"");
            // the site itself says the page is not there: sure, not retried (a bare 404 may be a mistake of the site or the proxy)
            if (FindAny(markerText, rules.NotFoundMarkers) is { } notFound) return ValidationResult.NotFound($"marker \"{notFound}\"");
        }

        if (rules.BlockedStatusCodes.Contains(status)) return ValidationResult.Blocked($"status {status}");

        if (rules.NotFoundOnStatus404 && status is 404 or 410) return ValidationResult.NotFoundStatus($"status {status}");

        if (rules.RequireSuccessStatus && !response.IsSuccessStatusCode) return ValidationResult.Retry($"status {status}");

        if (!hasBody)
        {
            if (!rules.AllowEmptyBody && status != 204) return ValidationResult.Retry("empty body");
        }
        else if (rules.Content == ExpectedContent.Binary)
        {
            if (rules.CheckFileContent && FileContent.Problem(response.Body) is { } problem) return ValidationResult.Retry(problem);
        }
        else if (checkText)
        {
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
            var result = custom(request, response, text);
            if (result.Outcome != ValidationOutcome.Ok) return result with { Reason = result.Reason ?? "custom check" };
        }

        return ValidationResult.Ok;
    }

    private static string? FindAny(ReadOnlySpan<char> text, string[] parts)
    {
        foreach (var part in parts)
        {
            if (text.Contains(part, StringComparison.OrdinalIgnoreCase)) return part;
        }
        return null;
    }

    private static bool LooksLikeJson(ReadOnlySpan<char> text)
    {
        var span = text.Trim();
        return span.Length >= 2 && ((span[0] == '{' && span[^1] == '}') || (span[0] == '[' && span[^1] == ']'));
    }
}
