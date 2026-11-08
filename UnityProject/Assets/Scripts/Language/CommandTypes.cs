using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TeamRescue.Language
{
    /// <summary>Structured command produced from a player's utterance.</summary>
    [Serializable]
    public sealed class ParsedCommand
    {
        public string intent = "other";   // fetch | cancel | new_layout | status | clarify | other
        public string[] targets = Array.Empty<string>(); // ordered "blue"/"orange"
        public string reply = "";
        [NonSerialized] public string backend = "";
        [NonSerialized] public float latency;
        [NonSerialized] public string error;

        public List<CellColor> TargetColors()
        {
            var list = new List<CellColor>();
            if (targets == null) return list;
            foreach (var t in targets)
            {
                var s = (t ?? "").Trim().ToLowerInvariant();
                if (s == "blue") list.Add(CellColor.Blue);
                else if (s == "orange") list.Add(CellColor.Orange);
            }
            return list;
        }
    }

    /// <summary>What the language layer is told about the current scene.</summary>
    [Serializable]
    public sealed class SceneFacts
    {
        public string carrying = "nothing";
        public string currentTask = "none (waiting for orders)";
        public float blueToRobot, orangeToRobot, blueToGenerator, orangeToGenerator;
        public string blueSide = "left", orangeSide = "right";

        public static SceneFacts From(RescueBotAgent agent, bool holding)
        {
            var a = agent.arena;
            Vector3 r = agent.transform.localPosition;
            Vector3 g = a.generator.localPosition;
            Vector3 b = a.Cell(CellColor.Blue).transform.localPosition;
            Vector3 o = a.Cell(CellColor.Orange).transform.localPosition;
            if (a.Cell(CellColor.Blue).IsCarried) b = r;
            if (a.Cell(CellColor.Orange).IsCarried) o = r;
            return new SceneFacts
            {
                carrying = agent.Carried == null ? "nothing" : agent.Carried.color.ToString().ToLowerInvariant(),
                currentTask = holding ? "none (waiting for orders)" : $"fetch {agent.Target.ToString().ToLowerInvariant()}",
                blueToRobot = Flat(b - r), orangeToRobot = Flat(o - r),
                blueToGenerator = Flat(b - g), orangeToGenerator = Flat(o - g),
                blueSide = b.x < o.x ? "left" : "right",
                orangeSide = b.x < o.x ? "right" : "left",
            };
        }

        private static float Flat(Vector3 d) => new Vector2(d.x, d.z).magnitude;

        public string ToPromptText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Scene facts (seen from the player's top-down camera):");
            sb.AppendLine($"- Robot is carrying: {carrying}. Current task: {currentTask}.");
            sb.AppendLine($"- BLUE cell (round cylinder): {blueToRobot:0.0} m from the robot, {blueToGenerator:0.0} m from the generator, on the {blueSide} of the other cell.");
            sb.AppendLine($"- ORANGE cell (square box): {orangeToRobot:0.0} m from the robot, {orangeToGenerator:0.0} m from the generator, on the {orangeSide} of the other cell.");
            // Pre-computed relations, so the language model never has to compare numbers itself.
            string closerRobot = blueToRobot <= orangeToRobot ? "blue" : "orange";
            string fartherRobot = closerRobot == "blue" ? "orange" : "blue";
            string closerGen = blueToGenerator <= orangeToGenerator ? "blue" : "orange";
            string fartherGen = closerGen == "blue" ? "orange" : "blue";
            string left = blueSide == "left" ? "blue" : "orange";
            string right = left == "blue" ? "orange" : "blue";
            sb.AppendLine($"Relations: closer to the robot = {closerRobot}; farther from the robot = {fartherRobot}; " +
                          $"closer to the generator = {closerGen}; farther from the generator = {fartherGen}; " +
                          $"left = {left}; right = {right}.");
            return sb.ToString();
        }
    }

    public interface ICommandParser
    {
        string Name { get; }
        /// <summary>history: previous (player, robot) turns, oldest first.</summary>
        System.Collections.IEnumerator Parse(string utterance, SceneFacts facts, List<(string player, string robot)> history, Action<ParsedCommand> done);
    }
}
