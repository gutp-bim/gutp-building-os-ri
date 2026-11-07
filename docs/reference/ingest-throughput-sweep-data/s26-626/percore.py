import sys, time
def snap():
    out = {}
    for line in open('/proc/stat'):
        if line.startswith('cpu') and line[3].isdigit():
            p = line.split(); v = list(map(int, p[1:])); out[p[0]] = (sum(v) - v[3] - v[4], sum(v))
    return out
dur = float(sys.argv[1]); a = snap(); time.sleep(dur); b = snap()
print(' '.join(f"{k[3:]}:{100*(b[k][0]-a[k][0])/max(1,b[k][1]-a[k][1]):.0f}" for k in sorted(a, key=lambda k: int(k[3:]))))
