"""Summarise the co-op user study: game logs + questionnaires, per condition (descriptive only)."""
import csv
import glob
import os
import statistics as st
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
LOGS = glob.glob(os.path.join(HERE, "..", "Build", "Coop", "StudyData", "P*.csv"))
NAMES = {"A": "Fixed plan", "B": "Adaptive", "C": "Language orders"}


def mean(xs):
    xs = [x for x in xs if x is not None]
    return f"{st.mean(xs):.2f}" if xs else "-"


rounds = defaultdict(list)
for path in LOGS:
    with open(path, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            if r["condition"] in NAMES:
                rounds[r["condition"]].append(r)

print(f"Game logs: {len(LOGS)} participant file(s)\n")
print(f"{'condition':18} {'rounds':>6} {'done%':>6} {'secs':>6} {'lost':>5} {'switch':>6} {'orders':>6} {'shocks':>6}"
      f" {'wait':>6} {'askE':>5} {'botAsk':>6} {'botOpen':>7} {'youOpen':>7}")
for c in "ABC":
    rs = rounds.get(c, [])
    if not rs:
        continue
    f = lambda k: [float(r[k]) for r in rs if r.get(k, "") != ""]
    print(f"{NAMES[c]:18} {len(rs):6d} {100 * st.mean(f('completed')):6.0f} {mean(f('seconds')):>6} "
          f"{mean(f('robot_lost_target')):>5} {mean(f('robot_switches')):>6} {mean(f('commands')):>6} {mean(f('human_shocks')):>6}"
          f" {mean(f('human_fence_wait')):>6} {mean(f('human_help_requests')):>5} {mean(f('robot_help_requests')):>6}"
          f" {mean(f('robot_assists')):>7} {mean(f('human_assists')):>7}")
print("wait = seconds the participant stood at the live fence with a cell; askE = times they asked the robot for the"
      " breaker; botAsk = times the robot asked them; botOpen / youOpen = fence openings by robot / participant.")

q = defaultdict(lambda: defaultdict(list))
qpath = os.path.join(HERE, "questionnaire_answers.csv")
with open(qpath, newline="", encoding="utf-8") as f:
    for r in csv.DictReader(f):
        for k in ["Q1", "Q2", "Q3", "Q4", "Q5", "Q6", "Q7", "rank"]:
            if r.get(k, "").strip():
                q[r["condition"]][k].append(float(r[k]))

if q:
    print("\nQuestionnaire means (1-7; Q5 and Q6 lower is better; rank 1 = preferred)")
    print(f"{'condition':18} " + " ".join(f"{k:>5}" for k in ["Q1", "Q2", "Q3", "Q4", "Q5", "Q6", "Q7", "rank"]))
    for c in "ABC":
        if c in q:
            print(f"{NAMES[c]:18} " + " ".join(f"{mean(q[c][k]):>5}" for k in ["Q1", "Q2", "Q3", "Q4", "Q5", "Q6", "Q7", "rank"]))
else:
    print("\nNo questionnaire answers entered yet.")
