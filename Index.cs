using System.IO.Enumeration;

struct Node
{
    public string? Name;
    public int Parent;
    public long Size, Alloc, Modified, Accessed;
    public bool Live, Dir;
}

/// In-memory copy of the MFT kept current from the USN journal, plus the read-only queries over it.
/// Directory totals are derived lazily: changes only mark the index dirty.
sealed class Index(Volume vol)
{
    const int Root = 5;
    const long RebuildInterval = 2000; // ms; under constant churn, totals lag by at most this much

    readonly object gate = new();
    Node[] nodes = [];
    List<int>?[] kids = [];
    long[] total = [], newest = [];
    int[] files = [];
    bool dirty;
    long journalId, usn, changes, built;
    DateTime lastChange;
    string? error;

    public int Count => nodes.Length;

    public void Load()
    {
        vol.Reload(); // the MFT may have grown since the last scan
        var (id, next) = vol.QueryJournal(); // before the scan, so nothing that changes during it is missed
        var n = new Node[vol.RecordCount];
        vol.Scan((i, raw) => { if (Mft.Parse(raw, out var r)) Apply(n, i, r); });
        lock (gate) { nodes = n; journalId = id; usn = next; dirty = true; built = 0; error = null; }
    }

    /// Follows the USN journal forever; falls back to a full rescan if the journal is lost.
    public void Watch()
    {
        while (true)
        {
            try
            {
                var changed = vol.ReadJournal(journalId, ref usn);
                var recs = changed.Where(f => f < int.MaxValue).SelectMany(f => Fetch((int)f)).ToList(); // I/O outside the lock
                lock (gate)
                {
                    dirty = true;
                    foreach (var (i, r) in recs) Refresh(i, r);
                    changes += changed.Count;
                    lastChange = DateTime.Now;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"journal: {e.Message}; rescanning");
                lock (gate) error = e.Message;
                Thread.Sleep(5000);
                try { Load(); } catch (Exception e2) { Console.Error.WriteLine($"rescan failed: {e2.Message}"); }
            }
        }
    }

    /// A record plus any extension records its attribute list points to (journal entries name only the base).
    List<(int, MftRecord?)> Fetch(int i)
    {
        var raw = vol.ReadRecord(i);
        MftRecord? r = raw != null && Mft.Parse(raw, out var p) ? p : null;
        var all = new List<(int, MftRecord?)> { (i, r) };
        foreach (long e in r?.Ext ?? [])
            if (e < int.MaxValue && vol.ReadRecord(e) is { } x && Mft.Parse(x, out var q)) all.Add(((int)e, q));
        return all;
    }

    void Refresh(int i, MftRecord? r)
    {
        if (i >= nodes.Length) Array.Resize(ref nodes, i + i / 4 + 1024);
        if (r is not { } x) { nodes[i].Live = false; return; }
        if (x.Base == 0 && x.Name == null && x.Ext != null) nodes[i].Name = null; // name lives in an extension, re-applied next
        Apply(nodes, i, x);
    }

    static void Apply(Node[] nodes, int i, in MftRecord r)
    {
        if (!r.InUse) { if (r.Base == 0) nodes[i].Live = false; return; }
        if (r.Base != 0) // extension record: contributes attributes that overflowed its base record
        {
            if (r.Base >= nodes.Length) return;
            ref var b = ref nodes[r.Base];
            if (r.HasData) (b.Size, b.Alloc) = (r.Size, r.Alloc);
            if (r.Name != null && b.Name == null) (b.Name, b.Parent) = (r.Name, r.Parent);
            return;
        }
        ref var x = ref nodes[i];
        (x.Live, x.Dir, x.Modified, x.Accessed) = (true, r.Dir, r.Modified, r.Accessed);
        if (r.Name != null) (x.Name, x.Parent) = (r.Name, r.Parent);
        if (r.HasData) (x.Size, x.Alloc) = (r.Size, r.Alloc);
        else if (!r.HasList) (x.Size, x.Alloc) = (0, 0); // with an attribute list, $DATA may live elsewhere
    }

    void Rebuild()
    {
        int n = nodes.Length;
        (kids, total, newest, files) = (new List<int>?[n], new long[n], new long[n], new int[n]);
        for (int i = 0; i < n; i++)
        {
            ref var x = ref nodes[i];
            if (!x.Live || x.Name == null || i == Root || x.Parent >= n || !nodes[x.Parent].Live) continue;
            (kids[x.Parent] ??= []).Add(i);
            if (x.Dir) continue;
            for (int j = i, depth = 0; depth < 256; depth++) // roll the file up into every ancestor
            {
                total[j] += x.Alloc;
                files[j]++;
                newest[j] = Math.Max(newest[j], x.Modified);
                if (j == Root) break;
                j = nodes[j].Parent;
                if (j >= n || !nodes[j].Live) break;
            }
        }
        (dirty, built) = (false, Environment.TickCount64);
    }

    T Read<T>(Func<T> query)
    {
        lock (gate)
        {
            if (dirty && Environment.TickCount64 - built >= RebuildInterval) Rebuild();
            return query();
        }
    }

    // ---- queries -------------------------------------------------------------------------------

    public object Summary() => Read(() =>
    {
        var d = new DriveInfo(vol.Drive);
        int dirs = 0;
        foreach (ref var x in nodes.AsSpan()) if (x.Live && x.Dir) dirs++;
        return new
        {
            volume = $"{vol.Drive}:\\",
            capacity = Human(d.TotalSize),
            used = Human(d.TotalSize - d.TotalFreeSpace),
            free = Human(d.TotalFreeSpace),
            indexed = Human(total[Root]),
            files = files[Root],
            dirs,
            mft_records = nodes.Length,
            journal_changes = changes,
            last_change = changes > 0 ? lastChange.ToString("s") : null,
            error, // set while the journal is lost and rescans are failing: results are stale
        };
    });

    public object List(string path, int limit) => Read(() =>
    {
        int r = Resolve(path);
        var children = kids[r] ?? [];
        return new
        {
            path = PathOf(r),
            size = Human(total[r]),
            files = files[r],
            entries = children.OrderByDescending(c => total[c]).Take(limit).Select(c => Entry(c, false)).ToList(),
            omitted = Math.Max(0, children.Count - limit),
        };
    });

    public object Largest(string path, int limit, string? ext, int olderThanDays) => Read(() =>
    {
        long cutoff = olderThanDays > 0 ? DateTime.UtcNow.AddDays(-olderThanDays).ToFileTimeUtc() : long.MaxValue;
        var hits = FilesUnder(Resolve(path))
            .Where(i => nodes[i].Modified < cutoff && (ext == null || Ext(nodes[i].Name!) == ext.TrimStart('.').ToLowerInvariant()))
            .ToList();
        return new
        {
            matched = hits.Count,
            matched_size = Human(hits.Sum(i => nodes[i].Alloc)),
            files = hits.OrderByDescending(i => nodes[i].Alloc).Take(limit).Select(i => Entry(i, true)).ToList(),
        };
    });

    public object Extensions(string path, int limit) => Read(() =>
        FilesUnder(Resolve(path))
            .GroupBy(i => Ext(nodes[i].Name!))
            .Select(g => (ext: g.Key, bytes: g.Sum(i => nodes[i].Alloc), count: g.Count()))
            .OrderByDescending(g => g.bytes).Take(limit)
            .Select(g => new { g.ext, size = Human(g.bytes), bytes = g.bytes, g.count })
            .ToList());

    public object Duplicates(string path, long minBytes, int limit) => Read(() =>
        FilesUnder(Resolve(path))
            .Where(i => nodes[i].Size >= minBytes)
            .GroupBy(i => (nodes[i].Size, nodes[i].Name!.ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .Select(g => (g, wasted: g.Sum(i => nodes[i].Alloc) - g.Max(i => nodes[i].Alloc)))
            .OrderByDescending(d => d.wasted).Take(limit)
            .Select(d => new
            {
                name = nodes[d.g.First()].Name,
                size = Human(nodes[d.g.First()].Size),
                copies = d.g.Count(),
                wasted = Human(d.wasted),
                paths = d.g.Take(10).Select(PathOf).ToList(),
            })
            .ToList());

    public object Find(string pattern, string path, int limit) => Read(() =>
        Under(Resolve(path))
            .Where(i => nodes[i].Live && FileSystemName.MatchesSimpleExpression(pattern, nodes[i].Name, ignoreCase: true))
            .OrderByDescending(i => nodes[i].Dir ? total[i] : nodes[i].Alloc).Take(limit)
            .Select(i => Entry(i, true))
            .ToList());

    // ---- helpers -------------------------------------------------------------------------------

    object Entry(int i, bool fullPath)
    {
        ref var x = ref nodes[i];
        long bytes = x.Dir ? total[i] : x.Alloc;
        return new
        {
            path = fullPath ? PathOf(i) : null,
            name = fullPath ? null : x.Name,
            type = x.Dir ? "dir" : "file",
            size = Human(bytes),
            bytes,
            files = x.Dir ? files[i] : (int?)null,
            modified = Date(x.Dir ? newest[i] : x.Modified), // dirs: newest file modification inside
            accessed = x.Dir ? null : Date(x.Accessed),
        };
    }

    int Resolve(string path)
    {
        int cur = Root;
        foreach (var part in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (cur == Root && part.EndsWith(':')) continue; // drive prefix
            int next = kids[cur]?.FindIndex(c => string.Equals(nodes[c].Name, part, StringComparison.OrdinalIgnoreCase)) ?? -1;
            if (next < 0) throw new ArgumentException($"Path not found: {path}");
            cur = kids[cur]![next];
        }
        return cur;
    }

    string PathOf(int i)
    {
        var parts = new List<string>();
        for (int depth = 0; i != Root && depth < 256; depth++, i = nodes[i].Parent) parts.Add(nodes[i].Name!);
        parts.Reverse();
        return $"{vol.Drive}:\\" + string.Join('\\', parts);
    }

    IEnumerable<int> Under(int root)
    {
        var stack = new Stack<int>([root]);
        while (stack.TryPop(out int i))
        {
            yield return i;
            if (kids[i] is { } k) foreach (int c in k) stack.Push(c);
        }
    }

    // Between rebuilds, kids may still list records the journal has since freed.
    IEnumerable<int> FilesUnder(int root) => Under(root).Where(i => !nodes[i].Dir && nodes[i].Live && nodes[i].Name != null);

    static string Ext(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..].ToLowerInvariant() : "(none)";
    }

    static string? Date(long fileTime) =>
        fileTime is > 0 and <= MaxFileTime ? DateTime.FromFileTimeUtc(fileTime).ToString("yyyy-MM-dd") : null;

    const long MaxFileTime = 2650467743999999999; // 9999-12-31, the DateTime limit

    static string Human(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        int u = 0;
        for (; v >= 1024 && u < units.Length - 1; u++) v /= 1024;
        return $"{v:0.#} {units[u]}";
    }
}
