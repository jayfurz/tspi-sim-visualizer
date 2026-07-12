using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tspi.Sim.Models;

namespace Tspi.Cli.Commands;

/// <summary>Tiny positional + --flag parser shared by all commands.</summary>
public sealed class ArgParse
{
    private readonly List<string> _positionals = new();
    private readonly Dictionary<string, string> _options = new();
    private readonly HashSet<string> _switches = new();

    public ArgParse(string[] args, HashSet<string> knownSwitches)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal) || a.StartsWith("-", StringComparison.Ordinal))
            {
                string key = a.TrimStart('-');
                if (knownSwitches.Contains(key)) { _switches.Add(key); continue; }
                if (i + 1 >= args.Length) throw new CliError("option '" + a + "' needs a value");
                _options[key] = args[++i];
            }
            else _positionals.Add(a);
        }
    }

    public string Positional(int index, string name)
    {
        if (index >= _positionals.Count) throw new CliError("missing argument: " + name);
        return _positionals[index];
    }

    public int PositionalCount => _positionals.Count;
    public bool Switch(string name) => _switches.Contains(name);
    public string? Option(string name) => _options.TryGetValue(name, out var v) ? v : null;
    public string Option(string name, string fallback) => _options.TryGetValue(name, out var v) ? v : fallback;

    /// <summary>Alias resolution: -o == --out, -j == --jobs.</summary>
    public string? OptionAny(params string[] names)
    {
        foreach (var n in names)
            if (_options.TryGetValue(n, out var v)) return v;
        return null;
    }
}

public static class CliCommon
{
    /// <summary>Build a ModelLibrary from --models dirs plus the built-in ./models next to the manifest.</summary>
    public static ModelLibrary Models(string? modelsOpt, string manifestPath)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(modelsOpt))
            dirs.AddRange(modelsOpt.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        // Default search: ./models under CWD and alongside the manifest.
        dirs.Add(Path.Combine(Directory.GetCurrentDirectory(), "models"));
        string? manDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        if (!string.IsNullOrEmpty(manDir)) dirs.Add(Path.Combine(manDir, "models"));
        return new ModelLibrary(dirs.Distinct());
    }

    public static void RequireFile(string path, string what)
    {
        if (!File.Exists(path)) throw new CliError(what + " not found: " + path);
    }
}
