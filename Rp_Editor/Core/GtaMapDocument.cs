using System.Globalization;
using System.Text;

namespace ZeusMapEditor.Core;

/// <summary>Um arquivo IPL carregado (guarda o texto original para preservar o que não é "inst").</summary>
public sealed class IplFile
{
    public string Path = "";
    public List<string> Original = new();
    public List<string> Kept = new();   // linhas "inst" desconhecidas/comentários
    public string Baseline = "";        // texto gerado ao carregar: serve para saber se mudou
}

public sealed class GtaMapDocument
{
    public List<IplFile> Files { get; } = new();
    public List<MapObject> Objects { get; } = new();
    public Dictionary<int, string> ModelNames { get; } = new();
    public Dictionary<string, int> ModelIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Modelo -> nome do TXD (3º campo do IDE).</summary>
    public Dictionary<string, string> ModelTxds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int BinaryFiles { get; private set; }

    public string? IplPath => Files.Count == 1 ? Files[0].Path : null;
    public int SkippedLines => Files.Sum(f => f.Kept.Count(l => !IsCommentOrEmpty(l)));

    // ---------------------------------------------------------------- IDE
    public int LoadIde(string path)
    {
        int count = 0;
        bool active = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.Equals("objs", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("tobj", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("anim", StringComparison.OrdinalIgnoreCase)) { active = true; continue; }
            if (line.Equals("end", StringComparison.OrdinalIgnoreCase)) { active = false; continue; }
            if (!active) continue;
            var p = line.Split(',').Select(x => x.Trim()).ToArray();
            if (p.Length >= 3 && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                ModelNames[id] = p[1];
                ModelIds[p[1]] = id;
                if (p[2].Length > 0) ModelTxds[p[1]] = p[2];
                count++;
            }
        }
        return count;
    }

    // ---------------------------------------------------------------- IPL
    public void Clear() { Files.Clear(); Objects.Clear(); BinaryFiles = 0; }

    /// <summary>Abre um único IPL, substituindo o que estava carregado.</summary>
    public void LoadIpl(string path) { Clear(); AddIpl(path); }

    /// <summary>Acrescenta um IPL de texto. Retorna false para IPL binário (não suportado).</summary>
    public bool AddIpl(string path)
    {
        using (var fs = File.OpenRead(path))
        {
            var head = new byte[4];
            if (fs.Read(head, 0, 4) == 4 && head[0] == 'b' && head[1] == 'n' && head[2] == 'r' && head[3] == 'y')
            { BinaryFiles++; return false; }
        }
        var file = new IplFile { Path = path, Original = File.ReadAllLines(path).ToList() };
        var objects = new List<MapObject>();
        bool inst = false;
        foreach (var raw in file.Original)
        {
            var line = StripComment(raw).Trim();
            if (line.Equals("inst", StringComparison.OrdinalIgnoreCase)) { inst = true; continue; }
            if (line.Equals("end", StringComparison.OrdinalIgnoreCase)) { inst = false; continue; }
            if (!inst) continue;
            if (line.Length == 0) { if (raw.Trim().Length > 0) file.Kept.Add(raw); continue; }
            if (TryParseInst(line, out var obj)) { obj!.SourceFile = path; objects.Add(obj); }
            else file.Kept.Add(raw);
        }
        Files.Add(file);
        Objects.AddRange(objects);
        file.Baseline = string.Join("\n", Build(file, objects));
        return true;
    }

    private static bool TryParseInst(string line, out MapObject? obj)
    {
        obj = null;
        var p = line.Split(',').Select(x => x.Trim()).ToArray();
        if (p.Length != 11) return false; // SA: id, modelo, interior, x,y,z, rx,ry,rz,rw, lod
        var ci = CultureInfo.InvariantCulture;
        if (!int.TryParse(p[0], NumberStyles.Integer, ci, out int id) ||
            !int.TryParse(p[2], NumberStyles.Integer, ci, out int interior) ||
            !int.TryParse(p[10], NumberStyles.Integer, ci, out int lod)) return false;
        var f = new float[7];
        for (int i = 0; i < 7; i++)
            if (!float.TryParse(p[3 + i], NumberStyles.Float, ci, out f[i])) return false;
        obj = new MapObject
        {
            Id = id, ModelName = p[1], Interior = interior,
            X = f[0], Y = f[1], Z = f[2], RotX = f[3], RotY = f[4], RotZ = f[5], RotW = f[6], Lod = lod
        };
        return true;
    }

    // ---------------------------------------------------------------- salvar
    private static List<string> Build(IplFile file, IEnumerable<MapObject> objs)
    {
        var output = new List<string>();
        bool wroteInst = false, skippingInst = false;
        void WriteInst()
        {
            output.Add("inst");
            output.AddRange(file.Kept);
            foreach (var o in objs) output.Add(Format(o));
            output.Add("end");
        }
        foreach (var line in file.Original)
        {
            var t = StripComment(line).Trim();
            if (t.Equals("inst", StringComparison.OrdinalIgnoreCase))
            {
                if (!wroteInst) { WriteInst(); wroteInst = true; }
                skippingInst = true;
                continue;
            }
            if (skippingInst)
            {
                if (t.Equals("end", StringComparison.OrdinalIgnoreCase)) skippingInst = false;
                continue;
            }
            output.Add(line);
        }
        if (!wroteInst) WriteInst();
        return output;
    }

    /// <summary>IPLs cujo conteúdo mudou desde que foram carregados/salvos.</summary>
    public List<IplFile> ChangedFiles()
    {
        var by = Objects.ToLookup(o => o.SourceFile, StringComparer.OrdinalIgnoreCase);
        return Files.Where(f => string.Join("\n", Build(f, by[f.Path])) != f.Baseline).ToList();
    }

    public void SaveFile(IplFile f)
    {
        var lines = Build(f, Objects.Where(o => string.Equals(o.SourceFile, f.Path, StringComparison.OrdinalIgnoreCase)));
        File.WriteAllLines(f.Path, lines, new UTF8Encoding(false));
        f.Original = lines;
        f.Baseline = string.Join("\n", lines);
    }

    // ---------------------------------------------------------------- edição
    public void Add(MapObject obj) => Objects.Add(obj);
    public void Remove(MapObject obj) => Objects.Remove(obj);
    public void ReplaceObjects(IEnumerable<MapObject> items)
    {
        var list = items.ToList();
        Objects.Clear();
        Objects.AddRange(list);
    }

    // ---------------------------------------------------------------- util
    private static string StripComment(string s)
    {
        int i = s.IndexOf('#');
        if (i >= 0) s = s[..i];
        i = s.IndexOf("//", StringComparison.Ordinal);
        if (i >= 0) s = s[..i];
        return s;
    }
    private static bool IsCommentOrEmpty(string s) => StripComment(s).Trim().Length == 0;
    private static string N(float f) => f.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Format(MapObject o) =>
        $"{o.Id}, {o.ModelName}, {o.Interior}, {N(o.X)}, {N(o.Y)}, {N(o.Z)}, {N(o.RotX)}, {N(o.RotY)}, {N(o.RotZ)}, {N(o.RotW)}, {o.Lod}";
}
