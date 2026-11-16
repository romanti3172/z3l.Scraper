"""CPU time and peak memory of this process: peak working set on Windows, max RSS elsewhere."""
import sys

import psutil


def usage():
    me = psutil.Process()
    cpu = me.cpu_times()
    if sys.platform == "win32":
        peak = me.memory_info().peak_wset / 1048576
    else:
        import resource
        peak = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024  # KB on Linux
    return cpu.user + cpu.system, peak
