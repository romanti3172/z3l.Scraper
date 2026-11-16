// node-crawler ("crawler" on npm) on the bench shop. Usage: node node_crawler_bench.js <start url> <concurrency>
// It has no link de-duplication of its own beyond what we add, and no routing: each request carries its callback.
import Crawler from 'crawler';
import { report } from './report.js';

const [start, concurrency] = [process.argv[2], Number(process.argv[3])];
const began = performance.now();

let items = 0, pages = 0, failed = 0;
const seen = new Set();
const crawler = new Crawler({ maxConnections: concurrency, retries: 3, retryInterval: 1000, silence: true });

function add(url, callback) {
    if (seen.has(url)) return;
    seen.add(url);
    crawler.add({
        url,
        callback(error, res, done) {
            if (error || res.statusCode !== 200) failed++;
            else { pages++; callback(res); }
            done();
        },
    });
}

const links = (res, selector) => res.$(selector).map((_, a) => new URL(res.$(a).attr('href'), res.options.url).href).get();

const item = res => {
    const $ = res.$;
    const title = $('h1').first().text(), price = $('p.price_color').first().text(), availability = $('p.availability').first().text();
    if (!title || !price || !availability) throw new Error(`empty item ${res.options.url}`);
    items++;
};
const listing = res => links(res, 'article.product_pod h3 a').forEach(url => add(url, item));
const root = res => links(res, 'a.catalog').forEach(url => add(url, listing));

await new Promise(resolve => {
    crawler.on('drain', resolve);
    add(start, root);
});
report({ items, pages, failed, began });
