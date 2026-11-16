// Crawlee (JavaScript, CheerioCrawler) on the bench shop. Usage: node crawlee_bench.js <start url> <concurrency>
import { CheerioCrawler, Configuration, log } from 'crawlee';
import { report } from './report.js';

const [start, concurrency] = [process.argv[2], Number(process.argv[3])];
log.setLevel(log.LEVELS.WARNING);
const began = performance.now();

let items = 0;
const crawler = new CheerioCrawler({
    minConcurrency: concurrency,
    maxConcurrency: concurrency,
    maxRequestRetries: 3,
    async requestHandler({ request, $, enqueueLinks }) {
        if (request.label === 'LISTING') {
            await enqueueLinks({ selector: 'article.product_pod h3 a', label: 'ITEM' });
        } else if (request.label === 'ITEM') {
            const title = $('h1').first().text();
            const price = $('p.price_color').first().text();
            const availability = $('p.availability').first().text();
            if (!title || !price || !availability) throw new Error(`empty item ${request.url}`);
            items++;
        } else {
            await enqueueLinks({ selector: 'a.catalog', label: 'LISTING' });
        }
    },
}, new Configuration({ persistStorage: false, purgeOnStart: true }));

const stats = await crawler.run([start]);
report({ items, pages: stats.requestsFinished, failed: stats.requestsFailed, began });
