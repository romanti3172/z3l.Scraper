"""Crawlee for Python (ParselCrawler) on the bench shop. Usage: python crawlee_bench.py <start url> <concurrency>"""
import asyncio
import json
import logging
import sys
import time

from usage import usage
from crawlee import ConcurrencySettings
from crawlee.crawlers import ParselCrawler, ParselCrawlingContext
from crawlee.storage_clients import MemoryStorageClient


async def run(start, concurrency):
    items = []
    crawler = ParselCrawler(
        concurrency_settings=ConcurrencySettings(
            min_concurrency=concurrency, desired_concurrency=concurrency, max_concurrency=concurrency),
        storage_client=MemoryStorageClient(),
        max_request_retries=3,
        configure_logging=False,
    )

    @crawler.router.default_handler
    async def root(ctx: ParselCrawlingContext) -> None:
        await ctx.enqueue_links(selector="a.catalog", label="LISTING")

    @crawler.router.handler("LISTING")
    async def listing(ctx: ParselCrawlingContext) -> None:
        await ctx.enqueue_links(selector="article.product_pod h3 a", label="ITEM")

    @crawler.router.handler("ITEM")
    async def item(ctx: ParselCrawlingContext) -> None:
        title = ctx.selector.css("h1::text").get()
        price = ctx.selector.css("p.price_color::text").get()
        if title is None or price is None:
            raise ValueError(f"empty item {ctx.request.url}")
        items.append({
            "title": title,
            "price": price,
            "availability": ctx.selector.css("p.availability::text").get(),
            "url": ctx.request.url,
        })

    stats = await crawler.run([start])
    return len(items), stats


def main():
    logging.basicConfig(level=logging.WARNING)
    start, concurrency = sys.argv[1], int(sys.argv[2])
    began = time.perf_counter()
    items, stats = asyncio.run(run(start, concurrency))
    seconds = time.perf_counter() - began

    cpu_seconds, peak_mb = usage()
    print(json.dumps({
        "items": items,
        "pages": stats.requests_finished,
        "failed": stats.requests_failed,
        "seconds": seconds,
        "cpu_seconds": cpu_seconds,
        "peak_mb": peak_mb,
    }))


if __name__ == "__main__":
    main()
