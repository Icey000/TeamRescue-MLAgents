"""Plot the v3 curriculum training curves as two small multiples sharing the x axis."""
import csv, sys
sys.path.insert(0, r"D:\Coursework\HCI\Lab3-TeamRescue\tools\pylib")
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

src, out = sys.argv[1], sys.argv[2]
lessons = [(0, "No cables"), (1_040_000, "1 cable"), (1_600_000, "2 cables")]
SURFACE, INK, MUTED, GRID, SERIES = "#fcfcfb", "#0b0b0b", "#52514e", "#e4e3df", "#2a78d6"

steps, succ, shock = [], [], []
with open(src) as f:
    for r in csv.DictReader(f):
        if r["Rescue/SuccessRate"] == "":
            continue
        steps.append(int(r["step"]) / 1e6)
        succ.append(float(r["Rescue/SuccessRate"]) * 100)
        shock.append(float(r["Rescue/HazardHitsPerEpisode"] or 0))

def smooth(v, k=5):
    return [sum(v[max(0, i - k + 1):i + 1]) / len(v[max(0, i - k + 1):i + 1]) for i in range(len(v))]

plt.rcParams.update({"font.family": "DejaVu Sans", "font.size": 11})
fig, axes = plt.subplots(2, 1, figsize=(9, 6.2), sharex=True, facecolor=SURFACE)
panels = [(axes[0], smooth(succ), "Success rate (%)", (0, 100)),
          (axes[1], smooth(shock), "Shocks per episode", (0, None))]
for ax, ys, title, ylim in panels:
    ax.set_facecolor(SURFACE)
    ax.plot(steps, ys, color=SERIES, linewidth=2, solid_capstyle="round")
    ax.set_title(title, loc="left", color=INK, fontsize=12, fontweight="bold")
    ax.grid(axis="y", color=GRID, linewidth=0.8)
    ax.set_ylim(*ylim)
    for s in ("top", "right"):
        ax.spines[s].set_visible(False)
    for s in ("left", "bottom"):
        ax.spines[s].set_color(GRID)
    ax.tick_params(colors=MUTED)
    for x, name in lessons:
        if x > 0:
            ax.axvline(x / 1e6, color=MUTED, linewidth=1, linestyle=(0, (3, 3)))
for x, name in lessons:
    axes[0].text(x / 1e6 + 0.04, 6, name, color=MUTED, fontsize=10)
axes[1].set_xlabel("Training steps (millions)", color=MUTED)
fig.suptitle("Rescue robot v3 · PPO curriculum training (5-point moving average)",
             x=0.01, ha="left", color=INK, fontsize=13)
fig.tight_layout()
fig.savefig(out, dpi=150, facecolor=SURFACE)
print(out)
