package bench;

import java.util.concurrent.atomic.AtomicLong;
import java.util.List;
import us.codecraft.webmagic.Page;
import us.codecraft.webmagic.Request;
import us.codecraft.webmagic.Site;
import us.codecraft.webmagic.Spider;
import us.codecraft.webmagic.SpiderListener;
import us.codecraft.webmagic.processor.PageProcessor;

/** WebMagic on the bench shop. Usage: WebMagicBench <start url> <concurrency>. CPU time and memory are measured by run.py. */
public class WebMagicBench implements PageProcessor {
    static final AtomicLong items = new AtomicLong(), pages = new AtomicLong(), failed = new AtomicLong();

    private final Site site = Site.me().setRetryTimes(3).setSleepTime(0).setRetrySleepTime(1000).setTimeOut(60_000);

    @Override
    public void process(Page page) {
        pages.incrementAndGet();
        page.setSkip(true);
        String url = page.getUrl().toString();
        if (url.contains("/item/")) {
            String title = page.getHtml().css("h1", "text").get();
            String price = page.getHtml().css("p.price_color", "text").get();
            String availability = page.getHtml().css("p.availability", "text").get();
            if (title == null || price == null || availability == null) throw new IllegalStateException("empty item " + url);
            items.incrementAndGet();
        } else if (url.contains("/catalog/")) {
            page.addTargetRequests(page.getHtml().xpath("//article[@class='product_pod']//h3/a/@href").all());
        } else {
            page.addTargetRequests(page.getHtml().xpath("//a[@class='catalog']/@href").all());
        }
    }

    @Override
    public Site getSite() { return site; }

    public static void main(String[] args) {
        long began = System.nanoTime();
        Spider.create(new WebMagicBench())
            .addUrl(args[0])
            .thread(Integer.parseInt(args[1]))
            .addPipeline((resultItems, task) -> { })
            .setSpiderListeners(List.of(new SpiderListener() {
                @Override public void onSuccess(Request request) { }
                @Override public void onError(Request request, Exception e) { failed.incrementAndGet(); }
            }))
            .run();
        double seconds = (System.nanoTime() - began) / 1e9;
        System.out.printf(java.util.Locale.ROOT, "{\"items\": %d, \"pages\": %d, \"failed\": %d, \"seconds\": %.4f}%n",
            items.get(), pages.get(), failed.get(), seconds);
    }
}
