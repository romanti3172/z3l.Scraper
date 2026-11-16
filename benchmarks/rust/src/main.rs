//! spider (Rust) on the bench shop. Usage: spider_bench <start url> <concurrency>
//! spider follows every link on the site by itself and streams the pages; the shop's sidebar categories
//! and the header link are left out by a whitelist, so it visits the same pages as the other scrapers. Product data is taken in the subscriber.

use scraper::{Html, Selector};
use spider::website::Website;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::Arc;
use std::time::Instant;

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().collect();
    let (start, concurrency) = (args[1].clone(), args[2].parse::<usize>().unwrap());
    let began = Instant::now();

    let mut website = Website::new(&start)
        .with_concurrency_limit(Some(concurrency))
        .with_respect_robots_txt(false)
        .with_whitelist_url(Some(vec!["/s/".into()]))
        .with_retry(3)
        .build()
        .unwrap();

    let items = Arc::new(AtomicI64::new(0));
    let pages = Arc::new(AtomicI64::new(0));
    let failed = Arc::new(AtomicI64::new(0));

    let mut rx = website.subscribe(100_000);
    let (i, p, f) = (items.clone(), pages.clone(), failed.clone());
    let consumer = tokio::spawn(async move {
        let mut work = tokio::task::JoinSet::new();
        while let Ok(page) = rx.recv().await {
            if std::env::var("BENCH_DEBUG").is_ok() {
                eprintln!("{:?} {} {}", began.elapsed(), page.status_code, page.get_url());
            }
            if !page.status_code.is_success() {
                f.fetch_add(1, Ordering::Relaxed);
                continue;
            }
            p.fetch_add(1, Ordering::Relaxed);
            if !page.get_url().contains("/item/") {
                continue;
            }
            let i = i.clone();
            work.spawn(async move {
                let doc = Html::parse_document(&page.get_html());
                let text = |css: &str| {
                    doc.select(&Selector::parse(css).unwrap()).next().map(|e| e.text().collect::<String>())
                };
                let (title, price, availability) = (text("h1"), text("p.price_color"), text("p.availability"));
                if title.is_none() || price.is_none() || availability.is_none() {
                    panic!("empty item {}", page.get_url());
                }
                i.fetch_add(1, Ordering::Relaxed);
            });
        }
        while let Some(result) = work.join_next().await {
            result.unwrap();
        }
    });

    website.crawl().await;
    website.unsubscribe();
    drop(website);
    consumer.await.unwrap();

    report(items.load(Ordering::Relaxed), pages.load(Ordering::Relaxed), failed.load(Ordering::Relaxed), began);
}

fn report(items: i64, pages: i64, failed: i64, began: Instant) {
    let seconds = began.elapsed().as_secs_f64();
    let (cpu_seconds, peak_mb) = usage();
    println!(
        "{}",
        serde_json::json!({
            "items": items, "pages": pages, "failed": failed, "seconds": seconds,
            "cpu_seconds": cpu_seconds, "peak_mb": peak_mb,
        })
    );
}

/// CPU time in seconds and peak working set in MB.
#[cfg(windows)]
fn usage() -> (f64, f64) {
    #[repr(C)]
    #[derive(Default)]
    struct MemoryCounters {
        cb: u32,
        page_fault_count: u32,
        peak_working_set_size: usize,
        rest: [usize; 7],
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetCurrentProcess() -> isize;
        fn GetProcessTimes(process: isize, creation: *mut u64, exit: *mut u64, kernel: *mut u64, user: *mut u64) -> i32;
        fn K32GetProcessMemoryInfo(process: isize, counters: *mut MemoryCounters, cb: u32) -> i32;
    }

    let (mut c, mut e, mut k, mut u) = (0u64, 0u64, 0u64, 0u64);
    let mut mem = MemoryCounters { cb: std::mem::size_of::<MemoryCounters>() as u32, ..Default::default() };
    unsafe {
        let me = GetCurrentProcess();
        GetProcessTimes(me, &mut c, &mut e, &mut k, &mut u);
        K32GetProcessMemoryInfo(me, &mut mem, mem.cb);
    }
    ((k + u) as f64 / 1e7, mem.peak_working_set_size as f64 / 1048576.0)
}

/// CPU time in seconds and max RSS in MB (Linux reports it in KB).
#[cfg(unix)]
fn usage() -> (f64, f64) {
    let mut ru: libc::rusage = unsafe { std::mem::zeroed() };
    unsafe { libc::getrusage(libc::RUSAGE_SELF, &mut ru) };
    let secs = |t: libc::timeval| t.tv_sec as f64 + t.tv_usec as f64 / 1e6;
    (secs(ru.ru_utime) + secs(ru.ru_stime), ru.ru_maxrss as f64 / 1024.0)
}
