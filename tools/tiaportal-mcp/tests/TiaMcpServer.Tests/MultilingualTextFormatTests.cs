using System;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// TIA V21 rejects plain strings for MultilingualTextItem.Text ("invalid format") and accepts
    /// "&lt;body&gt;&lt;p&gt;…&lt;/p&gt;&lt;/body&gt;". Pins the wrapping.
    /// </summary>
    internal static class MultilingualTextFormatTests
    {
        internal static void Run(Action<bool, string> check)
        {
            check(MultilingualTextFormat.ToTiaXml("Plain string text") == "<body><p>Plain string text</p></body>", "plain text is wrapped in <body><p>");
            check(MultilingualTextFormat.ToTiaXml("A < B & C > \"D\"") == "<body><p>A &lt; B &amp; C &gt; &quot;D&quot;</p></body>", "XML special characters are escaped");
            check(MultilingualTextFormat.ToTiaXml("Line 1\r\nLine 2\nLine 3") == "<body><p>Line 1</p><p>Line 2</p><p>Line 3</p></body>", "each line becomes its own <p>");
            check(MultilingualTextFormat.ToTiaXml("<body><p>already</p></body>") == "<body><p>already</p></body>", "text already in TIA format passes unchanged");
            check(MultilingualTextFormat.ToTiaXml("") == "<body><p></p></body>", "empty text becomes one empty paragraph");
            check(MultilingualTextFormat.ToTiaXml("Überhitzung 状态") == "<body><p>Überhitzung 状态</p></body>", "non-ASCII text is kept as is");
        }
    }
}
