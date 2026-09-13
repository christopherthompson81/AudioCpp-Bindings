import os, sys, time
cmd = sys.argv[1:]
t0 = time.time()
pid = os.spawnvpe(os.P_NOWAIT, cmd[0], cmd, os.environ)
_, status, ru = os.wait4(pid, 0)
wall = time.time() - t0
print(f"--- wall {wall:.1f}s  user {ru.ru_utime:.1f}s  sys {ru.ru_stime:.1f}s  "
      f"parallelism {(ru.ru_utime+ru.ru_stime)/wall:.2f}x")
print(f"--- minor faults {ru.ru_minflt:,}  major faults {ru.ru_majflt:,}  "
      f"maxRSS {ru.ru_maxrss/1024/1024:.2f} GB  vol-ctxsw {ru.ru_nvcsw:,}  invol {ru.ru_nivcsw:,}")
# Propagate the child's status. A run that aborts mid-sweep otherwise prints a
# plausible-looking fast result and exits 0, silently contaminating the medians.
sys.exit(os.waitstatus_to_exitcode(status))
