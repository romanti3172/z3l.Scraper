using System.Collections.Concurrent;
using System.Text;

// A synthetic shop for the benchmarks. Everything lives under /s/{delayMs}/{listings}/:
//   ./                 root, links to every listing (class "catalog")
//   ./catalog/{n}      a listing with 20 product cards (article.product_pod h3 a -> ../item/{id})
//   ./item/{id}        a product page: h1, p.price_color, p.availability, a description and related products
// Each response waits {delayMs} first, standing in for network latency. Pages are rendered once and cached,
// so the site itself costs next to nothing. GET /_hits returns the number of shop pages served and resets it.
// Run: dotnet run -c Release -- [port]

var port = args.Length > 0 ? int.Parse(args[0]) : 5099;
const int ItemsPerListing = 20;

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();

var cache = new ConcurrentDictionary<string, byte[]>();
long hits = 0;

app.MapGet("/_hits", () => Interlocked.Exchange(ref hits, 0).ToString());

app.MapGet("/s/{delay:int}/{listings:int}/{**rest}", async (int delay, int listings, string? rest, HttpContext http) =>
{
    Interlocked.Increment(ref hits);
    if (delay > 0) await Task.Delay(delay);

    var key = $"{listings}/{rest}";
    if (!cache.TryGetValue(key, out var body))
    {
        var html = Render(listings, rest ?? "");
        if (html is null) return Results.NotFound();
        body = cache.GetOrAdd(key, Encoding.UTF8.GetBytes(html));
    }
    return Results.Bytes(body, "text/html; charset=utf-8");
});

Console.WriteLine($"BenchSite listening on http://127.0.0.1:{port}");
app.Run();

static string? Render(int listings, string rest)
{
    if (rest == "") return Root(listings);
    var parts = rest.Split('/');
    if (parts.Length == 2 && int.TryParse(parts[1], out var n))
    {
        if (parts[0] == "catalog" && n >= 1 && n <= listings) return Listing(n);
        if (parts[0] == "item" && n >= 1 && n <= listings * ItemsPerListing) return Item(n, listings);
    }
    return null;
}

static string Root(int listings)
{
    var sb = Begin("Catalogue");
    sb.Append("<h1>All books</h1><ul class=\"catalogs\">");
    for (var i = 1; i <= listings; i++) sb.Append($"<li><a class=\"catalog\" href=\"catalog/{i}\">Page {i}</a></li>");
    sb.Append("</ul>");
    return End(sb);
}

static string Listing(int n)
{
    var sb = Begin($"Page {n}");
    sb.Append($"<h1>Page {n}</h1><ol class=\"row\">");
    for (var i = 0; i < ItemsPerListing; i++)
    {
        var id = (n - 1) * ItemsPerListing + i + 1;
        sb.Append("<li class=\"col-xs-6 col-sm-4 col-md-3 col-lg-3\"><article class=\"product_pod\">")
          .Append($"<div class=\"image_container\"><a href=\"../item/{id}\"><img src=\"/media/cache/{id:x8}.jpg\" alt=\"Book {id}\" class=\"thumbnail\"></a></div>")
          .Append($"<p class=\"star-rating {Stars(id)}\"><i class=\"icon-star\"></i><i class=\"icon-star\"></i><i class=\"icon-star\"></i><i class=\"icon-star\"></i><i class=\"icon-star\"></i></p>")
          .Append($"<h3><a href=\"../item/{id}\" title=\"Book number {id}\">Book number {id}</a></h3>")
          .Append($"<div class=\"product_price\"><p class=\"price_color\">£{Price(id)}</p><p class=\"instock availability\"><i class=\"icon-ok\"></i> In stock</p>")
          .Append("<form><button type=\"submit\" class=\"btn btn-primary btn-block\" data-loading-text=\"Adding...\">Add to basket</button></form></div>")
          .Append("</article></li>");
    }
    sb.Append("</ol>");
    return End(sb);
}

static string Item(int id, int listings)
{
    var sb = Begin($"Book number {id}");
    sb.Append("<article class=\"product_page\"><div class=\"row\">")
      .Append($"<div class=\"col-sm-6\"><img src=\"/media/cache/{id:x8}.jpg\" alt=\"Book {id}\"></div>")
      .Append($"<div class=\"col-sm-6 product_main\"><h1>Book number {id}</h1><p class=\"price_color\">£{Price(id)}</p>")
      .Append($"<p class=\"availability\">In stock ({id % 23} available)</p><p class=\"star-rating {Stars(id)}\"></p></div></div>")
      .Append("<div id=\"product_description\" class=\"sub-header\"><h2>Product Description</h2></div><p>");
    for (var i = 0; i < 12; i++)
        sb.Append("Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. ");
    sb.Append("</p><table class=\"table table-striped\">");
    string[] rows = ["UPC", "Product Type", "Price (excl. tax)", "Price (incl. tax)", "Tax", "Availability", "Number of reviews"];
    foreach (var row in rows) sb.Append($"<tr><th>{row}</th><td>{row.Length * id % 997}</td></tr>");
    sb.Append("</table><h2>You might also like</h2><ul class=\"related\">");
    var total = listings * ItemsPerListing;
    for (var i = 1; i <= 4; i++)
    {
        var other = (id + i * 7919) % total + 1;
        sb.Append($"<li><article class=\"related_pod\"><h3><a href=\"{other}\">Book number {other}</a></h3><p class=\"price_color\">£{Price(other)}</p></article></li>");
    }
    sb.Append("</ul></article>");
    return End(sb);
}

static StringBuilder Begin(string title)
{
    var sb = new StringBuilder(32 * 1024);
    sb.Append("<!DOCTYPE html><html lang=\"en-us\"><head><meta charset=\"utf-8\">")
      .Append($"<title>{title} | Bench Books</title>")
      .Append("<meta name=\"viewport\" content=\"width=device-width\"><link rel=\"stylesheet\" href=\"/static/css/styles.css\">")
      .Append("<script src=\"/static/js/app.js\"></script></head><body id=\"default\" class=\"default\">")
      .Append("<header class=\"header container-fluid\"><div class=\"page_inner\"><a href=\"/\">Bench Books</a></div></header>")
      .Append("<div class=\"container-fluid page\"><div class=\"page_inner\"><div class=\"row\"><aside class=\"sidebar col-sm-4 col-md-3\"><ul class=\"nav nav-list\">");
    for (var i = 1; i <= 50; i++) sb.Append($"<li><a class=\"category\" href=\"/categories/{i}\">Category {i}</a></li>");
    sb.Append("</ul></aside><div class=\"col-sm-8 col-md-9\"><section>");
    return sb;
}

static string End(StringBuilder sb) =>
    sb.Append("</section></div></div></div></div><footer class=\"footer container-fluid\"><p>Bench Books is a synthetic shop for benchmarks.</p></footer></body></html>").ToString();

static string Price(int id) => $"{10 + id * 37 % 4000 / 100.0:0.00}";
static string Stars(int id) => (id % 5) switch { 0 => "One", 1 => "Two", 2 => "Three", 3 => "Four", _ => "Five" };
