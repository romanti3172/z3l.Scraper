// Prints the result line every bench prints: crawl time, CPU time and peak working set (maxRSS is the peak
// working set on Windows, in KB).
export function report({ items, pages, failed, began }) {
    const cpu = process.cpuUsage();
    console.log(JSON.stringify({
        items,
        pages,
        failed,
        seconds: (performance.now() - began) / 1000,
        cpu_seconds: (cpu.user + cpu.system) / 1e6,
        peak_mb: process.resourceUsage().maxRSS / 1024,
    }));
}
