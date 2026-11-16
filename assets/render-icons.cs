// Renders the package icons from their SVG sources: dotnet run assets/render-icons.cs
// Needs Chromium for Playwright (playwright.ps1 install chromium).
#:package Microsoft.Playwright@1.62.0
#:property PublishAot=false

using Microsoft.Playwright;

var dir = Path.GetDirectoryName(Path.GetFullPath(args.Length > 0 ? args[0] : "assets/icon.svg"))!;

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync();
var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 256, Height = 256 } });

foreach (var name in new[] { "icon", "icon-playwright" })
{
    var svg = await File.ReadAllTextAsync(Path.Combine(dir, name + ".svg"));
    await page.SetContentAsync($"<html><body style=\"margin:0;background:transparent\">{svg}</body></html>");
    await page.Locator("svg").ScreenshotAsync(new() { Path = Path.Combine(dir, name + ".png"), OmitBackground = true });
    Console.WriteLine($"{name}.png");
}
