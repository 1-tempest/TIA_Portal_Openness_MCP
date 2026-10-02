using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// HmiNameLookup replaced a per-call walk of the whole collection (one cross-process Name read
    /// per element). These tests count Name reads on fakes shaped like Openness compositions, so a
    /// regression back to the O(N) walk shows up as a number, not just as a slow publish.
    /// </summary>
    internal static class HmiNameLookupTests
    {
        internal static void Run(Action<bool, string> check)
        {
            // Composition with Find(string): a hit must cost no enumeration at all.
            var withFind = new FakeCompositionWithFind(Enumerable.Range(0, 500).Select(i => new FakeItem("Item" + i)));
            var hit = HmiNameLookup.Find(withFind, "Item321");
            check(hit is FakeItem h && h.Name == "Item321", "Find(string) hit returns the item");
            check(withFind.Enumerations == 0, "Find(string) hit must not enumerate (enumerations=" + withFind.Enumerations + ")");

            // Case-insensitive fallback: Find is exact, the index catches 'item7' for 'Item7'.
            check(HmiNameLookup.Find(withFind, "item7") is FakeItem, "case-insensitive match still works when Find is exact");
            var reads = FakeItem.NameReads;
            check(HmiNameLookup.Find(withFind, "NoSuchItem") == null, "missing name returns null");
            check(withFind.Enumerations == 1, "the case-insensitive index is built once per collection (enumerations=" + withFind.Enumerations + ")");
            check(FakeItem.NameReads == reads, "a miss after the index exists costs no Name reads");

            // Composition without Find: index built once, hits verified by one Name read.
            var noFind = new FakeComposition(Enumerable.Range(0, 300).Select(i => new FakeItem("N" + i)), hasFind: false);
            for (int i = 0; i < 300; i += 10) HmiNameLookup.Find(noFind, "N" + i);
            check(noFind.Enumerations == 1, "without Find, 30 lookups enumerate once (enumerations=" + noFind.Enumerations + ")");

            // A deleted object (Name read throws) must not be returned from the cache.
            var victim = (FakeItem)HmiNameLookup.Find(noFind, "N5")!;
            victim.Deleted = true;
            noFind.Remove(victim);
            check(HmiNameLookup.Find(noFind, "N5") == null, "deleted item is not returned from the cache");

            // Renamed object: the cache entry must not answer for the old name.
            var renamed = (FakeItem)HmiNameLookup.Find(noFind, "N6")!;
            renamed.Name = "N6_new";
            check(HmiNameLookup.Find(noFind, "N6") == null, "renamed item is not returned for its old name");
            check(HmiNameLookup.Find(noFind, "N6_new") is FakeItem, "renamed item is found under its new name");

            // Without Find, an item added later must be found (miss → rebuild).
            noFind.Add(new FakeItem("Late"));
            check(HmiNameLookup.Find(noFind, "Late") is FakeItem, "without Find, an item added after the index was built is found");

            // Screens: root Find first, then groups via the walk; cached afterwards.
            var grouped = new FakeScreen("Deep");
            var software = new FakeSoftware(
                new FakeScreenComposition(new[] { new FakeScreen("Home"), new FakeScreen("Overview") }),
                new[] { new FakeGroup(new[] { grouped }) });
            check(HmiNameLookup.FindScreen(software, "Overview") is FakeScreen, "root screen found through Find");
            check(software.Root.Enumerations == 0, "root screen hit does not walk (enumerations=" + software.Root.Enumerations + ")");
            check(HmiNameLookup.FindScreen(software, "deep") == grouped, "screen inside a group found (case-insensitive)");
            var walks = software.Root.Enumerations;
            check(HmiNameLookup.FindScreen(software, "Deep") == grouped && software.Root.Enumerations == walks,
                "second lookup of a grouped screen comes from the cache");
            check(HmiNameLookup.FindScreen(software, "Nope") == null, "missing screen returns null");

            // MultilingualTextItems have no Name: address them by culture ("EventText/Items/en-US").
            var mlItems = new System.Collections.ArrayList
            {
                new FakeTextItem("de-DE", "Störung"),
                new FakeTextItem("en-US", "Fault")
            };
            check(HmiNameLookup.Find(mlItems, "en-US") is FakeTextItem en && en.Text == "Fault", "multilingual item found by culture name");
            check(HmiNameLookup.Find(mlItems, "fr-FR") == null, "missing culture returns null");

            // Contract: never throws.
            check(HmiNameLookup.Find(null, "x") == null && HmiNameLookup.Find(withFind, "") == null, "null/empty inputs return null");
        }

        internal sealed class FakeItem
        {
            public static int NameReads;
            private string _name;
            public FakeItem(string name) { _name = name; }
            public bool Deleted;
            internal string RawName => _name; // what Openness Find compares server-side, no proxy Name read
            public string Name
            {
                get
                {
                    NameReads++;
                    if (Deleted) throw new InvalidOperationException("EngineeringObjectDisposedException");
                    return _name;
                }
                set { _name = value; }
            }
        }

        internal class FakeComposition : IEnumerable
        {
            private readonly List<FakeItem> _items;
            private readonly bool _hasFind;
            public int Enumerations;
            public FakeComposition(IEnumerable<FakeItem> items, bool hasFind) { _items = items.ToList(); _hasFind = hasFind; }
            public void Add(FakeItem item) => _items.Add(item);
            public void Remove(FakeItem item) => _items.Remove(item);
            public IEnumerator GetEnumerator() { Enumerations++; return _items.ToList().GetEnumerator(); }
            public FakeItem? FindImpl(string name) => _hasFind ? _items.FirstOrDefault(i => !i.Deleted && i.RawName == name) : null;
        }

        // Find(string) only on this subtype, so the lookup sees "no Find" on the base type.
        internal sealed class FakeCompositionWithFind : FakeComposition
        {
            public FakeCompositionWithFind(IEnumerable<FakeItem> items) : base(items, true) { }
            public FakeItem? Find(string name) => FindImpl(name);
        }

        internal sealed class FakeLanguage
        {
            public FakeLanguage(string culture) { Culture = new System.Globalization.CultureInfo(culture); }
            public System.Globalization.CultureInfo Culture { get; }
        }

        internal sealed class FakeTextItem
        {
            public FakeTextItem(string culture, string text) { Language = new FakeLanguage(culture); Text = text; }
            public FakeLanguage Language { get; }
            public string Text { get; set; }
        }

        internal sealed class FakeScreen
        {
            public FakeScreen(string name) { Name = name; }
            public string Name { get; }
        }

        internal sealed class FakeScreenComposition : IEnumerable
        {
            private readonly List<FakeScreen> _screens;
            public int Enumerations;
            public FakeScreenComposition(IEnumerable<FakeScreen> screens) { _screens = screens.ToList(); }
            public IEnumerator GetEnumerator() { Enumerations++; return _screens.GetEnumerator(); }
            public FakeScreen? Find(string name) => _screens.FirstOrDefault(s => s.Name == name);
        }

        internal sealed class FakeGroup
        {
            public FakeGroup(IEnumerable<FakeScreen> screens) { Screens = screens.ToList(); }
            public List<FakeScreen> Screens { get; }
        }

        internal sealed class FakeSoftware
        {
            public FakeSoftware(FakeScreenComposition root, IEnumerable<FakeGroup> groups) { Root = root; ScreenGroups = groups.ToList(); }
            public FakeScreenComposition Root { get; }
            public FakeScreenComposition Screens => Root;
            public List<FakeGroup> ScreenGroups { get; }
        }
    }
}
