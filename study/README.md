# Team Rescue · Human–AI co-op user study kit

A short within-subjects study: each participant plays the co-op game with the trained robot under
three conditions and rates each one. Everything needed is in this folder.

| Condition | Label shown | What the robot does |
|---|---|---|
| Fixed plan | A | Always starts with the blue cell; only reacts after a cell has been taken. |
| Adaptive | B | Infers the participant's goal from their movement (shown as "Robot's guess" bars) and takes the other cell. |
| Language orders | C | Waits for typed orders (local LLM, Chinese / English / French). |

The order of A/B/C is counterbalanced automatically (Latin square from the participant number:
P01 → A B C, P02 → B C A, P03 → C A B, P04 → A B C …).

## Before the session (5 min)

1. Plug in the laptop. Close other heavy programs (the language model runs on the CPU).
2. Double-click `Run-Study.cmd`, type the participant ID (P01, P02 …) and press Enter.
   It starts the local language model server and the game in study mode.
3. Read the participant the script below. Do not explain how the robot decides.

> "You and a rescue robot must restore power. The generator needs both power cells, the blue
> cylinder and the orange box. Move with WASD; walk into a cell to pick it up and walk to the
> generator to install it. Cables flash when live and stun whoever touches them; standing on the
> yellow pad switches them off. You will try three versions of the robot, three rounds each.
> After each version you fill in a short questionnaire. There are no right or wrong answers."

## During the session (≈15 min)

- Practice round (1 round), then three conditions × 3 rounds. The game pauses between
  conditions ("Press SPACE to start").
- After each condition, hand over `questionnaire.md` (one page per condition).
- Write down anything notable the participant says or does (confusion, surprises, strategies).

## Data

- Game log: `Build\Coop\StudyData\<ID>.csv` — one row per round: time, completion, shocks,
  how often the robot lost its target to the participant, how often it switched plan, number of
  typed orders, who installed which cell.
- Questionnaire answers: enter them into `questionnaire_answers.csv` (same participant IDs).
- No names or personal data are collected; use the participant ID only. Ask for verbal consent
  and say they can stop at any time.

## Analysis

```bash
python analyze_study.py
```

It prints per-condition means for the logs and questionnaires. With 3–6 participants, report the
numbers descriptively and quote what people said — do not claim statistical significance.
