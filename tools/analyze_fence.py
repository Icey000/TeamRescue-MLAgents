"""Summarise the fenced co-op simulations (Fixed vs Adaptive) with bootstrap 95% CIs.

Usage: python analyze_fence.py
Reads results/coop_fence_fixed.csv and results/coop_fence_adaptive.csv.
"""
import csv
import os
import random
import statistics as st

R = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "results")
random.seed(0)


def load(mode):
    with open(os.path.join(R, f"coop_fence_{mode}.csv"), newline="") as f:
        return [r for r in csv.DictReader(f) if r["participant"] == "sim"]


def ci(xs, n=2000):
    bs = sorted(st.mean(random.choices(xs, k=len(xs))) for _ in range(n))
    return bs[int(0.025 * n)], bs[int(0.975 * n)]


def diff_ci(a, b, n=4000):
    d = sorted(st.mean(random.choices(a, k=len(a))) - st.mean(random.choices(b, k=len(b))) for _ in range(n))
    return d[int(0.025 * n)], d[int(0.975 * n)]


data = {}
for m in ["fixed", "adaptive"]:
    rs = load(m)
    done = [r for r in rs if r["completed"] == "1"]
    col = lambda name, rows=done: [float(r[name]) for r in rows]
    data[m] = {
        "time": col("seconds"),
        "wait": col("human_fence_wait"),
        "asks": col("human_help_requests"),
        "conflict": [1.0 if float(r["robot_lost_target"]) > 0 else 0.0 for r in rs],
    }
    t, w = data[m]["time"], data[m]["wait"]
    print(f"{m:9s} n {len(rs)} completed {len(done)} ({100 * len(done) / len(rs):.1f}%)")
    print(f"          time {st.mean(t):.2f} s (CI {ci(t)[0]:.2f}-{ci(t)[1]:.2f}), median {st.median(t):.2f}")
    print(f"          player waits at fence {st.mean(w):.2f} s (CI {ci(w)[0]:.2f}-{ci(w)[1]:.2f})")
    print(f"          player had to ask {st.mean(data[m]['asks']):.2f} times/round;"
          f" robot opened for player {st.mean(col('robot_assists')):.2f}, player opened for robot {st.mean(col('human_assists')):.2f}")
    print(f"          robot asked {st.mean(col('robot_help_requests')):.2f} times/round; same-cell conflicts {100 * st.mean(data[m]['conflict']):.1f}%")

f, a = data["fixed"], data["adaptive"]
lo, hi = diff_ci(f["time"], a["time"])
print(f"time saved (fixed - adaptive) {st.mean(f['time']) - st.mean(a['time']):.2f} s, 95% CI {lo:.2f} to {hi:.2f}")
lo, hi = diff_ci(f["wait"], a["wait"])
print(f"waiting saved (fixed - adaptive) {st.mean(f['wait']) - st.mean(a['wait']):.2f} s, 95% CI {lo:.2f} to {hi:.2f}")
