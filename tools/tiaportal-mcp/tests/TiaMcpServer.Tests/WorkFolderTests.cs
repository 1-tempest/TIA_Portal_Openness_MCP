using System;
using System.IO;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// The work folder is the only place the file tools may touch. A confinement bug would not fail
    /// anything visibly; it would let a client read or overwrite arbitrary files on the TIA VM.
    /// </summary>
    internal static class WorkFolderTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var root = Path.Combine(Path.GetTempPath(), "TiaMcpWorkTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            WorkFolder.SetRootOverride(root);
            try
            {
                check(Directory.Exists(WorkFolder.Root), "root is created on first use");

                // Confinement
                check(Throws(() => WorkFolder.Resolve("../outside.txt")), "'..' escaping the folder is refused");
                check(Throws(() => WorkFolder.Resolve("a/../../outside.txt")), "nested '..' escaping the folder is refused");
                check(Throws(() => WorkFolder.Resolve(Path.GetTempPath())), "absolute path outside the folder is refused");
                check(Throws(() => WorkFolder.Resolve(root + "_sibling" + Path.DirectorySeparatorChar + "x")), "sibling folder sharing the name prefix is refused");
                check(!Throws(() => WorkFolder.Resolve(Path.Combine(root, "sub", "ok.txt"))), "absolute path inside the folder is accepted");
                check(!Throws(() => WorkFolder.Resolve("a/../b.txt")), "'..' that stays inside is accepted");

                // Text round trip (UTF-8, umlauts and CJK)
                WorkFolder.Write("textlists/TL.txt", "Zustand Ä 状态", base64: false, overwrite: false, append: false);
                var t = WorkFolder.Read("textlists/TL.txt", 0, 0, "auto");
                check(t.Encoding == "text" && t.Content == "Zustand Ä 状态" && t.Eof, "text file reads back as text (got " + t.Encoding + ")");

                // Overwrite guard
                check(Throws(() => WorkFolder.Write("textlists/TL.txt", "x", false, false, false)), "existing file is not replaced without overwrite");
                WorkFolder.Write("textlists/TL.txt", "new", false, overwrite: true, append: false);
                check(WorkFolder.Read("textlists/TL.txt", 0, 0, "text").Content == "new", "overwrite=true replaces the file");

                // Binary round trip, chunked upload + paged download
                var bytes = Enumerable.Range(0, 3000).Select(i => (byte)(i % 256)).ToArray();
                WorkFolder.Write("bin/data.xlsx", Convert.ToBase64String(bytes, 0, 1000), base64: true, overwrite: false, append: false);
                WorkFolder.Write("bin/data.xlsx", Convert.ToBase64String(bytes, 1000, 2000), base64: true, overwrite: false, append: true);
                var p1 = WorkFolder.Read("bin/data.xlsx", 0, 1200, "auto");
                var p2 = WorkFolder.Read("bin/data.xlsx", p1.Offset + p1.Returned, 5000, "auto");
                var back = Convert.FromBase64String(p1.Content).Concat(Convert.FromBase64String(p2.Content)).ToArray();
                check(p1.Encoding == "base64" && !p1.Eof && p2.Eof, "binary file pages as base64 with eof on the last page");
                check(back.SequenceEqual(bytes), "chunked base64 upload and paged download round-trip the bytes");

                // Listing and delete
                var list = WorkFolder.List("", "", recursive: true);
                check(list.Any(e => e.Path == "textlists/TL.txt") && list.Any(e => e.Path == "bin" && e.IsDirectory),
                    "list shows relative paths with '/' and folders");
                WorkFolder.Delete("bin");
                check(!Directory.Exists(Path.Combine(root, "bin")), "delete removes a folder recursively");
                check(Throws(() => WorkFolder.Delete("")), "the work folder itself cannot be deleted");
            }
            finally
            {
                WorkFolder.SetRootOverride(null);
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static bool Throws(Action a)
        {
            try { a(); return false; }
            catch { return true; }
        }
    }
}
