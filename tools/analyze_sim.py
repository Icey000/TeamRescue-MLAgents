"""Summarise simulated co-op runs (Fixed vs Adaptive) with bootstrap 95% CIs.

Usage: python analyze_sim.py [suffix]
Reads results/coop_sim_fixed{suffix}.csv and results/coop_sim_adaptive{suffix}.csv.
"""
import csv
import os
import random
import statistics as st
import sys

R = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "results")
suffix = sys.argv[1] if len(sys.argv) > 1 else ""


def load(mode):
    with open(os.path.join(R, f"coop_sim_{mode}{suffix}.csv"), newline="") as f:
        return [r for r in csv.DictReader(f) if r["participant"] == "sim"]


random.seed(0)


def ci(xs, f=st.mean, n=2000):
    bs = sorted(f(random.choices(xs, k=len(xs))) for _ in range(n))
    return bs[int(0.025 * n)], bs[int(0.975 * n)]


out = {}
for m in ["fixed", "adaptive"]:
    rs = load(m)
    done = [r for r in rs if r["completed"] == "1"]
    t = [float(r["seconds"]) for r in done]
    conflict = [1.0 if float(r["robot_lost_target"]) > 0 else 0.0 for r in rs]
    out[m] = t
    share = sum(float(r["robot_cells"]) for r in rs) / max(1, sum(float(r["robot_cells"]) + float(r["human_cells"]) for r in rs))
    print(f"{m:9s} n {len(rs)} done {len(done)} ({100 * len(done) / len(rs):.1f}%)"
          f"  time mean {st.mean(t):.2f} CI {ci(t)[0]:.2f}-{ci(t)[1]:.2f} median {st.median(t):.2f}"
          f"  conflict rounds {100 * st.mean(conflict):.1f}% CI {100 * ci(conflict)[0]:.1f}-{100 * ci(conflict)[1]:.1f}"
          f"  robot share {share:.2f}")

tf, ta = out["fixed"], out["adaptive"]
d = sorted(st.mean(random.choices(tf, k=len(tf))) - st.mean(random.choices(ta, k=len(ta))) for _ in range(4000))
print(f"time saved (fixed - adaptive) mean {st.mean(tf) - st.mean(ta):.2f} s, 95% CI {d[100]:.2f} to {d[3900]:.2f}")
