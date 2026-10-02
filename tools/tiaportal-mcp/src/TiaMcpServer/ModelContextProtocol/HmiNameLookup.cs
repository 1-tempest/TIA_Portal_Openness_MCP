using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Name lookups in Openness collections without walking the whole collection per call.
    ///
    /// The old lookup enumerated the collection and read <c>Name</c> of every element — each read
    /// is a cross-process call into TIA Portal. ApplyUnifiedHmiScreenDesignJson does one lookup
    /// per item, so a 493-item screen cost ~N²/2 ≈ 120k cross-process calls.
    ///
    /// Now: the composition's own <c>Find(string)</c> first (one call). Its miss falls back to a
    /// case-insensitive name index built once per collection instance. Cached hits are verified
    /// by re-reading Name (a deleted object throws, a renamed one mismatches → rebuild), so the
    /// cache needs no explicit invalidation.
    ///
    /// Zero-dependency on purpose (pure reflection) so the offline test suite can feed it fakes.
    /// </summary>
    internal static class HmiNameLookup
    {
        private sealed class NameIndex
        {
            public readonly Dictionary<string, object> ByName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly ConditionalWeakTable<object, NameIndex> ItemIndexes = new ConditionalWeakTable<object, NameIndex>();
        private static readonly ConditionalWeakTable<object, NameIndex> ScreenIndexes = new ConditionalWeakTable<object, NameIndex>();
        private static readonly ConcurrentDictionary<Type, MethodInfo?> FindMethods = new ConcurrentDictionary<Type, MethodInfo?>();
        private static readonly object Sync = new object();

        /// <summary>Member of <paramref name="collection"/> whose Name equals <paramref name="name"/> (case-insensitive), or null. Never throws.</summary>
        public static object? Find(object? collection, string? name)
        {
            if (collection == null || string.IsNullOrWhiteSpace(name)) return null;
            var wanted = name!.Trim();

            var find = GetFindMethod(collection.GetType());
            if (find != null)
            {
                try
                {
                    var hit = find.Invoke(collection, new object[] { wanted });
                    if (hit != null) return hit;
                }
                catch
                {
                    // fall through to the index
                }
            }

            lock (Sync)
            {
                if (ItemIndexes.TryGetValue(collection, out var index))
                {
                    if (index.ByName.TryGetValue(wanted, out var cached))
                    {
                        if (NameMatches(cached, wanted)) return cached;
                    }
                    else if (find != null)
                    {
                        // Find (exact) missed and the case-insensitive index has no entry either.
                        return null;
                    }
                    ItemIndexes.Remove(collection);
                }

                index = BuildIndex(collection as IEnumerable);
                ItemIndexes.Add(collection, index);
                return index.ByName.TryGetValue(wanted, out var found) ? found : null;
            }
        }

        /// <summary>
        /// Screen by name anywhere under the HMI software (root and screen groups), case-insensitive.
        /// Cached per HMI software instance; a hit is verified by re-reading its Name.
        /// </summary>
        public static object? FindScreen(object? hmiRoot, string? name)
        {
            if (hmiRoot == null || string.IsNullOrWhiteSpace(name)) return null;
            var wanted = name!.Trim();

            lock (Sync)
            {
                if (ScreenIndexes.TryGetValue(hmiRoot, out var index)
                    && index.ByName.TryGetValue(wanted, out var cached)
                    && NameMatches(cached, wanted))
                {
                    return cached;
                }
            }

            // Root screens: one Find call instead of a walk.
            var rootScreens = GetProperty(hmiRoot, "Screens");
            if (rootScreens != null)
            {
                var find = GetFindMethod(rootScreens.GetType());
                if (find != null)
                {
                    try
                    {
                        var hit = find.Invoke(rootScreens, new object[] { wanted });
                        if (hit != null)
                        {
                            Remember(hmiRoot, wanted, hit);
                            return hit;
                        }
                    }
                    catch { }
                }
            }

            // Screen groups (or no Find): walk once, remember every screen seen.
            object? match = null;
            var fresh = new NameIndex();
            foreach (var screen in HmiScreenWalk.EnumerateAll(hmiRoot))
            {
                var n = GetName(screen);
                if (string.IsNullOrWhiteSpace(n)) continue;
                n = n!.Trim();
                if (!fresh.ByName.ContainsKey(n)) fresh.ByName[n] = screen;
                if (match == null && string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)) match = screen;
            }
            lock (Sync)
            {
                ScreenIndexes.Remove(hmiRoot);
                ScreenIndexes.Add(hmiRoot, fresh);
            }
            return match;
        }

        private static void Remember(object hmiRoot, string name, object screen)
        {
            lock (Sync)
            {
                ScreenIndexes.GetValue(hmiRoot, _ => new NameIndex()).ByName[name] = screen;
            }
        }

        private static NameIndex BuildIndex(IEnumerable? en)
        {
            var index = new NameIndex();
            if (en == null || en is string) return index;
            try
            {
                foreach (var it in en)
                {
                    if (it == null) continue;
                    var n = GetName(it);
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    n = n!.Trim();
                    if (!index.ByName.ContainsKey(n)) index.ByName[n] = it;
                }
            }
            catch
            {
                // best-effort, same contract as the old enumerate-and-compare
            }
            return index;
        }

        private static bool NameMatches(object o, string wanted)
            => string.Equals(GetName(o)?.Trim(), wanted, StringComparison.OrdinalIgnoreCase);

        private static MethodInfo? GetFindMethod(Type t) => FindMethods.GetOrAdd(t, type =>
        {
            try
            {
                var m = type.GetMethod("Find", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                return m != null && !m.ReturnType.IsValueType && m.ReturnType != typeof(string) ? m : null;
            }
            catch
            {
                return null;
            }
        });

        private static object? GetProperty(object obj, string name)
        {
            try
            {
                return obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
            }
            catch
            {
                return null;
            }
        }

        private static string? GetName(object item)
        {
            try
            {
                // Dynamizations have no Name; they are addressed by the property they drive.
                // MultilingualTextItems have neither; they are addressed by culture (e.g. "en-US").
                var n = (GetProperty(item, "Name") ?? GetProperty(item, "PropertyName"))?.ToString();
                if (n != null) return n;
                var language = GetProperty(item, "Language");
                return language == null ? null : (GetProperty(language, "Culture") as System.Globalization.CultureInfo)?.Name;
            }
            catch
            {
                return null;
            }
        }
    }
}
