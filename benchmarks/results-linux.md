Concurrency 32, median of 3 runs, Linux 7.0.0-34-generic, 8 cores, Python 3.12.3.

### no latency, 10 501 pages

| Scraper | Language | Pages loaded | Crawl time, s | Speed, pages/s | vs fastest | CPU time, s | Peak memory, MB | Process wall, s | All pages, once |
|---|---|--:|--:|--:|--:|--:|--:|--:|:-:|
| z3l.Scraper (Native AOT + Server GC) | C# | 10,501 | 0.21 | 49,631 | 1.0x | 1.00 | 67 | 0.23 | yes |
| Geziyor | Go | 10,501 | 0.63 | 16,649 | 3.0x | 3.53 | 92 | 0.64 | yes |
| z3l.Scraper (ReadyToRun + Server GC) | C# | 10,501 | 0.76 | 13,851 | 3.6x | 3.72 | 102 | 0.82 | yes |
| Colly | Go | 10,501 | 0.82 | 12,864 | 3.9x | 4.55 | 141 | 0.82 | yes |
| z3l.Scraper | C# | 10,501 | 0.83 | 12,699 | 3.9x | 3.48 | 80 | 0.88 | yes |
| spider | Rust | 10,501 | 1.03 | 10,225 | 4.9x | 5.66 | 279 | 1.06 | yes |
| WebMagic | Java | 10,501 | 1.63 | 6,456 | 7.7x | 10.32 | 403 | 1.75 | yes |
| Crawlee for Python | Python | 10,501 | 7.99 | 1,314 | 37.8x | 11.15 | 118 | 8.54 | yes |
| node-crawler | JavaScript | 10,501 | 9.82 | 1,069 | 46.4x | 14.70 | 876 | 10.10 | yes |
| Abot2 | C# | 10,501 | 12.24 | 858 | 57.8x | 25.62 | 141 | 12.33 | yes |
| Scrapy | Python | 10,501 | 13.37 | 785 | 63.2x | 13.57 | 98 | 13.70 | yes |
| Crawlee | JavaScript | 10,501 | 15.73 | 668 | 74.3x | 21.86 | 497 | 17.31 | yes |
| DotnetSpider | C# | 5 of 10,501 | 22.44 | 468 | 106.1x | 36.10 | 129 | 23.55 | NO |

### 50 ms latency, 5 251 pages

| Scraper | Language | Pages loaded | Crawl time, s | Speed, pages/s | vs fastest | CPU time, s | Peak memory, MB | Process wall, s | All pages, once |
|---|---|--:|--:|--:|--:|--:|--:|--:|:-:|
| z3l.Scraper (Native AOT + Server GC) | C# | 5,251 | 8.35 | 629 | 1.0x | 0.95 | 53 | 8.38 | yes |
| Colly | Go | 5,251 | 8.38 | 627 | 1.0x | 2.68 | 62 | 8.39 | yes |
| Geziyor | Go | 5,251 | 8.39 | 626 | 1.0x | 2.39 | 44 | 8.40 | yes |
| z3l.Scraper (ReadyToRun + Server GC) | C# | 5,251 | 8.48 | 619 | 1.0x | 2.47 | 98 | 8.53 | yes |
| z3l.Scraper | C# | 5,251 | 8.52 | 617 | 1.0x | 2.62 | 82 | 8.56 | yes |
| spider | Rust | 5,251 | 8.52 | 616 | 1.0x | 3.15 | 187 | 8.55 | yes |
| WebMagic | Java | 5,251 | 8.75 | 600 | 1.0x | 6.52 | 220 | 8.84 | yes |
| node-crawler | JavaScript | 5,251 | 9.31 | 564 | 1.1x | 8.16 | 279 | 9.51 | yes |
| Crawlee for Python | Python | 5,251 | 9.36 | 561 | 1.1x | 6.43 | 99 | 9.79 | yes |
| Abot2 | C# | 5,251 | 10.76 | 488 | 1.3x | 15.28 | 196 | 10.87 | yes |
| Scrapy | Python | 5,251 | 12.10 | 434 | 1.4x | 7.93 | 93 | 12.42 | yes |
| DotnetSpider | C# | 1,745 of 5,251 | 12.28 | 427 | 1.5x | 21.86 | 134 | 13.34 | NO |
| Crawlee | JavaScript | 5,251 | 25.68 | 205 | 3.1x | 12.90 | 434 | 27.22 | yes |

