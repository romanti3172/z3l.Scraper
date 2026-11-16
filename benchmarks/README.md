# Benchmarks

z3l.Scraper next to ten popular scraping frameworks, on the same local shop with the same work:

| Scraper | Language | Version | HTTP / parser |
|---|---|---|---|
| z3l.Scraper | C# | this repo | HttpClient / z3l.FastHtmlParser |
| [Scrapy](https://scrapy.org/) | Python | 2.20 | Twisted / parsel (lxml) |
| [Crawlee for Python](https://crawlee.dev/python/) `ParselCrawler` | Python | 1.11 | impit / parsel (lxml) |
| [Crawlee](https://crawlee.dev/) `CheerioCrawler` | JavaScript | 3.18 | got-scraping / cheerio |
| [node-crawler](https://github.com/bda-research/node-crawler) | JavaScript | 2.1 | got / cheerio |
| [Colly](https://go-colly.org/) | Go | 2.3 | net/http / goquery |
| [Geziyor](https://github.com/geziyor/geziyor) | Go | 2024-08 | net/http / goquery |
| [spider](https://github.com/spider-rs/spider) | Rust | 2.53 | reqwest / scraper |
| [WebMagic](https://github.com/code4craft/webmagic) | Java | 1.0.3 | Apache HttpClient / jsoup |
| [Abot2](https://github.com/sjdirect/abot) | C# | 2.0.70 | HttpClient / AngleSharp |
| [DotnetSpider](https://github.com/dotnetcore/DotnetSpider) | C# | 5.1.7 | HttpClient / HtmlAgilityPack |

## What is measured

`BenchSite` is a Kestrel shop on `127.0.0.1`: a root page links to N listings, each listing has 20 product
cards, each product page has a title, a price, an availability line, a description and related products. Pages
are rendered once and cached, so serving costs next to nothing. A delay per response stands in for network latency.

Every scraper does the same: from the root follow the listing links, from each listing follow the 20 product links,
from each product page take the title, price and availability. Settings are made equal where they exist:
32 concurrent requests, no download delay, no robots.txt, no auto-throttling, up to 3 retries, results kept in memory,
no disk storage.

Two scenarios:

- **No latency, 10 501 pages**: how much CPU the framework spends per page (the scraper is the bottleneck).
- **50 ms latency, 5 251 pages**: how well it keeps 32 requests in flight. On Windows a 50 ms `Task.Delay` lasts
  about 62.5 ms (the 15.6 ms timer tick), so the floor is 5 251 × 62.5 ms / 32 ≈ 10.3 s.

Each scraper runs in its own process and reports its crawl time (from start to the last item, without the runtime
start-up), CPU time and peak working set; for WebMagic the CPU time and memory are sampled from outside by `run.py`.
*Process wall* is the whole process including start-up. The shop counts the pages it served; a run that skipped or
repeated pages is marked.

CPU time on Windows is charged on timer ticks (15.6 ms): a thread that works for less than a tick and then waits for
the network is charged little or nothing. In the latency scenario, where scrapers work in short bursts, the Windows
CPU column is therefore too low, the more so the faster the code (the Native AOT bench is charged 0.05 s there, and
`psutil` from outside sees the same). Linux counts CPU time exactly; compare CPU time in `results-linux.md`.

## Three z3l.Scraper rows

All three are the same code with the same settings (32 requests in flight); only the way it is built differs.

- *z3l.Scraper* is the bench as `dotnet build` makes it: code compiled just in time, the workstation GC.
- *z3l.Scraper (ReadyToRun + Server GC)* is published with `-p:PublishReadyToRun=true -p:ServerGarbageCollection=true`:
  most code is compiled ahead of time and garbage is collected on all cores. The JIT is still there for the rest.
- *z3l.Scraper (Native AOT + Server GC)* is published with `-p:PublishAot=true -p:ServerGarbageCollection=true`:
  one native executable (8 MB), no JIT and no IL at all, as Go and Rust binaries are. The library is trimming and
  AOT compatible, and the bench builds with no AOT warnings. It needs the platform linker: on Windows the Visual C++
  build tools (and `vswhere.exe` on PATH, in `C:\Program Files (x86)\Microsoft Visual Studio\Installer`), on Linux
  `clang` and `zlib1g-dev`.

Without latency the scraper is CPU-bound, so this is where the build shows; more requests in flight do not help there
(at 64 both z3l.Scraper and Geziyor are a little slower than at 32). The same build settings would help Abot2 and
DotnetSpider too, but their time goes elsewhere (see below).

## Notes on the frameworks

- **spider** and **Abot2** follow every link on a page by themselves; a whitelist (spider) and decision makers
  (Abot2) keep them on the same pages as the others. spider's whitelist needs its `regex` feature.
- **Colly** has no routing: every callback runs on every page, so the page kind is told by its path.
- **node-crawler** has no de-duplication; the bench keeps a set of seen URLs.
- **DotnetSpider** has no concurrency setting; it limits requests per second (`Speed`, at most 500) and requests in
  flight (`RequestedQueueCount`, set to 32). The speed is kept by a token bucket of one token refilled by a timer,
  which on Windows ticks every 15.6 ms: it never goes above ~64 requests a second there, whatever the settings
  (on Linux the same setting gives up to ~500). It also resolves relative links against the site root instead of the
  page (`catalog/1` on `/s/0/500/` becomes `/catalog/1`); the bench gives its HTML parser the page URL so the links
  resolve correctly. It stops only after a second with an empty queue, so its crawl time is taken at the last page.

- **Losing pages.** On Windows Abot2 sometimes finishes a crawl early with no errors reported: in earlier runs it
  lost up to 200 of 10 501 pages, in the run in `results.md` 50 without latency and 1 with it; on Linux it lost none.
  DotnetSpider loses a few pages in most runs on both systems, and on Linux it sometimes stops long before the end
  (after 5 and after 1 745 pages in two runs in `results-linux.md`), when its queue happens to be empty for the
  second it waits. Both are marked *NO* in the table; *Pages loaded* shows the worst run, the times are medians.

## Results

- [`results.md`](results.md): Windows 10, 12 cores (the site and the scrapers on one desktop).
- [`results-linux.md`](results-linux.md): Linux (Ubuntu, kernel 7.0) in Docker, 8 cores of a 16-core server
  (`--cpuset-cpus 8-15`), the other 8 busy with the server's own services. On Linux a 50 ms delay lasts 50 ms, so
  the latency floor is 5 251 × 50 ms / 32 ≈ 8.2 s; and DotnetSpider is not held back by the timer (≈460 pages/s
  instead of ≈50).

## Run

Needs on PATH: the .NET 10 SDK (and for the AOT row the platform linker, see above), Python 3.12+, Node.js, Go,
Rust (`cargo`; with the GNU toolchain also MinGW-w64 for
`dlltool`), a JDK 21+ and Maven.

```
python -m venv benchmarks/.venv
benchmarks/.venv/Scripts/python -m pip install -r benchmarks/python/requirements.txt
benchmarks/.venv/Scripts/python benchmarks/run.py
```

`run.py` builds everything first (`npm ci`, `go build`, `cargo build --release`, `mvn package`, `dotnet build`).
Options: `--repeats 3`, `--concurrency 32`, `--tools z3l,z3l-tuned,z3l-aot,scrapy,crawlee-py,crawlee-js,node-crawler,colly,geziyor,spider-rs,webmagic,abot,dotnetspider`,
`--skip-build`. The table goes to `results.md`.

### Linux, in Docker

`docker/Dockerfile` holds every toolchain at the same versions and builds all benches into the image:

```
docker build -f benchmarks/docker/Dockerfile -t z3l-bench .
docker run --rm --cpuset-cpus 8-15 --memory 12g z3l-bench
```

The table is printed at the end (`results-linux.md` in the container). On Linux the memory column is the peak
resident set (max RSS) instead of the peak working set.

The site and the scraper share one machine, so on a machine with few cores the numbers are closer together than
they would be against a remote site.
