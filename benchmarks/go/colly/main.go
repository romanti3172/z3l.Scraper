// Colly on the bench shop. Usage: colly <start url> <concurrency>
// Colly has no routing: every OnHTML callback runs on every page, so the page kind is told by its path.
package main

import (
	"os"
	"strconv"
	"strings"
	"sync/atomic"
	"time"

	"github.com/gocolly/colly/v2"
	"z3lbench/report"
)

func main() {
	start := os.Args[1]
	concurrency, _ := strconv.Atoi(os.Args[2])
	began := time.Now()

	var items, pages, failed atomic.Int64
	c := colly.NewCollector(colly.Async(true))
	_ = c.Limit(&colly.LimitRule{DomainGlob: "*", Parallelism: concurrency})

	c.OnResponse(func(*colly.Response) { pages.Add(1) })
	c.OnError(func(*colly.Response, error) { failed.Add(1) })
	c.OnHTML("a.catalog", func(e *colly.HTMLElement) { _ = e.Request.Visit(e.Attr("href")) })
	c.OnHTML("article.product_pod h3 a", func(e *colly.HTMLElement) { _ = e.Request.Visit(e.Attr("href")) })
	c.OnHTML("html", func(e *colly.HTMLElement) {
		if !strings.Contains(e.Request.URL.Path, "/item/") {
			return
		}
		title := e.ChildText("h1")
		price := e.DOM.Find("p.price_color").First().Text()
		availability := e.DOM.Find("p.availability").First().Text()
		if title == "" || price == "" || availability == "" {
			panic("empty item " + e.Request.URL.String())
		}
		items.Add(1)
	})

	_ = c.Visit(start)
	c.Wait()
	report.Print(items.Load(), pages.Load(), failed.Load(), began)
}
