using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Wall-clock time per tool call, measured in the tool wrapper. Lets a client compare a publish
    /// before and after a change from inside the server, without trusting its own HTTP timings
    /// (which include queueing behind the previous request).
    /// Zero-dependency on purpose so the offline test suite can drive it.
    /// </summary>
    internal static class ToolTimings
    {
        private sealed class Stat
        {
            public long Count;
            public long TotalMs;
            public long MaxMs;
            public long Errors;
        }

        private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>(StringComparer.Ordinal);
        private static readonly object Sync = new object();

        public static void Record(string tool, long ms, bool isError)
        {
            lock (Sync)
            {
                if (!Stats.TryGetValue(tool, out var s)) Stats[tool] = s = new Stat();
                s.Count++;
                s.TotalMs += ms;
                if (ms > s.MaxMs) s.MaxMs = ms;
                if (isError) s.Errors++;
            }
        }

        public static JsonArray Snapshot(bool reset)
        {
            lock (Sync)
            {
                var arr = new JsonArray();
                foreach (var kv in Stats.OrderByDescending(x => x.Value.TotalMs))
                {
                    arr.Add(new JsonObject
                    {
                        ["tool"] = kv.Key,
                        ["count"] = kv.Value.Count,
                        ["totalMs"] = kv.Value.TotalMs,
                        ["avgMs"] = kv.Value.Count == 0 ? 0 : kv.Value.TotalMs / kv.Value.Count,
                        ["maxMs"] = kv.Value.MaxMs,
                        ["errors"] = kv.Value.Errors
                    });
                }
                if (reset) Stats.Clear();
                return arr;
            }
        }
    }
}
