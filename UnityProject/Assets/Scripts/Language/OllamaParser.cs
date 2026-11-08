using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace TeamRescue.Language
{
    /// <summary>
    /// Sends the utterance plus scene facts to a local Ollama model and asks for JSON that
    /// follows a fixed schema (Ollama structured outputs). Falls back to the rule parser
    /// if the server is unreachable or the answer is not valid.
    /// </summary>
    public sealed class OllamaParser : ICommandParser
    {
        public string Url = "http://127.0.0.1:11434/api/chat";
        public string Model = "qwen2.5:3b";
        public int TimeoutSeconds = 30;
        public string Name => $"ollama:{Model}";

        private string systemPrompt;

        private const string Schema =
            "{\"type\":\"object\",\"properties\":{" +
            "\"intent\":{\"type\":\"string\",\"enum\":[\"fetch\",\"cancel\",\"new_layout\",\"status\",\"other\"]}," +
            "\"refs\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"by\":{\"type\":\"string\",\"enum\":[\"blue\",\"orange\",\"round\",\"square\",\"closer_to_robot\",\"farther_from_robot\",\"closer_to_generator\",\"farther_from_generator\",\"left\",\"right\",\"unspecified\"]}," +
                "\"negated\":{\"type\":\"boolean\"}},\"required\":[\"by\",\"negated\"]}}," +
            "\"reply\":{\"type\":\"string\"}}," +
            "\"required\":[\"intent\",\"refs\",\"reply\"]}";

        [Serializable] private class Msg { public string role; public string content; }
        [Serializable] private class Resp { public Msg message; }

        private string SystemPrompt
        {
            get
            {
                if (systemPrompt != null) return systemPrompt;
                string path = Path.Combine(Application.streamingAssetsPath, "command_prompt.txt");
                systemPrompt = File.Exists(path) ? File.ReadAllText(path) : "Convert the instruction to JSON.";
                return systemPrompt;
            }
        }

        public IEnumerator Parse(string utterance, SceneFacts facts, List<(string player, string robot)> history, Action<ParsedCommand> done)
        {
            var sb = new StringBuilder();
            sb.Append("{\"model\":").Append(Q(Model))
              .Append(",\"stream\":false,\"keep_alive\":\"30m\",\"options\":{\"temperature\":0},\"format\":").Append(Schema)
              .Append(",\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":").Append(Q(SystemPrompt)).Append('}');
            if (history != null)
                foreach (var (p, r) in history)
                {
                    sb.Append(",{\"role\":\"user\",\"content\":").Append(Q("Player: " + p)).Append('}');
                    sb.Append(",{\"role\":\"assistant\",\"content\":").Append(Q(r)).Append('}');
                }
            sb.Append(",{\"role\":\"user\",\"content\":").Append(Q(facts.ToPromptText() + "\nPlayer: " + utterance)).Append("}]}");

            float t0 = Time.realtimeSinceStartup;
            using var req = new UnityWebRequest(Url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString())),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = TimeoutSeconds,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            yield return req.SendWebRequest();
            float latency = Time.realtimeSinceStartup - t0;

            ParsedCommand cmd = null;
            string error = null;
            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var resp = JsonUtility.FromJson<Resp>(req.downloadHandler.text);
                    var parse = JsonUtility.FromJson<SemanticParse>(resp.message.content);
                    if (parse == null || string.IsNullOrEmpty(parse.intent)) error = "empty answer";
                    else cmd = Grounding.Ground(parse, facts, utterance);
                }
                catch (Exception e) { error = "bad JSON: " + e.Message; }
            }
            else error = req.error;

            if (cmd == null)
            {
                cmd = RuleParser.ParseNow(utterance);
                cmd.backend = "rules (LLM unavailable)";
                cmd.error = error;
            }
            else cmd.backend = Name;
            cmd.latency = latency;
            done(cmd);
        }

        private static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c); else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
