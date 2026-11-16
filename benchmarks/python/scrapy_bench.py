"""Scrapy on the bench shop. Usage: python scrapy_bench.py <start url> <concurrency>"""
import json
import sys
import time

from usage import usage
import scrapy
from scrapy.crawler import CrawlerProcess


class BenchSpider(scrapy.Spider):
    name = "bench"

    def __init__(self, start, **kwargs):
        super().__init__(**kwargs)
        self.start_urls = [start]

    def parse(self, response):
        for href in response.css("a.catalog::attr(href)").getall():
            yield response.follow(href, self.parse_listing)

    def parse_listing(self, response):
        for href in response.xpath("//article[@class='product_pod']//h3/a/@href").getall():
            yield response.follow(href, self.parse_item)

    def parse_item(self, response):
        title = response.css("h1::text").get()
        price = response.css("p.price_color::text").get()
        if title is None or price is None:
            raise ValueError(f"empty item {response.url}")
        yield {
            "title": title,
            "price": price,
            "availability": response.css("p.availability::text").get(),
            "url": response.url,
        }


def main():
    start, concurrency = sys.argv[1], int(sys.argv[2])
    began = time.perf_counter()
    process = CrawlerProcess(settings={
        "LOG_LEVEL": "WARNING",
        "ROBOTSTXT_OBEY": False,
        "CONCURRENT_REQUESTS": concurrency,
        "CONCURRENT_REQUESTS_PER_DOMAIN": concurrency,
        "DOWNLOAD_DELAY": 0,
        "AUTOTHROTTLE_ENABLED": False,
        "TELNETCONSOLE_ENABLED": False,
    })
    crawler = process.create_crawler(BenchSpider)
    process.crawl(crawler, start=start)
    process.start()
    seconds = time.perf_counter() - began

    stats = crawler.stats.get_stats()
    cpu_seconds, peak_mb = usage()
    print(json.dumps({
        "items": stats.get("item_scraped_count", 0),
        "pages": stats.get("downloader/response_status_count/200", 0),
        "failed": stats.get("downloader/response_count", 0) - stats.get("downloader/response_status_count/200", 0),
        "seconds": seconds,
        "cpu_seconds": cpu_seconds,
        "peak_mb": peak_mb,
    }))


if __name__ == "__main__":
    main()
