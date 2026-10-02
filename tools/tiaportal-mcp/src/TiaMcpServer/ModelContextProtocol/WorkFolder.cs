using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Server-side work folder for file round trips (Openness Export → client edits → Import).
    /// The client cannot see the VM's disk, so it lists, reads and writes files here through MCP;
    /// every path is confined to this folder.
    ///
    /// Root: --work-dir &gt; TIA_MCP_WORK_DIR &gt; %TEMP%\TiaMcpWork.
    /// Zero-dependency on purpose so the offline test suite can drive it.
    /// </summary>
    internal static class WorkFolder
    {
        private static string? _override;

        public static void SetRootOverride(string? path)
        {
            _override = string.IsNullOrWhiteSpace(path) ? null : path;
        }

        public static string Root
        {
            get
            {
                var root = _override
                           ?? Environment.GetEnvironmentVariable("TIA_MCP_WORK_DIR")
                           ?? Path.Combine(Path.GetTempPath(), "TiaMcpWork");
                root = Path.GetFullPath(root);
                Directory.CreateDirectory(root);
                return root;
            }
        }

        /// <summary>
        /// Full path of <paramref name="relativePath"/> inside the work folder. An absolute path is
        /// accepted only if it already lies inside the folder. Anything escaping it throws.
        /// </summary>
        public static string Resolve(string? relativePath)
        {
            var root = Root;
            var rel = (relativePath ?? "").Trim();
            var full = Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(root, rel));
            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar;
            var cmp = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(full, root, cmp) && !full.StartsWith(rootWithSep, cmp))
            {
                throw new UnauthorizedAccessException($"Path '{relativePath}' is outside the work folder '{root}'.");
            }
            return full;
        }

        public static string ToRelative(string fullPath)
        {
            var root = Root;
            var rel = fullPath.Length > root.Length ? fullPath.Substring(root.Length).TrimStart('\\', '/') : "";
            return rel.Replace('\\', '/');
        }

        public sealed class Entry
        {
            public string Path = "";
            public bool IsDirectory;
            public long Size;
            public DateTime ModifiedUtc;
        }

        public static List<Entry> List(string? subPath, string? pattern, bool recursive)
        {
            var dir = Resolve(subPath);
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"'{subPath}' does not exist in the work folder.");
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var pat = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern!;
            var list = new List<Entry>();
            foreach (var d in Directory.GetDirectories(dir, pat, option))
            {
                list.Add(new Entry { Path = ToRelative(d), IsDirectory = true, ModifiedUtc = Directory.GetLastWriteTimeUtc(d) });
            }
            foreach (var f in Directory.GetFiles(dir, pat, option))
            {
                var fi = new FileInfo(f);
                list.Add(new Entry { Path = ToRelative(f), Size = fi.Length, ModifiedUtc = fi.LastWriteTimeUtc });
            }
            return list.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Reads a slice. encoding "auto": text when the bytes decode as UTF-8 without NULs, else base64.
        /// Offsets and lengths are in bytes.
        /// </summary>
        public static (string Content, string Encoding, long Size, long Offset, int Returned, bool Eof) Read(string path, long offset, int length, string? encoding)
        {
            var full = Resolve(path);
            if (!File.Exists(full)) throw new FileNotFoundException($"'{path}' does not exist in the work folder.");
            var size = new FileInfo(full).Length;
            offset = Math.Max(0, Math.Min(offset, size));
            var count = (int)Math.Min(length <= 0 ? 1_000_000 : length, size - offset);
            var buffer = new byte[count];
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                var read = 0;
                while (read < count)
                {
                    var n = fs.Read(buffer, read, count - read);
                    if (n <= 0) break;
                    read += n;
                }
            }

            var enc = (encoding ?? "auto").Trim().ToLowerInvariant();
            if (enc == "auto") enc = LooksLikeText(buffer) ? "text" : "base64";

            // A text page must not end inside a UTF-8 sequence, or both pages decode a broken char.
            if (enc == "text" && offset + count < size)
            {
                var cut = Utf8SafeLength(buffer);
                if (cut > 0 && cut < count)
                {
                    Array.Resize(ref buffer, cut);
                    count = cut;
                }
            }

            var content = enc == "base64" ? Convert.ToBase64String(buffer) : DecodeUtf8(buffer, offset == 0);
            return (content, enc, size, offset, count, offset + count >= size);
        }

        /// <summary>Length without a trailing incomplete UTF-8 sequence.</summary>
        internal static int Utf8SafeLength(byte[] b)
        {
            var n = b.Length;
            var i = n - 1;
            var continuation = 0;
            while (i >= 0 && continuation < 3 && (b[i] & 0xC0) == 0x80) { i--; continuation++; }
            if (i < 0) return n;
            var lead = b[i];
            var need = (lead & 0x80) == 0 ? 1 : (lead & 0xE0) == 0xC0 ? 2 : (lead & 0xF0) == 0xE0 ? 3 : (lead & 0xF8) == 0xF0 ? 4 : 1;
            return continuation + 1 >= need ? n : i;
        }

        public static long Write(string path, string content, bool base64, bool overwrite, bool append)
        {
            var full = Resolve(path);
            if (Directory.Exists(full)) throw new IOException($"'{path}' is a directory.");
            if (File.Exists(full) && !overwrite && !append)
            {
                throw new IOException($"'{path}' already exists. Pass overwrite=true to replace it or append=true to add to it.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var bytes = base64 ? Convert.FromBase64String(content ?? "") : new UTF8Encoding(false).GetBytes(content ?? "");
            using (var fs = new FileStream(full, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
            {
                fs.Write(bytes, 0, bytes.Length);
            }
            return new FileInfo(full).Length;
        }

        public static void Delete(string path)
        {
            var full = Resolve(path);
            if (string.Equals(full, Root, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("The work folder itself cannot be deleted.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            else if (File.Exists(full)) File.Delete(full);
            else throw new FileNotFoundException($"'{path}' does not exist in the work folder.");
        }

        private static bool LooksLikeText(byte[] bytes)
        {
            if (bytes.Any(b => b == 0)) return false;
            try
            {
                new UTF8Encoding(false, true).GetString(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                // a slice may cut a multi-byte char at its end; one more try without the tail
                if (bytes.Length < 4) return false;
                try { new UTF8Encoding(false, true).GetString(bytes, 0, bytes.Length - 3); return true; }
                catch (DecoderFallbackException) { return false; }
            }
        }

        private static string DecodeUtf8(byte[] bytes, bool stripBom)
        {
            var start = stripBom && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return new UTF8Encoding(false).GetString(bytes, start, bytes.Length - start);
        }
    }
}
