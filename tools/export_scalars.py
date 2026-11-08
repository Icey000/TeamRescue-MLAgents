"""Export ML-Agents TensorBoard scalars of one run to CSV (run with the mlagents env)."""
import csv, glob, sys
from tensorboard.backend.event_processing.event_accumulator import EventAccumulator

run, out = sys.argv[1], sys.argv[2]
tags = ["Rescue/SuccessRate", "Rescue/HazardHitsPerEpisode", "Rescue/EpisodeSeconds",
        "Rescue/WrongCellTouchRate", "Environment/Cumulative Reward"]
ea = EventAccumulator(glob.glob(rf"{run}\RescueBot\events*")[0], size_guidance={"scalars": 0})
ea.Reload()
rows = {}
for t in tags:
    if t not in ea.Tags()["scalars"]:
        continue
    for e in ea.Scalars(t):
        rows.setdefault(e.step, {})[t] = e.value
with open(out, "w", newline="") as f:
    w = csv.writer(f)
    w.writerow(["step"] + tags)
    for s in sorted(rows):
        w.writerow([s] + [rows[s].get(t, "") for t in tags])
print(out, len(rows))
