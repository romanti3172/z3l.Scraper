//go:build !windows

package report

import "syscall"

// usage returns the CPU time in seconds and the max RSS in MB (Linux reports it in KB).
func usage() (float64, float64) {
	var ru syscall.Rusage
	_ = syscall.Getrusage(syscall.RUSAGE_SELF, &ru)
	seconds := func(t syscall.Timeval) float64 { return float64(t.Sec) + float64(t.Usec)/1e6 }
	return seconds(ru.Utime) + seconds(ru.Stime), float64(ru.Maxrss) / 1024
}
