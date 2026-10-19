using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Media;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>A saved look: one unit's lighting plus, optionally, one screen's content settings.</summary>
public sealed class FlexTemplate
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public LianLiFlexUnitSettings? Lighting { get; set; }
    public LianLiWirelessScreenSettings? Screen { get; set; }
}

/// <summary>
/// Templates as one JSON file each. Export is a ZIP holding a single template.json; import takes
/// only that shape and refuses anything else (extra entries, oversized entries, a newer version).
/// A template never carries media or sprite files, only references to them.
/// </summary>
public sealed class FlexTemplateStore
{
    public const int MaxJsonBytes = 256 * 1024;
    public const int MaxNameLength = 80;
    public const int MaxTemplates = 200;
    private const string EntryName = "template.json";

    private readonly string _root;
    private readonly object _gate = new();

    public FlexTemplateStore()
        : this(MediaLibrary.DeviceStoreDir("lianli-flex-templates"))
    {
    }

    internal FlexTemplateStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public IReadOnlyList<FlexTemplate> List()
    {
        lock (_gate)
        {
            return Directory.GetFiles(_root, "*.json")
                .OrderByDescending(File.GetCreationTimeUtc)
                .Select(Read)
                .OfType<FlexTemplate>()
                .ToList();
        }
    }

    public FlexTemplate? Get(string id)
    {
        if (!MediaLibrary.IsValidId(id))
        {
            return null;
        }
        lock (_gate)
        {
            var path = PathOf(id);
            return File.Exists(path) ? Read(path) : null;
        }
    }

    /// <summary>Stores a copy under a fresh id; an error text when the template is unusable.</summary>
    public (FlexTemplate? Template, string? Error) Save(FlexTemplate template)
    {
        var name = (template.Name ?? "").Trim();
        if (name.Length == 0 || name.Length > MaxNameLength)
        {
            return (null, "invalid name");
        }
        if (template.Lighting is null && template.Screen is null)
        {
            return (null, "empty template");
        }
        lock (_gate)
        {
            if (Directory.GetFiles(_root, "*.json").Length >= MaxTemplates)
            {
                return (null, "too many templates");
            }
            var copy = Roundtrip(template)!;
            copy.Version = 1;
            copy.Name = name;
            copy.Id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{Guid.NewGuid().ToString("N")[..6]}";
            if (copy.Lighting is not null)
            {
                copy.Lighting.PackageSignature = null;
                copy.Lighting.PackageEffectIndex = null;
            }
            File.WriteAllText(PathOf(copy.Id), JsonSerializer.Serialize(copy, PersistenceJsonContext.Default.FlexTemplate));
            return (copy, null);
        }
    }

    public bool Delete(string id)
    {
        if (!MediaLibrary.IsValidId(id))
        {
            return false;
        }
        lock (_gate)
        {
            var path = PathOf(id);
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            return true;
        }
    }

    public byte[]? Export(string id)
    {
        var template = Get(id);
        if (template is null)
        {
            return null;
        }
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(EntryName);
            using var writer = entry.Open();
            JsonSerializer.Serialize(writer, template, PersistenceJsonContext.Default.FlexTemplate);
        }
        return ms.ToArray();
    }

    /// <summary>Imports an exported ZIP as a new template.</summary>
    public (FlexTemplate? Template, string? Error) Import(byte[] zipBytes)
    {
        FlexTemplate? parsed;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            if (zip.Entries.Count != 1 || zip.Entries[0].FullName != EntryName)
            {
                return (null, "not a Nexus template");
            }
            var entry = zip.Entries[0];
            if (entry.Length > MaxJsonBytes)
            {
                return (null, "template too large");
            }
            using var stream = entry.Open();
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                bounded.Write(buffer, 0, read);
                if (bounded.Length > MaxJsonBytes)
                {
                    return (null, "template too large");
                }
            }
            parsed = JsonSerializer.Deserialize(bounded.ToArray(), PersistenceJsonContext.Default.FlexTemplate);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
        {
            return (null, "not a Nexus template");
        }
        if (parsed is null || parsed.Version != 1)
        {
            return (null, "unsupported template version");
        }
        return Save(parsed);
    }

    /// <summary>A deep copy of the template's lighting for a unit; null when it has none.</summary>
    public static LianLiFlexUnitSettings? LightingOf(FlexTemplate template) => Roundtrip(template)?.Lighting;

    public static LianLiWirelessScreenSettings? ScreenOf(FlexTemplate template) => Roundtrip(template)?.Screen;

    private static FlexTemplate? Roundtrip(FlexTemplate t) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(t, PersistenceJsonContext.Default.FlexTemplate), PersistenceJsonContext.Default.FlexTemplate);

    private string PathOf(string id) => Path.Combine(_root, id + ".json");

    private static FlexTemplate? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), PersistenceJsonContext.Default.FlexTemplate);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}
