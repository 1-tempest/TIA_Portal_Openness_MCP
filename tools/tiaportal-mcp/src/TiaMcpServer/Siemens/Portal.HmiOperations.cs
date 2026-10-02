using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// ApplyHmiOperations: many small HMI edits in one call. A publish used to cost one MCP round
    /// trip (plus a fresh screen/item resolve) per SetAttribute / Bind / script / property check.
    /// Here the screen and items are resolved once per batch, the batch runs under one
    /// TiaPortal.ExclusiveAccess (optionally one Transaction), and every operation reports its own
    /// ok/error without stopping the batch.
    /// </summary>
    public partial class Portal
    {
        public sealed class HmiOperationsJob
        {
            public string Id = "";
            public string State = "queued"; // queued | running | done | failed
            public int Total;
            public int Done;
            public int Failed;
            public DateTime StartedUtc;
            public DateTime? FinishedUtc;
            public string? Error;
            public readonly JsonArray Results = new JsonArray();
            public readonly object Sync = new object();
        }

        private static readonly ConcurrentDictionary<string, HmiOperationsJob> _hmiJobs = new ConcurrentDictionary<string, HmiOperationsJob>();

        /// <summary>
        /// Serializes Openness work between MCP tool calls and background HMI jobs.
        /// Taken by the MCP tool wrapper for every Openness-touching tool and held by a running job.
        /// </summary>
        public static readonly SemaphoreSlim OpennessGate = new SemaphoreSlim(1, 1);

        public static string? RunningHmiJobId
            => _hmiJobs.Values.FirstOrDefault(j => j.State == "queued" || j.State == "running")?.Id;

        /// <summary>Synchronous batch. Caller already holds <see cref="OpennessGate"/>.</summary>
        public JsonObject ApplyHmiOperations(string hmiSoftwarePath, JsonArray operations, bool useExclusiveAccess = true, bool useTransaction = false)
        {
            var job = new HmiOperationsJob { Id = "sync", Total = operations.Count, StartedUtc = DateTime.UtcNow, State = "running" };
            RunHmiOperations(job, hmiSoftwarePath, operations, useExclusiveAccess, useTransaction);
            return HmiJobToJson(job, includeResults: true, offset: 0, limit: int.MaxValue);
        }

        /// <summary>
        /// Starts the batch on a background thread and returns at once. The thread takes
        /// <see cref="OpennessGate"/> for the whole run; poll <see cref="GetHmiOperationsJob"/>.
        /// </summary>
        public JsonObject StartHmiOperationsJob(string hmiSoftwarePath, JsonArray operations, bool useExclusiveAccess = true, bool useTransaction = false)
        {
            var running = RunningHmiJobId;
            if (running != null)
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    $"HMI operations job '{running}' is still running. Poll GetHmiOperationsJob until it is done.");
            }

            var job = new HmiOperationsJob
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Total = operations.Count,
                StartedUtc = DateTime.UtcNow
            };
            _hmiJobs[job.Id] = job;
            PruneHmiJobs();

            var thread = new Thread(() =>
            {
                OpennessGate.Wait();
                try
                {
                    lock (job.Sync) job.State = "running";
                    RunHmiOperations(job, hmiSoftwarePath, operations, useExclusiveAccess, useTransaction);
                }
                catch (Exception ex)
                {
                    lock (job.Sync)
                    {
                        job.State = "failed";
                        job.Error = ex.Message;
                        job.FinishedUtc = DateTime.UtcNow;
                    }
                }
                finally
                {
                    OpennessGate.Release();
                }
            })
            { IsBackground = true, Name = "HmiOperationsJob-" + job.Id };
            thread.Start();

            return HmiJobToJson(job, includeResults: false, offset: 0, limit: 0);
        }

        public JsonObject GetHmiOperationsJob(string jobId, bool includeResults = true, int offset = 0, int limit = 500)
        {
            if (!_hmiJobs.TryGetValue(jobId ?? "", out var job))
            {
                throw new PortalException(PortalErrorCode.NotFound, $"No HMI operations job '{jobId}'. Jobs are kept in memory only (last 20).");
            }
            return HmiJobToJson(job, includeResults, offset, limit);
        }

        private static void PruneHmiJobs()
        {
            foreach (var old in _hmiJobs.Values
                         .Where(j => j.State == "done" || j.State == "failed")
                         .OrderByDescending(j => j.StartedUtc)
                         .Skip(20)
                         .ToList())
            {
                _hmiJobs.TryRemove(old.Id, out _);
            }
        }

        private static JsonObject HmiJobToJson(HmiOperationsJob job, bool includeResults, int offset, int limit)
        {
            lock (job.Sync)
            {
                var end = job.FinishedUtc ?? DateTime.UtcNow;
                var o = new JsonObject
                {
                    ["jobId"] = job.Id,
                    ["state"] = job.State,
                    ["total"] = job.Total,
                    ["done"] = job.Done,
                    ["ok"] = job.Done - job.Failed,
                    ["failed"] = job.Failed,
                    ["elapsedMs"] = (long)(end - job.StartedUtc).TotalMilliseconds
                };
                if (job.Error != null) o["error"] = job.Error;
                if (includeResults)
                {
                    var page = new JsonArray();
                    foreach (var r in job.Results.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit)))
                    {
                        page.Add(r?.DeepClone());
                    }
                    o["offset"] = Math.Max(0, offset);
                    o["results"] = page;
                }
                return o;
            }
        }

        private void RunHmiOperations(HmiOperationsJob job, string hmiSoftwarePath, JsonArray operations, bool useExclusiveAccess, bool useTransaction)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open.");
            var hmi = ResolveHmiSoftwareOrThrow(hmiSoftwarePath);

            var screens = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var items = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            ExclusiveAccess? access = null;
            Transaction? transaction = null;
            try
            {
                if (useExclusiveAccess && _portal != null)
                {
                    access = _portal.ExclusiveAccess($"MCP: {operations.Count} HMI operations");
                    if (useTransaction && _project is ITransactionSupport ts)
                    {
                        transaction = access.Transaction(ts, $"MCP: {operations.Count} HMI operations");
                    }
                }

                for (int i = 0; i < operations.Count; i++)
                {
                    var op = operations[i] as JsonObject;
                    var sw = Stopwatch.StartNew();
                    var result = new JsonObject { ["i"] = i };
                    try
                    {
                        if (op == null) throw new InvalidOperationException("operation must be a JSON object");
                        var kind = Str(op, "op") ?? throw new InvalidOperationException("'op' is required");
                        result["op"] = kind;
                        var value = RunHmiOperation(hmi, kind, op, screens, items, result);
                        if (value != null) result["value"] = value;
                        result["ok"] = true;
                    }
                    catch (Exception ex)
                    {
                        var real = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                        result["ok"] = false;
                        result["error"] = real.Message;
                    }
                    result["ms"] = sw.ElapsedMilliseconds;

                    lock (job.Sync)
                    {
                        job.Results.Add(result);
                        job.Done++;
                        if (result["ok"]?.GetValue<bool>() != true) job.Failed++;
                    }

                    // A killed Portal makes every later operation fail the same way; stop there.
                    if (result["ok"]?.GetValue<bool>() != true && IsProjectNull()) break;
                }

                transaction?.CommitOnDispose();
            }
            finally
            {
                transaction?.Dispose();
                access?.Dispose();
                lock (job.Sync)
                {
                    if (job.State == "running") job.State = "done";
                    job.FinishedUtc = DateTime.UtcNow;
                }
                _logger?.LogInformation($"ApplyHmiOperations {job.Id}: {job.Done}/{job.Total} ops, {job.Failed} failed, {(long)((job.FinishedUtc ?? DateTime.UtcNow) - job.StartedUtc).TotalMilliseconds} ms");
            }
        }

        private JsonNode? RunHmiOperation(object hmi, string kind, JsonObject op, Dictionary<string, object> screens, Dictionary<string, object> items, JsonObject result)
        {
            var screenName = Str(op, "screen");
            var itemName = Str(op, "item");
            var target = ResolveOperationTarget(hmi, op, screens, items, result);

            switch (kind.Trim().ToLowerInvariant())
            {
                case "setattribute":
                {
                    var name = Str(op, "name") ?? throw new InvalidOperationException("'name' is required");
                    var r = InvokeOnInstance(target, "HmiOperation", Describe(result), "SetAttribute",
                        new JsonArray(name, op["value"]?.DeepClone()), allowWrite: true);
                    if (r.Message != "OK") throw new InvalidOperationException(r.Message);
                    return null;
                }

                case "getattribute":
                {
                    var name = Str(op, "name") ?? throw new InvalidOperationException("'name' is required");
                    var r = InvokeOnInstance(target, "HmiOperation", Describe(result), "GetAttribute", new JsonArray(name), allowWrite: false);
                    if (r.Message != "OK") throw new InvalidOperationException(r.Message);
                    return FormatOperationValue(r.Value);
                }

                case "setproperty":
                {
                    var name = Str(op, "name") ?? throw new InvalidOperationException("'name' is required");
                    var r = SetPropertyOnInstance(target, "HmiOperation", Describe(result), name, JsonToPlain(op["value"]));
                    return FormatOperationValue(r.Value);
                }

                case "getproperty":
                {
                    var name = Str(op, "name") ?? throw new InvalidOperationException("'name' is required");
                    return FormatOperationValue(GetPropertyPathValue(target, name));
                }

                case "bind":
                {
                    var meta = new JsonObject();
                    BindTagDynamizationOnItem(target, itemName ?? Describe(result),
                        Str(op, "property") ?? throw new InvalidOperationException("'property' is required"),
                        Str(op, "tag") ?? throw new InvalidOperationException("'tag' is required"),
                        Str(op, "dataType") ?? "Bool",
                        Str(op, "plcTag") ?? "",
                        Str(op, "address") ?? "",
                        meta);
                    return meta["action"]?.DeepClone();
                }

                case "setscript":
                {
                    var evt = Str(op, "event") ?? throw new InvalidOperationException("'event' is required");
                    var handler = GetOrCreateEventHandlerOrThrow(target, itemName ?? Describe(result), evt);
                    var asyncScript = op["async"] is JsonValue av && av.TryGetValue<bool>(out var b) && b;
                    WriteEventScript(handler, itemName ?? Describe(result), evt, Str(op, "code") ?? "", Str(op, "globalCode") ?? "", asyncScript, syntaxCheck: false, new JsonObject());
                    return null;
                }

                case "delete":
                {
                    var del = target.GetType().GetMethod("Delete", Type.EmptyTypes)
                        ?? throw new InvalidOperationException($"{target.GetType().Name} has no Delete()");
                    del.Invoke(target, null);
                    if (screenName != null && itemName != null) items.Remove(screenName + "\u0001" + itemName);
                    return null;
                }

                case "invoke":
                {
                    var method = Str(op, "method") ?? throw new InvalidOperationException("'method' is required");
                    var allowWrite = op["allowWrite"] is JsonValue aw && aw.TryGetValue<bool>(out var w) && w;
                    var r = InvokeOnInstance(target, "HmiOperation", Describe(result), method, op["args"]?.DeepClone() as JsonArray, allowWrite);
                    if (r.Message != "OK") throw new InvalidOperationException(r.Message);
                    return FormatOperationValue(r.Value);
                }

                default:
                    throw new InvalidOperationException($"Unknown op '{kind}'. Use SetAttribute|GetAttribute|SetProperty|GetProperty|Bind|SetScript|Delete|Invoke.");
            }
        }

        /// <summary>
        /// Target = screen item ({screen, item}), screen ({screen}) or any HmiPath under the HMI
        /// software ({path}); optional {sub} walks further (HmiPath rules), e.g. "Font".
        /// </summary>
        private object ResolveOperationTarget(object hmi, JsonObject op, Dictionary<string, object> screens, Dictionary<string, object> items, JsonObject result)
        {
            var screenName = Str(op, "screen");
            var itemName = Str(op, "item");
            var path = Str(op, "path");
            object? target;

            if (!string.IsNullOrWhiteSpace(path))
            {
                result["path"] = path;
                target = WalkObjectPath(hmi, SplitObjectPath(path));
                if (target == null) throw new InvalidOperationException($"path '{path}' not found under the HMI software");
            }
            else if (!string.IsNullOrWhiteSpace(screenName))
            {
                result["screen"] = screenName;
                if (!screens.TryGetValue(screenName!, out var screen))
                {
                    screen = TryFindScreenByName(hmi, screenName!) ?? throw new InvalidOperationException($"screen '{screenName}' not found");
                    screens[screenName!] = screen;
                }
                target = screen;

                if (!string.IsNullOrWhiteSpace(itemName))
                {
                    result["item"] = itemName;
                    var key = screenName + "\u0001" + itemName;
                    if (!items.TryGetValue(key, out var item))
                    {
                        var coll = TryGetPropertyValue(screen, "ScreenItems") ?? throw new InvalidOperationException($"ScreenItems not found on '{screenName}'");
                        item = FindExistingByName(coll, itemName!) ?? throw new InvalidOperationException($"item '{itemName}' not found on screen '{screenName}'");
                        items[key] = item;
                    }
                    target = item;
                }
            }
            else
            {
                throw new InvalidOperationException("each operation needs 'screen' (+ 'item') or 'path'");
            }

            var sub = Str(op, "sub");
            if (!string.IsNullOrWhiteSpace(sub))
            {
                result["sub"] = sub;
                target = WalkObjectPath(target, SplitObjectPath(sub)) ?? throw new InvalidOperationException($"sub path '{sub}' not found");
            }
            return target;
        }

        private static string Describe(JsonObject result)
            => string.Join(":", new[] { result["screen"], result["item"], result["path"], result["sub"] }
                .Where(x => x != null).Select(x => x!.ToString()));

        private static string? Str(JsonObject o, string key)
        {
            foreach (var kv in o)
            {
                if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) || kv.Value == null) continue;
                return kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : kv.Value.ToJsonString();
            }
            return null;
        }

        private static object? JsonToPlain(JsonNode? n)
        {
            if (n == null) return null;
            if (n is JsonArray arr) return arr.Select(JsonToPlain).ToList();
            if (n is JsonValue jv)
            {
                if (jv.TryGetValue<string>(out var s)) return s;
                if (jv.TryGetValue<bool>(out var b)) return b;
                if (jv.TryGetValue<long>(out var l)) return l;
                if (jv.TryGetValue<double>(out var d)) return d;
            }
            return n.ToJsonString();
        }

        private static JsonNode? FormatOperationValue(object? v)
        {
            if (v == null) return null;
            if (v is string s) return s;
            if (v is bool b) return b;
            if (v is int || v is long || v is short || v is byte || v is uint || v is ushort) return Convert.ToInt64(v);
            if (v is float || v is double || v is decimal) return Convert.ToDouble(v);
            if (v is System.Drawing.Color c) return "0x" + c.ToArgb().ToString("X8");
            if (v is Dictionary<string, string?> map)
            {
                var o = new JsonObject();
                foreach (var kv in map) o[kv.Key] = kv.Value;
                return o;
            }
            if (v is IEnumerable<string> strings) return new JsonArray(strings.Select(x => (JsonNode?)x).ToArray());
            if (IsEngineeringObject(v)) return FormatOperationValue(DescribeEngineeringObjectRef(v));
            return v.ToString();
        }
    }
}
