Concurrency 32, median of 3 runs, Windows 10, 12 cores, Python 3.14.10.

### no latency, 10 501 pages

| Scraper | Language | Pages loaded | Crawl time, s | Speed, pages/s | vs fastest | CPU time, s | Peak memory, MB | Process wall, s | All pages, once |
|---|---|--:|--:|--:|--:|--:|--:|--:|:-:|
| z3l.Scraper (Native AOT + Server GC) | C# | 10,501 | 0.24 | 44,396 | 1.0x | 1.59 | 71 | 0.28 | yes |
| z3l.Scraper (ReadyToRun + Server GC) | C# | 10,501 | 0.53 | 19,877 | 2.2x | 3.84 | 93 | 0.63 | yes |
| Geziyor | Go | 10,501 | 0.67 | 15,649 | 2.8x | 5.72 | 136 | 0.70 | yes |
| z3l.Scraper | C# | 10,501 | 0.78 | 13,533 | 3.3x | 4.31 | 64 | 0.89 | yes |
| Colly | Go | 10,501 | 0.89 | 11,790 | 3.8x | 7.77 | 155 | 0.92 | yes |
| spider | Rust | 10,501 | 0.97 | 10,779 | 4.1x | 9.52 | 292 | 1.00 | yes |
| WebMagic | Java | 10,501 | 1.82 | 5,764 | 7.7x | 15.92 | 512 | 2.05 | yes |
| node-crawler | JavaScript | 10,501 | 10.52 | 998 | 44.5x | 16.67 | 311 | 10.86 | yes |
| Abot2 | C# | 10,451 of 10,501 | 14.59 | 720 | 61.7x | 35.53 | 147 | 14.73 | NO |
| Crawlee for Python | Python | 10,501 | 18.32 | 573 | 77.5x | 29.73 | 110 | 20.12 | yes |
| Scrapy | Python | 10,501 | 21.00 | 500 | 88.8x | 21.38 | 125 | 22.11 | yes |
| Crawlee | JavaScript | 10,501 | 22.30 | 471 | 94.3x | 29.91 | 462 | 24.52 | yes |
| DotnetSpider | C# | 10,501 | 214.32 | 49 | 906.1x | 6.94 | 94 | 216.61 | yes |

### 50 ms latency, 5 251 pages

| Scraper | Language | Pages loaded | Crawl time, s | Speed, pages/s | vs fastest | CPU time, s | Peak memory, MB | Process wall, s | All pages, once |
|---|---|--:|--:|--:|--:|--:|--:|--:|:-:|
| z3l.Scraper (Native AOT + Server GC) | C# | 5,251 | 10.18 | 516 | 1.0x | 0.05 | 58 | 10.23 | yes |
| Colly | Go | 5,251 | 10.21 | 514 | 1.0x | 3.78 | 86 | 10.26 | yes |
| Geziyor | Go | 5,251 | 10.23 | 513 | 1.0x | 3.56 | 74 | 10.27 | yes |
| spider | Rust | 5,251 | 10.32 | 509 | 1.0x | 1.55 | 198 | 10.36 | yes |
| z3l.Scraper (ReadyToRun + Server GC) | C# | 5,251 | 10.36 | 507 | 1.0x | 1.81 | 90 | 10.48 | yes |
| z3l.Scraper | C# | 5,251 | 10.36 | 507 | 1.0x | 1.50 | 70 | 10.47 | yes |
| Abot2 | C# | 5,250 of 5,251 | 10.79 | 487 | 1.1x | 18.97 | 243 | 10.98 | NO |
| WebMagic | Java | 5,251 | 10.82 | 485 | 1.1x | 5.81 | 396 | 11.00 | yes |
| node-crawler | JavaScript | 5,251 | 11.59 | 453 | 1.1x | 7.45 | 588 | 11.92 | yes |
| Crawlee for Python | Python | 5,251 | 14.03 | 374 | 1.4x | 8.59 | 90 | 15.16 | yes |
| Scrapy | Python | 5,251 | 16.04 | 327 | 1.6x | 10.08 | 103 | 17.10 | yes |
| Crawlee | JavaScript | 5,251 | 31.06 | 169 | 3.1x | 9.37 | 410 | 33.14 | yes |
| DotnetSpider | C# | 5,250 of 5,251 | 107.60 | 49 | 10.6x | 4.83 | 93 | 109.69 | NO |

