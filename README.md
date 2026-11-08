# Team Rescue: a human–AI rescue robot in Unity ML-Agents

A Unity 6 game in which a **reinforcement-learning robot** (PPO, Unity ML-Agents 4.1) restores power in a blacked-out station: it must fetch the power cell that a human asks for and avoid exposed cables that switch on and off. On top of the learned policy there are two interaction layers:

1. **Natural-language orders.** A local LLM (Qwen2.5-3B through Ollama, no cloud, CPU only) turns Chinese, English or French sentences into a structured goal. Ordinary code then resolves that goal against the scene.
2. **Human–AI teamwork.** The player and the robot share the job. An adaptive teammate infers which cell the player is heading for from their movement, shows its belief on screen and takes the other cell.

Built for Lab 3 of an HCI course ("a Unity application using an ML agent"). The full process journal, including every failed training run and how it was fixed, is on the blog: [English](https://icey000.github.io/coursework/hci/lab-ml-agent/) · [中文](https://icey000.github.io/zh/coursework/hci/lab-ml-agent/).

![The robot carrying the orange cell](results/shots_final/06-orange-moving.png)

## How the pieces fit

```
 typed order ──► local LLM (parse only) ──► {intent, refs[{by, negated}]} ──► grounding code ──► target cell
                                                                                                │
 player movement ──► goal inference (adaptive teammate) ─────────────────────────────────────────┤
                                                                                                ▼
                                       PPO policy (ONNX, runs in Unity) ◄── 25 observations: target one-hot,
                                                 │                          cell/generator positions, cable state
                                                 ▼
                                       continuous movement (2 actions)
```

The language model **never controls the robot**. It only translates words into a goal (which cell, or stop/cancel). The movement, cable avoidance and pick-up/delivery are done by the trained neural network, which knows nothing about language. A weaker or stronger LLM therefore changes how well orders are *understood*, not how well the robot *moves*.

## Results

| What was measured | Result |
|---|---|
| Learned policy vs. scripted straight-line baseline, 300 random layouts, 2 cables | Same success rate (97.3 % vs 97.3 %), **78 % fewer shocks** (0.33 vs 1.51 per episode), about 1.9 s slower per delivery |
| Language layer, 20 held-out commands evaluated once | **85 %** correct with Qwen2.5-3B + grounding vs **55 %** for keyword rules; about 5.8 s per command on a laptop CPU; fails on ambiguous orders that need a clarifying question |
| Co-op, 150 rounds per mode against a simulated partner | Rounds in which human and robot went for the same cell: **48.0 % (fixed plan) → 3.3 % (adaptive)**; completion 98.7 % in both; time difference not significant (0.11 s, 95 % CI −0.73 to 0.84) |

The co-op numbers come from a *simulated* partner. A within-subjects user study (practice + 3 conditions × 3 rounds, Latin-square order, 6-item questionnaire) is prepared in [`study/`](study/) and has **not been run yet**.

The first human play-test found a bug the simulation had missed: once the robot had installed one cell, it waited for the player whenever the player was closer to the last cell, even if the player was standing still on the breaker pad to help it. The robot now waits only while the player is actually walking towards the cell (with a 1.5 s grace period), and takes over when the player holds the breaker or is shocked twice. The table shows the results after this fix. Before the fix they were 44.0 % → 0.7 %; run-to-run variation is several percentage points.

## Repository layout

| Path | Contents |
|---|---|
| `UnityProject/Assets/Scripts/` | Agent (`RescueBotAgent.cs`), arena, cables, shock feedback, demo HUD and evaluation modes |
| `UnityProject/Assets/Scripts/Language/` | LLM client (`OllamaParser.cs`), keyword baseline, grounding, chat UI |
| `UnityProject/Assets/Scripts/Coop/` | Player avatar, simulated partner, co-op coordinator with goal inference and study logging |
| `UnityProject/Assets/Editor/ArenaBuilder.cs` | Generates all scenes and builds every player from the command line |
| `UnityProject/Assets/TeamRescue/Models/` | Trained policies (`RescueBot_v3ft.onnx` is the final one) |
| `UnityProject/Assets/StreamingAssets/command_prompt.txt` | The LLM prompt |
| `training/` | ML-Agents configs: curriculum PPO run (4 M steps) and fine-tune (2 M steps) |
| `language/` | 45 development commands and 20 held-out commands |
| `results/` | Evaluation JSON/CSV, training curves, screenshots |
| `study/` | User-study protocol, questionnaire, launcher and analysis script |
| `tools/` | Scalar export, plotting and simulation analysis scripts |

## Running it

Requirements: Windows, Unity 6000.6.4f1, and optionally [Ollama](https://ollama.com) with `qwen2.5:3b` for typed orders (`ollama pull qwen2.5:3b`, about 2 GB, runs on CPU). Without Ollama the game falls back to keyword rules.

Build the players (Unity Hub must be running for the licence):

```bat
set TEAMRESCUE_MODEL=Assets/TeamRescue/Models/RescueBot_v3ft.onnx
Unity.exe -batchmode -quit -projectPath UnityProject -executeMethod TeamRescue.EditorTools.ArenaBuilder.BatchBuildDemoPlayer
Unity.exe -batchmode -quit -projectPath UnityProject -executeMethod TeamRescue.EditorTools.ArenaBuilder.BatchBuildCoopPlayer
```

Then run `Play-TeamRescue.cmd` (robot follows your orders) or `Play-Coop.cmd` (play together; F1 fixed plan, F2 adaptive, F3 typed orders, WASD to move, the yellow pad switches the cables off).

Retraining (Python 3.10, `mlagents==1.1.0`, CPU is enough; about 2 k steps/s with 4 parallel environments):

```bat
mlagents-learn training/rescuebot_ppo.yaml --run-id=rescuebot_v3 --env=Build/Train/TeamRescueTrain.exe --num-envs=4 --no-graphics
```

Evaluation flags of the demo player: `-eval N out.json [-baseline]`. The co-op player has `-coopsim N out.csv -mode fixed|adaptive`, and `python tools/analyze_sim.py` summarises the simulation runs.

## Known limitations

- The robot does not yield to the player's body: its sensors were trained without a human in the arena.
- The 3B model takes about 4–7 s per order on CPU and cannot yet ask a clarifying question when an order is ambiguous.
- One training seed; the simulated partner is not a substitute for real participants.
