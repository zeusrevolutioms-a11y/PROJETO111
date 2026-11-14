
using System;
using System.Collections.Generic;
using System.IO;
using ZeusMapEditor.Core;

namespace ZeusMapEditor.Core;

public sealed class ImgEntry
{
    public string Name = "";
    public long Offset;
    public int Size;
}

public sealed class ImgArchive
{
    public string Path { get; }

    public List<ImgEntry> Entries { get; } = new();

    private readonly Dictionary<string, ImgEntry> lookup =
        new(StringComparer.OrdinalIgnoreCase);

    private ImgArchive(string path)
    {
        Path = path;
    }

    public static ImgArchive? Open(string imgPath)
    {
        using var fs = new FileStream(
            imgPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
        );

        using var br = new BinaryReader(fs);

        var archive = new ImgArchive(imgPath);

        if (fs.Length < 8)
            return null;

        byte[] header = br.ReadBytes(8);

        if (header[0] == 'V' &&
            header[1] == 'E' &&
            header[2] == 'R' &&
            header[3] == '2')
        {
            int count = BitConverter.ToInt32(header, 4);

            if (count < 0 || count > 500000 ||
                8L + count * 32L > fs.Length)
                return null;

            for (int i = 0; i < count; i++)
            {
                byte[] entry = br.ReadBytes(32);

                uint offset = BitConverter.ToUInt32(entry, 0);
                ushort size = BitConverter.ToUInt16(entry, 4);

                archive.AddEntry(
                    Bin.Str(entry, 8, 24),
                    (long)offset * 2048,
                    (long)size * 2048,
                    fs.Length
                );
            }

            return archive;
        }

        string dir = System.IO.Path.ChangeExtension(
            imgPath,
            ".dir"
        );

        if (!File.Exists(dir))
            return null;

        byte[] data = File.ReadAllBytes(dir);

        for (int p = 0; p + 32 <= data.Length; p += 32)
        {
            uint offset = BitConverter.ToUInt32(data, p);
            uint size = BitConverter.ToUInt32(data, p + 4);

            archive.AddEntry(
                Bin.Str(data, p + 8, 24),
                (long)offset * 2048,
                (long)size * 2048,
                fs.Length
            );
        }

        return archive;
    }

    private void AddEntry(
        string name,
        long offset,
        long size,
        long archiveLength)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (offset < 0 || size <= 0 ||
            offset > archiveLength ||
            size > archiveLength - offset ||
            size > int.MaxValue)
            return;

        var entry = new ImgEntry
        {
            Name = name.Trim(),
            Offset = offset,
            Size = (int)size
        };

        Entries.Add(entry);
        lookup[entry.Name] = entry;
    }

    public bool TryGetEntry(string name, out ImgEntry entry)
    {
        return lookup.TryGetValue(name, out entry!);
    }

    public byte[] Read(ImgEntry entry)
    {
        if (entry.Offset < 0 || entry.Size <= 0)
            throw new InvalidDataException("Entrada IMG inválida.");

        using var fs = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
        );

        if (entry.Offset > fs.Length ||
            entry.Size > fs.Length - entry.Offset)
            throw new InvalidDataException("Entrada fora do arquivo IMG.");

        fs.Position = entry.Offset;

        byte[] buffer = new byte[entry.Size];

        fs.ReadExactly(buffer);

        return buffer;
    }
}
