// Geziyor on the bench shop. Usage: geziyor <start url> <concurrency>
package main

import (
	"os"
	"strconv"
	"sync/atomic"
	"time"

	"github.com/PuerkitoBio/goquery"
	"github.com/geziyor/geziyor"
	"github.com/geziyor/geziyor/client"
	"z3lbench/report"
)

var items, pages, failed atomic.Int64

func follow(g *geziyor.Geziyor, r *client.Response, selector string, parse func(*geziyor.Geziyor, *client.Response)) {
	r.HTMLDoc.Find(selector).Each(func(_ int, s *goquery.Selection) {
		if href, ok := s.Attr("href"); ok {
			g.Get(r.JoinURL(href), parse)
		}
	})
}

func root(g *geziyor.Geziyor, r *client.Response) {
	pages.Add(1)
	follow(g, r, "a.catalog", listing)
}

func listing(g *geziyor.Geziyor, r *client.Response) {
	pages.Add(1)
	follow(g, r, "article.product_pod h3 a", item)
}

func item(_ *geziyor.Geziyor, r *client.Response) {
	pages.Add(1)
	title := r.HTMLDoc.Find("h1").First().Text()
	price := r.HTMLDoc.Find("p.price_color").First().Text()
	availability := r.HTMLDoc.Find("p.availability").First().Text()
	if title == "" || price == "" || availability == "" {
		panic("empty item " + r.Request.URL.String())
	}
	items.Add(1)
}

func main() {
	start := os.Args[1]
	concurrency, _ := strconv.Atoi(os.Args[2])
	began := time.Now()

	geziyor.NewGeziyor(&geziyor.Options{
		StartURLs:                   []string{start},
		ParseFunc:                   root,
		ConcurrentRequests:          concurrency,
		ConcurrentRequestsPerDomain: concurrency,
		RobotsTxtDisabled:           true,
		LogDisabled:                 true,
		RetryTimes:                  3,
		ErrorFunc:                   func(*geziyor.Geziyor, *client.Request, error) { failed.Add(1) },
	}).Start()

	report.Print(items.Load(), pages.Load(), failed.Load(), began)
}
