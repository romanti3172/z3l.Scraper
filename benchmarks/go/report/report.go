// Package report prints the result line every bench prints: crawl time, CPU time and peak memory
// (peak working set on Windows, max RSS elsewhere).
package report

import (
	"encoding/json"
	"fmt"
	"time"
)

func Print(items, pages, failed int64, began time.Time) {
	seconds := time.Since(began).Seconds()
	cpu, peak := usage()
	line, _ := json.Marshal(map[string]any{
		"items": items, "pages": pages, "failed": failed, "seconds": seconds,
		"cpu_seconds": cpu, "peak_mb": peak,
	})
	fmt.Println(string(line))
}
