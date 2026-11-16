"""Runs every scraper on the bench shop and prints a table.

Usage (from the repo root):
    benchmarks/.venv/Scripts/python benchmarks/run.py [--repeats 3] [--concurrency 32] [--tools z3l,scrapy,...]

Needs on PATH: dotnet, node, go, cargo (with MinGW-w64 for the GNU toolchain), java and mvn. --skip-build reuses
what was built before.

Each scraper runs in its own process and prints one JSON line: items, pages, crawl time and, where it can, its own CPU
time and peak working set. For the others (the JVM) run.py samples them from outside. The shop counts the pages it
served, so a scraper that skipped or repeated pages is caught.
"""
import argparse
import json
import os
import platform
import shutil
import statistics
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

import psutil

HERE = Path(__file__).resolve().parent
PORT = 5099
SITE = f"http://127.0.0.1:{PORT}"
ITEMS_PER_LISTING = 20
WINDOWS = sys.platform == "win32"
EXE = ".exe" if WINDOWS else ""
RID = ("win" if WINDOWS else "linux") + "-x64"

# (name, delay per response in ms, listings)
SCENARIOS = [
    ("no latency, 10 501 pages", 0, 500),
    ("50 ms latency, 5 251 pages", 50, 250),
]


def exe(name):
    path = shutil.which(name)
    if path is None:
        raise RuntimeError(f"{name} is not on PATH")
    return path


def dotnet_dll(project):
    return [exe("dotnet"), str(HERE / project / "bin" / "Release" / "net10.0" / f"{project}.dll")]


def node(script):
    return [exe("node"), str(HERE / "node" / script)]


def python(script):
    return [sys.executable, str(HERE / "python" / script)]


JAVA_CP = os.pathsep.join([str(HERE / "java" / "target" / "classes"), str(HERE / "java" / "target" / "dependency" / "*")])

# key: (title, language, command)
TOOLS = {
    "z3l": ("z3l.Scraper", "C#", lambda: dotnet_dll("Z3lBench")),
    # the same bench published with code compiled ahead of time and the server GC (see README)
    "z3l-tuned": ("z3l.Scraper (ReadyToRun + Server GC)", "C#",
                  lambda: [str(HERE / "Z3lBench" / "bin" / "tuned" / f"Z3lBench{EXE}")]),
    # compiled to one native executable, no JIT at all (needs the platform linker: MSVC on Windows, clang on Linux)
    "z3l-aot": ("z3l.Scraper (Native AOT + Server GC)", "C#",
                lambda: [str(HERE / "Z3lBench" / "bin" / "aot" / f"Z3lBench{EXE}")]),
    "scrapy": ("Scrapy", "Python", lambda: python("scrapy_bench.py")),
    "crawlee-py": ("Crawlee for Python", "Python", lambda: python("crawlee_bench.py")),
    "crawlee-js": ("Crawlee", "JavaScript", lambda: node("crawlee_bench.js")),
    "node-crawler": ("node-crawler", "JavaScript", lambda: node("node_crawler_bench.js")),
    "colly": ("Colly", "Go", lambda: [str(HERE / "go" / "bin" / f"colly{EXE}")]),
    "geziyor": ("Geziyor", "Go", lambda: [str(HERE / "go" / "bin" / f"geziyor{EXE}")]),
    "spider-rs": ("spider", "Rust", lambda: [str(HERE / "rust" / "target" / "release" / f"spider_bench{EXE}")]),
    "webmagic": ("WebMagic", "Java", lambda: [exe("java"), "-cp", JAVA_CP, "bench.WebMagicBench"]),
    "abot": ("Abot2", "C#", lambda: dotnet_dll("AbotBench")),
    "dotnetspider": ("DotnetSpider", "C#", lambda: dotnet_dll("DotnetSpiderBench")),
}


def build(tools):
    quiet = {"stdout": subprocess.DEVNULL, "check": True}
    dotnet = {"z3l": "Z3lBench", "abot": "AbotBench", "dotnetspider": "DotnetSpiderBench"}
    for project in ["BenchSite"] + [dotnet[t] for t in tools if t in dotnet]:
        subprocess.run([exe("dotnet"), "build", "-c", "Release", "-v", "q", "-nologo", str(HERE / project)], **quiet)
    if "z3l-tuned" in tools:
        subprocess.run([exe("dotnet"), "publish", str(HERE / "Z3lBench"), "-c", "Release", "-r", RID,
                        "--self-contained", "false", "-p:PublishReadyToRun=true", "-p:ServerGarbageCollection=true",
                        "-o", str(HERE / "Z3lBench" / "bin" / "tuned"), "-v", "q", "-nologo"], **quiet)
    if "z3l-aot" in tools:
        subprocess.run([exe("dotnet"), "publish", str(HERE / "Z3lBench"), "-c", "Release", "-r", RID,
                        "-p:PublishAot=true", "-p:ServerGarbageCollection=true",
                        "-o", str(HERE / "Z3lBench" / "bin" / "aot"), "-v", "q", "-nologo"], **quiet)
    if {"crawlee-js", "node-crawler"} & set(tools):
        subprocess.run([exe("npm"), "ci", "--no-audit", "--no-fund"], cwd=HERE / "node", **quiet)
    if {"colly", "geziyor"} & set(tools):
        subprocess.run([exe("go"), "build", "-o", "bin/", "./colly", "./geziyor"], cwd=HERE / "go", **quiet)
    if "spider-rs" in tools:
        subprocess.run([exe("cargo"), "build", "--release", "-q"], cwd=HERE / "rust", **quiet)
    if "webmagic" in tools:
        subprocess.run([exe("mvn"), "-q", "-B", "package", "dependency:copy-dependencies"], cwd=HERE / "java", **quiet)


def hits():
    with urllib.request.urlopen(f"{SITE}/_hits") as r:
        return int(r.read())


def wait_for_site():
    for _ in range(100):
        try:
            hits()
            return
        except OSError:
            time.sleep(0.1)
    raise RuntimeError("the bench site did not start")


def peak_memory_mb(process):
    """Peak working set on Windows; on Linux the high-water mark of the resident set (VmHWM)."""
    if WINDOWS:
        return process.memory_info().peak_wset / 1048576
    with open(f"/proc/{process.pid}/status") as status:
        for line in status:
            if line.startswith("VmHWM:"):
                return int(line.split()[1]) / 1024
    return 0.0


def cpu_count():
    return len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else os.cpu_count()


def run_once(tool, url, concurrency):
    hits()
    wall = time.perf_counter()
    proc = subprocess.Popen(TOOLS[tool][2]() + [url, str(concurrency)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    # sampled from outside for the scrapers that do not report themselves; the peak working set only grows,
    # so the last sample is close to the real peak
    watched, peak, cpu = psutil.Process(proc.pid), 0.0, 0.0
    while proc.poll() is None:
        try:
            peak = max(peak, peak_memory_mb(watched))
            times = watched.cpu_times()
            cpu = times.user + times.system
        except psutil.Error:
            pass
        time.sleep(0.02)
    stdout, stderr = proc.communicate()
    wall = time.perf_counter() - wall
    lines = [l for l in stdout.splitlines() if l.startswith("{")]
    if proc.returncode != 0 or not lines:
        raise RuntimeError(f"{tool} failed ({proc.returncode}):\n{stdout[-2000:]}\n{stderr[-4000:]}")
    result = json.loads(lines[-1])
    result.setdefault("peak_mb", peak)
    result.setdefault("cpu_seconds", cpu)
    result["wall"] = wall
    result["served"] = hits()
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--concurrency", type=int, default=32)
    parser.add_argument("--tools", default=",".join(TOOLS))
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--build-only", action="store_true")
    parser.add_argument("--out", default="results.md", help="file for the table, next to run.py")
    args = parser.parse_args()
    tools = args.tools.split(",")

    if not args.skip_build:
        build(tools)
    if args.build_only:
        return
    site = subprocess.Popen(dotnet_dll("BenchSite") + [str(PORT)], stdout=subprocess.DEVNULL)
    report = [f"Concurrency {args.concurrency}, median of {args.repeats} runs, {platform.system()} {platform.release()}, "
              f"{cpu_count()} cores, Python {sys.version.split()[0]}.", ""]
    try:
        wait_for_site()
        for title, delay, listings in SCENARIOS:
            url = f"{SITE}/s/{delay}/{listings}/"
            expected_pages = 1 + listings + listings * ITEMS_PER_LISTING
            expected_items = listings * ITEMS_PER_LISTING
            rows = []
            for tool in tools:
                runs = []
                for i in range(args.repeats):
                    r = run_once(tool, url, args.concurrency)
                    ok = r["items"] == expected_items and r["served"] == expected_pages
                    print(f"  {title} | {tool} #{i + 1}: {r['seconds']:.2f}s, {r['items']} items, "
                          f"{r['served']} served{'' if ok else '  <-- MISMATCH'}", flush=True)
                    runs.append(r)
                med = lambda k: statistics.median(x[k] for x in runs)
                rows.append((tool, med("seconds"), med("cpu_seconds"), med("peak_mb"), med("wall"),
                             all(x["items"] == expected_items and x["served"] == expected_pages for x in runs),
                             min(x["served"] for x in runs)))

            best = min(r[1] for r in rows)
            report += [f"### {title}", "",
                       "| Scraper | Language | Pages loaded | Crawl time, s | Speed, pages/s | vs fastest | CPU time, s | Peak memory, MB | Process wall, s | All pages, once |",
                       "|---|---|--:|--:|--:|--:|--:|--:|--:|:-:|"]
            for tool, sec, cpu, mem, wall, ok, served in sorted(rows, key=lambda r: r[1]):
                pages = f"{served:,}" if ok else f"{served:,} of {expected_pages:,}"
                report.append(f"| {TOOLS[tool][0]} | {TOOLS[tool][1]} | {pages} | {sec:.2f} | {expected_pages / sec:,.0f} | {sec / best:.1f}x | "
                              f"{cpu:.2f} | {mem:.0f} | {wall:.2f} | {'yes' if ok else 'NO'} |")
            report.append("")
    finally:
        site.terminate()

    text = "\n".join(report)
    print("\n" + text)
    (HERE / args.out).write_text(text + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
