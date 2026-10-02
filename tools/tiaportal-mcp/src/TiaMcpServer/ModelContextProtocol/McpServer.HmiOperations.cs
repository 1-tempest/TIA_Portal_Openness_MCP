using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "ApplyHmiOperations"), Description(
            "[L2][HMI] Run many HMI edits in ONE call: the screen and items are resolved once, the batch runs under one " +
            "TiaPortal.ExclusiveAccess (optionally one Transaction), and every operation returns its own ok/error " +
            "without stopping the batch. operationsJson = JSON array of {op, screen, item | path, sub?, ...}: " +
            "SetAttribute{name,value} | GetAttribute{name} | SetProperty{name (dotted),value} | GetProperty{name (dotted)} | " +
            "Bind{property,tag,dataType?,plcTag?,address?} | SetScript{event,code,globalCode?,async?} | Delete | " +
            "Invoke{method,args?,allowWrite?}. 'path' is an HmiPath below the HMI software (e.g. TextLists/TL_X); " +
            "'sub' walks further from the target (e.g. Font). runAsync=true returns a jobId at once; poll GetHmiOperationsJob. " +
            "Use runAsync for anything that may take over ~30 s (HTTP gateway timeout).")]
        public static ResponseMessage ApplyHmiOperations(
            [Description("HMI software path, e.g. HMI_RT_1")] string hmiSoftwarePath,
            [Description("JSON array of operations (or {\"operations\":[...]}).")] string operationsJson,
            [Description("true: start a background job and return its jobId immediately.")] bool runAsync = false,
            [Description("Hold TiaPortal.ExclusiveAccess for the batch (faster; blocks the TIA UI meanwhile). Default true.")] bool useExclusiveAccess = true,
            [Description("Also wrap the batch in one Openness Transaction (undo as one step). Default false.")] bool useTransaction = false)
        {
            JsonArray ops;
            try
            {
                var parsed = JsonNode.Parse(operationsJson ?? "");
                ops = parsed as JsonArray
                      ?? (parsed as JsonObject)?["operations"] as JsonArray
                      ?? throw new McpException("operationsJson must be a JSON array of operations or {\"operations\":[...]}.", McpErrorCode.InvalidParams);
            }
            catch (System.Text.Json.JsonException jx)
            {
                throw new McpException("operationsJson is not valid JSON: " + jx.Message, McpErrorCode.InvalidParams);
            }

            try
            {
                if (runAsync)
                {
                    var started = Portal.StartHmiOperationsJob(hmiSoftwarePath, ops, useExclusiveAccess, useTransaction);
                    return new ResponseMessage { Message = $"Started job {started["jobId"]} with {ops.Count} operations. Poll GetHmiOperationsJob.", Meta = started };
                }

                var result = Portal.ApplyHmiOperations(hmiSoftwarePath, ops, useExclusiveAccess, useTransaction);
                return new ResponseMessage { Message = $"{result["ok"]}/{result["total"]} operations ok, {result["failed"]} failed, {result["elapsedMs"]} ms.", Meta = result };
            }
            catch (PortalException pex)
            {
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound || pex.Code == PortalErrorCode.InvalidParams || pex.Code == PortalErrorCode.InvalidState
                        ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying HMI operations: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetHmiOperationsJob"), Description(
            "[L2][HMI] Progress and per-operation results of an ApplyHmiOperations job started with runAsync=true. " +
            "state: queued|running|done|failed. Does not touch TIA, so it answers while the job runs. Page results with offset/limit.")]
        public static ResponseMessage GetHmiOperationsJob(
            [Description("jobId returned by ApplyHmiOperations(runAsync=true)")] string jobId,
            [Description("Include per-operation results. Default true.")] bool includeResults = true,
            [Description("First result index to return")] int offset = 0,
            [Description("Max results to return")] int limit = 500)
        {
            try
            {
                var job = Portal.GetHmiOperationsJob(jobId, includeResults, offset, limit);
                return new ResponseMessage { Message = $"{job["state"]}: {job["done"]}/{job["total"]} done, {job["failed"]} failed.", Meta = job };
            }
            catch (PortalException pex)
            {
                throw new McpException(pex.Message, pex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "GetToolTimings"), Description(
            "[L0][Meta] Per-tool call count, total/avg/max milliseconds since start (or last reset), measured around each " +
            "tool call inside the server. Use to compare publish timings before/after a change. Does not touch TIA.")]
        public static ResponseMessage GetToolTimings(
            [Description("Clear the counters after reading")] bool reset = false)
        {
            var snapshot = ToolTimings.Snapshot(reset);
            return new ResponseMessage { Message = $"{snapshot.Count} tools timed.", Meta = new JsonObject { ["tools"] = snapshot } };
        }
    }
}
