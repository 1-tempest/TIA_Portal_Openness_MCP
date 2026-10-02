using System;
using System.Linq;
using System.Security;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// TIA V21 rejects plain strings for MultilingualTextItem.Text ("The argument 'text' … has an
    /// invalid format"); it wants "&lt;body&gt;&lt;p&gt;…&lt;/p&gt;&lt;/body&gt;". Plain text is
    /// XML-escaped, one &lt;p&gt; per line; text already in that format passes unchanged.
    /// Zero-dependency so the offline tests can pin the format.
    /// </summary>
    internal static class MultilingualTextFormat
    {
        public static string ToTiaXml(string? text)
        {
            var t = text ?? "";
            var trimmed = t.Trim();
            if (trimmed.StartsWith("<body", StringComparison.OrdinalIgnoreCase)
                && trimmed.EndsWith("</body>", StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }

            var lines = t.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            return "<body>" + string.Concat(lines.Select(l => "<p>" + SecurityElement.Escape(l) + "</p>")) + "</body>";
        }
    }
}
