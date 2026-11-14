using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZeusMapEditor.Core;

public sealed class AssetLibrary
{
    private readonly ConcurrentDictionary<string, Func<byte[]>> dffSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Func<byte[]>> txdSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ModelGeometry> dffCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CollisionModel> colCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> failedDffs = new(StringComparer.OrdinalIgnoreCase);

    // Texturas guardadas POR TXD (o mesmo nome de textura existe em vários TXD com imagens diferentes).
    private readonly ConcurrentDictionary<string, Dictionary<string, TxdTexture>> txdTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> failedTxds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> modelTxd = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, TxdTexture> globalTextures = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Func<byte[]>> pendingCols = new();
    private readonly object sync = new();

    public int DffCount => dffSources.Count;
    public int CollisionCount => colCache.Count;
    public int TxdFileCount => txdSources.Count;
    public int TextureCount => txdTextures.Values.Sum(d => d.Count);
    public int TxdLoadedCount => txdTextures.Count;
    public ConcurrentBag<string> Unreadable { get; } = new();

    public bool HasSource(string name) => dffSources.ContainsKey(name);
    public bool HasTxd(string name) => txdSources.ContainsKey(name);

    public ModelGeometry? GetGeometry(string name) => dffCache.TryGetValue(name, out var g) ? g : null;

    public CollisionModel? GetCollision(string name) =>
        colCache.TryGetValue(name, out var c) && c.HasData ? c : null;

    // ------------------------------------------------------------ TXD de cada modelo (vem do IDE)
    public void SetModelTxds(IEnumerable<KeyValuePair<string, string>> map)
    {
        foreach (var kv in map)
            if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                modelTxd[kv.Key] = kv.Value.Trim();
    }

    /// <summary>Nome do TXD do modelo (IDE). Sem IDE, assume o próprio nome do modelo.</summary>
    public string GetModelTxd(string model) =>
        modelTxd.TryGetValue(model, out var t) ? t : model;

    private static string CleanTextureName(string name)
    {
        name = (name ?? "").Trim().Replace('\\', '/');
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Procura a textura primeiro no TXD do modelo; depois em qualquer TXD carregado.</summary>
    public TxdTexture? GetTexture(string model, string textureName)
    {
        string tex = CleanTextureName(textureName);
        if (tex.Length == 0) return null;
        if (txdTextures.TryGetValue(GetModelTxd(model), out var dict) && dict.TryGetValue(tex, out var t)) return t;
        return globalTextures.TryGetValue(tex, out t) ? t : null;
    }

    // ------------------------------------------------------------ indexar arquivos
    public int AddFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            string path = file;
            if (ext == ".dff") { dffSources[Path.GetFileNameWithoutExtension(file)] = () => File.ReadAllBytes(path); count++; }
            else if (ext == ".col") { lock (sync) pendingCols.Add(() => File.ReadAllBytes(path)); count++; }
            else if (ext == ".txd") { txdSources[Path.GetFileNameWithoutExtension(file)] = () => File.ReadAllBytes(path); count++; }
            else if (ext == ".img") count += AddImg(file);
        }
        return count;
    }

    public int AddImg(string path)
    {
        var img = ImgArchive.Open(path);
        if (img == null) return 0;
        int count = 0;
        foreach (var entry in img.Entries)
        {
            var e = entry;
            if (e.Name.EndsWith(".dff", StringComparison.OrdinalIgnoreCase))
            { dffSources[Path.GetFileNameWithoutExtension(e.Name)] = () => img.Read(e); count++; }
            else if (e.Name.EndsWith(".col", StringComparison.OrdinalIgnoreCase))
            { lock (sync) pendingCols.Add(() => img.Read(e)); count++; }
            else if (e.Name.EndsWith(".txd", StringComparison.OrdinalIgnoreCase))
            { txdSources[Path.GetFileNameWithoutExtension(e.Name)] = () => img.Read(e); count++; }
        }
        return count;
    }

    // ------------------------------------------------------------ pré-carregamento
    public Task PreloadAsync(IEnumerable<string> names, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var wanted = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // COL
            List<Func<byte[]>> cols;
            lock (sync) { cols = pendingCols.ToList(); pendingCols.Clear(); }
            Parallel.ForEach(cols, new ParallelOptions { CancellationToken = ct }, source =>
            {
                try { foreach (var m in ColReader.ReadAll(source())) if (!string.IsNullOrWhiteSpace(m.Name)) colCache[m.Name] = m; }
                catch { }
            });
            ct.ThrowIfCancellationRequested();

            // DFF
            int done = 0;
            Parallel.ForEach(wanted, new ParallelOptions { CancellationToken = ct }, name =>
            {
                if (!dffCache.ContainsKey(name) && !failedDffs.ContainsKey(name) && dffSources.TryGetValue(name, out var source))
                {
                    try
                    {
                        var g = DffReader.Read(source());
                        if (g != null) dffCache[name] = g;
                        else { failedDffs.TryAdd(name, 0); Unreadable.Add(name); }
                    }
                    catch { failedDffs.TryAdd(name, 0); Unreadable.Add(name); }
                }
                int c = Interlocked.Increment(ref done);
                if (c % 50 == 0 || c == wanted.Count) progress?.Report($"Modelos: {c}/{wanted.Count}");
            });
            ct.ThrowIfCancellationRequested();

            // TXD: só os que os modelos pedidos usam (carregar tudo ocuparia gigabytes de memória)
            var neededTxds = wanted.Select(GetModelTxd)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => txdSources.ContainsKey(t) && !txdTextures.ContainsKey(t) && !failedTxds.ContainsKey(t))
                .ToList();

            int txdDone = 0;
            Parallel.ForEach(neededTxds, new ParallelOptions { CancellationToken = ct }, txd =>
            {
                try
                {
                    var dict = new Dictionary<string, TxdTexture>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tex in TxdReader.Read(txdSources[txd]()))
                    {
                        string n = CleanTextureName(tex.Name);
                        if (n.Length > 0) dict.TryAdd(n, tex);
                    }
                    if (dict.Count > 0) txdTextures[txd] = dict; else failedTxds.TryAdd(txd, 0);
                }
                catch { failedTxds.TryAdd(txd, 0); }
                int c = Interlocked.Increment(ref txdDone);
                if (c % 10 == 0 || c == neededTxds.Count) progress?.Report($"Texturas (TXD): {c}/{neededTxds.Count}");
            });

            // Índice global (plano B) — ordem fixa por nome do TXD, para o resultado não variar a cada execução.
            var global = new Dictionary<string, TxdTexture>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in txdTextures.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                foreach (var t in kv.Value) global.TryAdd(t.Key, t.Value);
            globalTextures = global;
        }, ct);
    }
}
