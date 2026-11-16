package report

import (
	"syscall"
	"unsafe"
)

type memoryCounters struct {
	cb                 uint32
	PageFaultCount     uint32
	PeakWorkingSetSize uintptr
	rest               [7]uintptr
}

var getProcessMemoryInfo = syscall.NewLazyDLL("psapi.dll").NewProc("GetProcessMemoryInfo")

// usage returns the CPU time in seconds and the peak working set in MB.
func usage() (float64, float64) {
	self, _ := syscall.GetCurrentProcess()

	var creation, exit, kernel, user syscall.Filetime
	_ = syscall.GetProcessTimes(self, &creation, &exit, &kernel, &user)
	ticks := func(f syscall.Filetime) float64 { return float64(uint64(f.HighDateTime)<<32 | uint64(f.LowDateTime)) } // 100 ns each

	var mem memoryCounters
	mem.cb = uint32(unsafe.Sizeof(mem))
	getProcessMemoryInfo.Call(uintptr(self), uintptr(unsafe.Pointer(&mem)), uintptr(mem.cb))
	return (ticks(kernel) + ticks(user)) / 1e7, float64(mem.PeakWorkingSetSize) / 1048576
}
