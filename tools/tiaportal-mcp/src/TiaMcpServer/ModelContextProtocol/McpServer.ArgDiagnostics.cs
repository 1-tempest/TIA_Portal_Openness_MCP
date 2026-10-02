using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    // ───────────────────────────────────────────────────────────────────────────
    //  参数诊断的接线层 —— 判定逻辑在零依赖的 ArgDiagnostics.cs（那份能单测）。
    //
    //  实测的问题：直接调用一个已注册工具时少传/写错参数名，调用方拿到的全部信息就是一句
    //        An error occurred invoking 'GetSoftwareTree'.
    //  没说少了什么，也没说正确的参数叫什么。SDK 在参数绑定阶段就失败，工具方法体
    //  根本没执行，所以方法内部的任何校验都来不及。对 AI 客户端等于什么都没说，
    //  只能靠反复试错猜参数名。
    //
    //  为什么包在注册处而不是往每个工具里加校验：
    //  一个个加，漏掉一个就是一次零信息失败，而且新增工具必然会忘。包在注册处是
    //  **结构性**的 —— 工具进不了工具表就到不了模型手里，进了就必然过这一层。
    //
    //  保守原则：InputSchema 读不出来就**不做任何判断**直接放行。
    //  宁可漏掉一次诊断，也不能把一个本来能跑的调用拦下来。
    // ───────────────────────────────────────────────────────────────────────────
    public static partial class McpServer
    {
        /// <summary>注册工具的**唯一**入口：参数诊断在最外层，大响应护栏在里层。
        /// 每一处把工具交给 MCP 服务器的地方都必须走这里 —— 只接一半的层，
        /// 就是后加的注册点静默丢掉另一半的由来。
        /// 诊断放最外层是因为它必须在参数绑定之前拦住调用（副作用一点都不许发生）。</summary>
        public static IList<McpServerTool> WrapTools(IList<McpServerTool> tools) =>
            WrapWithArgDiagnostics(WrapWithResponseGuard(tools));

        /// <summary>逐个包装，让参数错误能自己把话说清楚。</summary>
        public static IList<McpServerTool> WrapWithArgDiagnostics(IList<McpServerTool> tools)
        {
            if (tools == null) return new List<McpServerTool>();
            var outList = new List<McpServerTool>(tools.Count);
            foreach (var t in tools)
            {
                if (t == null) continue;
                outList.Add(new ArgDiagnosticTool(t));
            }
            return outList;
        }
    }

    internal sealed class ArgDiagnosticTool : McpServerTool
    {
        private readonly McpServerTool _inner;

        public ArgDiagnosticTool(McpServerTool inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        // 协议层看到的仍是原工具的完整描述：这层只在出错时说话，不改工具的对外形状。
        public override Tool ProtocolTool => _inner.ProtocolTool;

        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var tool = ProtocolTool;
            var known = new List<string>();
            var required = new List<string>();
            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (ReadSchema(tool, known, required, types))
            {
                var supplied = new List<string>();
                var args = request.Params?.Arguments;
                if (args != null)
                    foreach (var kv in args)
                        if (!IsProtocolField(kv.Key))
                            supplied.Add(kv.Key);

                // 无参工具要单独说一句。ArgDiagnostics.Check 把「known 为空」定义成
                // 「schema 读不出来 → 不做判断」（那条哨兵是对的：判不了就别拦）。
                // 但**无参工具的 schema 是读得出来的**，它只是没有参数 —— 两件事被同一个
                // 空集合表示，于是无参工具（GetState / Connect / Disconnect / SaveProject /
                // CloseProject …）身上这层保护会整批失效：喂一个不存在的参数不但不拦，
                // 还照常执行（SaveProject 会落盘）。
                // 判断留在这里而不是改 Check：Check 是零依赖单测件，它那条语义有哨兵钉着。
                if (known.Count == 0)
                {
                    if (supplied.Count > 0)
                        return TextError((tool?.Name ?? "(tool)")
                            + " takes no arguments, but got: " + string.Join(", ", supplied)
                            + ". They would have been SILENTLY IGNORED (nothing was executed).");
                }
                else
                {
                    string problem = ArgDiagnostics.Check(tool?.Name ?? "(tool)", known, required, supplied, types);
                    if (problem.Length > 0)
                        return TextError(problem);
                }
            }

            // 参数没问题 → 原样转交，返回内部工具的结果本身（不改写、不重新包装）。
            // 外面包一层：Openness 闸门（后台 HMI 作业运行时别并发碰 TIA）+ 每个工具的耗时统计。
            var name = tool?.Name ?? "(tool)";
            var gated = !IsGateExempt(name, request.Params?.Arguments);
            if (gated)
            {
                // 没有后台作业时，闸门只是把调用排成串行（HTTP 本来就是串行的）。
                // 有后台作业时不能干等：HTTP 那边持着请求锁，等下去连轮询作业进度的请求都进不来。
                var jobId = TiaMcpServer.Siemens.Portal.RunningHmiJobId;
                var wait = jobId == null ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(2);
                if (!await TiaMcpServer.Siemens.Portal.OpennessGate.WaitAsync(wait, cancellationToken).ConfigureAwait(false))
                {
                    return TextError($"TIA is busy with HMI operations job '{jobId}'. Poll GetHmiOperationsJob until state=done, then retry {name}.");
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isError = true;
            System.Text.Json.Nodes.JsonObject? recovery = null;
            try
            {
                // TIA 进程挂了就先恢复（重连 + 重开上次的工程），再跑这次调用；
                // 恢复经过写进这次响应的 meta.recovery。Connect/Disconnect 是用户主动的，不插手。
                if (gated && !NoRecovery.Contains(name))
                {
                    try { recovery = McpServer.Portal.EnsureAliveOrRecover(); }
                    catch (Exception rex) { McpServer.Logger?.LogWarning(rex, "TIA crash recovery failed"); }
                }

                CallToolResult result;
                try
                {
                    result = await _inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (global::ModelContextProtocol.McpException mex) when (recovery != null)
                {
                    throw new global::ModelContextProtocol.McpException(mex.Message + " [TIA crashed before this call and was recovered: " + recovery.ToJsonString() + "]", mex, mex.ErrorCode);
                }
                isError = result?.IsError == true;
                if (recovery != null && result != null) AttachRecovery(result, recovery);
                return result!;
            }
            finally
            {
                if (gated)
                {
                    try { McpServer.Portal.NoteSessionState(); } catch { }
                    TiaMcpServer.Siemens.Portal.OpennessGate.Release();
                }
                ToolTimings.Record(name, sw.ElapsedMilliseconds, isError);
                McpServer.Logger?.LogInformation($"tool {name}: {sw.ElapsedMilliseconds} ms{(isError ? " (error)" : "")}");
            }
        }

        private static readonly HashSet<string> NoRecovery = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connect", "ConnectIsolated", "Disconnect",
        };

        /// <summary>
        /// Puts the recovery report into the response's meta ({"message","meta"} envelope); if the
        /// text is not that envelope, appends it as an extra text block instead.
        /// </summary>
        private static void AttachRecovery(CallToolResult result, System.Text.Json.Nodes.JsonObject recovery)
        {
            if (result.Content != null && result.Content.Count > 0 && result.Content[0] is TextContentBlock text)
            {
                try
                {
                    if (System.Text.Json.Nodes.JsonNode.Parse(text.Text ?? "") is System.Text.Json.Nodes.JsonObject obj)
                    {
                        if (obj["meta"] is not System.Text.Json.Nodes.JsonObject meta)
                        {
                            meta = new System.Text.Json.Nodes.JsonObject();
                            obj["meta"] = meta;
                        }
                        meta["recovery"] = recovery.DeepClone();
                        text.Text = obj.ToJsonString();
                        return;
                    }
                }
                catch
                {
                    // not JSON: fall through
                }
            }
            result.Content ??= new List<ContentBlock>();
            result.Content.Add(new TextContentBlock { Text = "TIA recovery: " + recovery.ToJsonString() });
        }

        /// <summary>不碰 TIA 的工具，作业运行期间也必须答得上来。</summary>
        private static readonly HashSet<string> GateExempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GetHmiOperationsJob", "GetToolTimings", "FindTools",
            "GetExport", "ListExports", "DeleteExport", "ClearExports",
            "ListWorkFiles", "ReadWorkFile", "WriteWorkFile", "DeleteWorkFile",
        };

        private static bool IsGateExempt(string toolName, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? args)
        {
            if (GateExempt.Contains(toolName)) return true;
            if (!string.Equals(toolName, "CallTool", StringComparison.OrdinalIgnoreCase) || args == null) return false;
            foreach (var kv in args)
            {
                if (string.Equals(kv.Key, "name", StringComparison.OrdinalIgnoreCase)
                    && kv.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return GateExempt.Contains(kv.Value.GetString() ?? "");
                }
            }
            return false;
        }

        /// <summary>协议自己可能塞进来的字段，不算工具参数。</summary>
        private static bool IsProtocolField(string key) =>
            string.Equals(key, "_meta", StringComparison.OrdinalIgnoreCase);

        /// <summary>从工具自带的 JSON schema 里取出属性名、required 列表和类型。
        /// false = schema 读不出来，意思是「这次调用不作判断」。</summary>
        private static bool ReadSchema(Tool? tool, List<string> known, List<string> required,
                                      Dictionary<string, string> types)
        {
            if (tool == null) return false;
            try
            {
                JsonElement schema = tool.InputSchema;
                if (schema.ValueKind != JsonValueKind.Object) return false;
                // properties 缺失 = **无参工具**，不是「schema 读不出来」。SDK 对有参数的
                // 工具一定会写 properties，所以这里的空是权威的空，可以照它判。
                if (!schema.TryGetProperty("properties", out var props)) return true;
                if (props.ValueKind != JsonValueKind.Object) return false;

                foreach (var p in props.EnumerateObject())
                {
                    known.Add(p.Name);
                    if (p.Value.ValueKind == JsonValueKind.Object
                        && p.Value.TryGetProperty("type", out var ty)
                        && ty.ValueKind == JsonValueKind.String)
                    {
                        var s = ty.GetString();
                        if (!string.IsNullOrEmpty(s)) types[p.Name] = s!;
                    }
                }

                if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                    foreach (var r in req.EnumerateArray())
                        if (r.ValueKind == JsonValueKind.String)
                        {
                            var s = r.GetString();
                            if (!string.IsNullOrEmpty(s)) required.Add(s!);
                        }

                return true;
            }
            catch
            {
                return false;   // schema 读不动，绝不能因此把调用拦下来
            }
        }

        private static CallToolResult TextError(string message) =>
            new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock> { new TextContentBlock { Text = message } }
            };
    }
}
