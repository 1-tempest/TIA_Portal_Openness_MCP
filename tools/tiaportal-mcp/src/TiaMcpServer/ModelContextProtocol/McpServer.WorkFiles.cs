using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // Server-side work folder (--work-dir > TIA_MCP_WORK_DIR > %TEMP%\TiaMcpWork). Openness
    // Export/Import only talk to files on the server; these four tools move those files to and
    // from the client. Every path is confined to the work folder. None of them touch TIA.
    public static partial class McpServer
    {
        [McpServerTool(Name = "ListWorkFiles"), Description(
            "[L1][Files] List files/folders in the server work folder (default %TEMP%\\TiaMcpWork; --work-dir / TIA_MCP_WORK_DIR). " +
            "Openness Export targets given as RELATIVE paths (e.g. InvokeObject Export args [\"textlists\",\"TL_X\"]) land here. Does not touch TIA.")]
        public static ResponseMessage ListWorkFiles(
            [Description("Sub-folder relative to the work folder; empty = root")] string subPath = "",
            [Description("File pattern, e.g. *.xlsx; empty = all")] string pattern = "",
            [Description("Include sub-folders")] bool recursive = true)
        {
            try
            {
                var arr = new JsonArray();
                foreach (var e in WorkFolder.List(subPath, pattern, recursive))
                {
                    arr.Add(new JsonObject
                    {
                        ["path"] = e.Path,
                        ["dir"] = e.IsDirectory,
                        ["size"] = e.Size,
                        ["modifiedUtc"] = e.ModifiedUtc.ToString("o")
                    });
                }
                return new ResponseMessage
                {
                    Message = $"{arr.Count} entries.",
                    Meta = new JsonObject { ["root"] = WorkFolder.Root, ["entries"] = arr }
                };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                throw new McpException(ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ReadWorkFile"), Description(
            "[L1][Files] Read a file from the server work folder. encoding auto|text|base64 (auto: UTF-8 text, else base64, e.g. .xlsx). " +
            "Pages always fit the response size limit (TIA_MCP_MAX_RESPONSE_CHARS, default 20000 chars): with the default limit a page is at most " +
            "~14 KB for base64 and up to ~19 KB for ASCII text (less for non-ASCII text); a larger 'length' is reduced (meta.clamped=true). " +
            "Page with nextOffset until eof=true; decode each base64 page before concatenating the bytes. Does not touch TIA.")]
        public static ResponseMessage ReadWorkFile(
            [Description("File path relative to the work folder")] string path,
            [Description("Byte offset to start at (use the previous page's nextOffset)")] long offset = 0,
            [Description("Max bytes to return (0 = the largest page that fits the response limit)")] int length = 0,
            [Description("auto | text | base64")] string encoding = "auto")
        {
            try
            {
                // A page larger than the response limit would be parked by the response guard
                // (meta.truncated + exportId instead of content). Shrink the page until it fits.
                var limit = ResolvedMaxResponseChars();
                var budget = limit > 0 ? limit - 300 : int.MaxValue; // headroom for the protocol wrapper
                var want = length > 0 ? length : (limit > 0 ? budget : 1_000_000);
                var clamped = false;

                for (int attempt = 0; ; attempt++)
                {
                    var r = WorkFolder.Read(path, offset, want, encoding);
                    var response = new ResponseMessage
                    {
                        Message = $"{r.Returned} of {r.Size} bytes from offset {r.Offset} ({r.Encoding}){(r.Eof ? ", eof" : "")}.",
                        Meta = new JsonObject
                        {
                            ["path"] = path,
                            ["encoding"] = r.Encoding,
                            ["size"] = r.Size,
                            ["offset"] = r.Offset,
                            ["returned"] = r.Returned,
                            ["nextOffset"] = r.Eof ? null : r.Offset + r.Returned,
                            ["eof"] = r.Eof,
                            ["clamped"] = clamped,
                            ["responseLimitChars"] = limit,
                            ["content"] = r.Content
                        }
                    };

                    // Default serializer escapes non-ASCII, so this measure is on the safe side.
                    var measured = System.Text.Json.JsonSerializer.Serialize(response).Length;
                    if (limit <= 0 || measured <= budget || r.Returned <= 64 || attempt >= 8) return response;

                    clamped = true;
                    want = Math.Max(64, (int)(r.Returned * (double)budget / measured * 0.95));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                throw new McpException(ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "WriteWorkFile"), Description(
            "[L1][Files] Write a file into the server work folder (folders are created). Text is written as UTF-8 without BOM; " +
            "base64=true for binary (e.g. an edited .xlsx). Refuses to replace an existing file unless overwrite=true; append=true " +
            "adds to it (send big files in base64 chunks). Then Import it, e.g. InvokeObject Import args [\"textlists\",\"TL_X.xlsx\"]. Does not touch TIA.")]
        public static ResponseMessage WriteWorkFile(
            [Description("File path relative to the work folder")] string path,
            [Description("Content: text, or base64 when base64=true")] string content,
            [Description("content is base64")] bool base64 = false,
            [Description("Replace an existing file")] bool overwrite = false,
            [Description("Append to the file (chunked upload)")] bool append = false)
        {
            try
            {
                var size = WorkFolder.Write(path, content, base64, overwrite, append);
                return new ResponseMessage
                {
                    Message = $"Wrote '{path}' ({size} bytes).",
                    Meta = new JsonObject { ["path"] = path, ["fullPath"] = WorkFolder.Resolve(path), ["size"] = size }
                };
            }
            catch (FormatException fx)
            {
                throw new McpException("content is not valid base64: " + fx.Message, fx, McpErrorCode.InvalidParams);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                throw new McpException(ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "DeleteWorkFile"), Description(
            "[L1][Files] Delete a file or folder (recursively) inside the server work folder, e.g. to clear an old export before re-exporting. Does not touch TIA.")]
        public static ResponseMessage DeleteWorkFile(
            [Description("File or folder path relative to the work folder")] string path)
        {
            try
            {
                WorkFolder.Delete(path);
                return new ResponseMessage { Message = $"Deleted '{path}'.", Meta = new JsonObject { ["path"] = path } };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                throw new McpException(ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }
    }
}
