using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tspi.Sim.Engine;
using Tspi.Sim.Manifest;
using Tspi.Sim.Models;

namespace Tspi.Cli.Commands;

/// <summary>
/// tspi serve — the web viewer's backend: serves web/viewer over http plus
/// run/validate endpoints, keeping the browser a pure UI shell (the edit-loop
/// counterpart of Unity's ScenarioEditController, which shells out to this CLI).
///
///   GET  /, /index.html, /app.js, /tspi.js   the viewer (whitelisted files only)
///   GET  /files/&lt;path&gt;.tspi                  read-only .tspi under --root
///   POST /api/validate                        manifest JSON -> {valid, errors, warnings}
///   POST /api/run[?seed=N]                    manifest JSON -> run to --out-dir,
///                                             {file, viewer, events, ...}
///   GET  /api/version
///
/// The loop: POST an edited manifest to /api/run, open the returned
/// `viewer` URL (`/?file=...&amp;t=&lt;sec&gt;` deep link) — determinism makes the
/// re-run resume seamless. Binds 127.0.0.1 by default; pass --bind to expose.
/// </summary>
public static class ServeCommand
{
    public static int Run(string[] args)
    {
        var p = new ArgParse(args, new HashSet<string>());
        if (!int.TryParse(p.Option("port", "8080"), out int port) || port < 1 || port > 65535)
            throw new CliError("--port must be 1..65535");
        string bind = p.Option("bind", "127.0.0.1");
        string root = Path.GetFullPath(p.Option("root", Directory.GetCurrentDirectory()));
        if (!Directory.Exists(root)) throw new CliError("serve root not found: " + root);
        string outDir = Path.GetFullPath(p.OptionAny("out-dir", "o") ?? Path.Combine(root, "runs", "serve"));
        string viewer = p.Option("viewer") ?? FindViewerDir()
            ?? throw new CliError("web/viewer not found near cwd or the executable; pass --viewer DIR");
        if (!File.Exists(Path.Combine(viewer, "index.html")))
            throw new CliError("not a viewer dir (no index.html): " + viewer);

        using var server = new TspiHttpServer(new TspiHttpServer.Config
        {
            Bind = bind, Port = port, ViewerDir = viewer,
            FilesRoot = root, OutDir = outDir, ModelsOption = p.Option("models"),
        });
        server.Start();

        Console.WriteLine($"tspi serve — {server.BaseUrl}");
        Console.WriteLine($"  viewer:  {viewer}");
        Console.WriteLine($"  files:   /files/** -> {root} (*.tspi, read-only)");
        Console.WriteLine($"  api:     POST /api/validate, POST /api/run (runs -> {outDir})");
        if (p.PositionalCount > 0)
        {
            string open = Path.GetFullPath(p.Positional(0, "file.tspi"));
            CliCommon.RequireFile(open, "file");
            string rel = Path.GetRelativePath(root, open);
            if (rel.StartsWith("..", StringComparison.Ordinal))
                Console.WriteLine($"  warning: {open} is outside the serve root, not linkable");
            else
                Console.WriteLine($"  open:    {server.BaseUrl}?file=/files/{rel.Replace('\\', '/')}");
        }
        Console.WriteLine("Ctrl+C to stop.");

        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        done.Wait();
        return 0;
    }

    /// <summary>Walk up from cwd, then the executable, looking for web/viewer/index.html.</summary>
    private static string? FindViewerDir()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string cand = Path.Combine(dir.FullName, "web", "viewer");
                if (File.Exists(Path.Combine(cand, "index.html"))) return cand;
            }
        }
        return null;
    }
}

/// <summary>The http server behind `tspi serve`; separate from the verb so tests can
/// drive it on an arbitrary port. HttpListener only — no packages, same as the rest
/// of the repo. Requests are handled concurrently; run outputs get unique names.</summary>
public sealed class TspiHttpServer : IDisposable
{
    public sealed class Config
    {
        public string Bind = "127.0.0.1";
        public int Port = 8080;
        public string ViewerDir = "";
        public string FilesRoot = "";
        public string OutDir = "";
        public string? ModelsOption;
    }

    private const long MaxBodyBytes = 16 * 1024 * 1024;
    private static readonly Dictionary<string, string> StaticFiles = new()
    {
        [""] = "index.html", ["index.html"] = "index.html",
        ["app.js"] = "app.js", ["tspi.js"] = "tspi.js",
    };
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly Config _cfg;
    private readonly HttpListener _listener = new();
    private Thread? _thread;
    private int _runCounter;

    public string BaseUrl => $"http://{_cfg.Bind}:{_cfg.Port}/";

    public TspiHttpServer(Config cfg)
    {
        _cfg = cfg;
        _cfg.FilesRoot = Path.GetFullPath(_cfg.FilesRoot);
        _cfg.OutDir = Path.GetFullPath(_cfg.OutDir);
        if (!PathIsUnder(_cfg.OutDir, _cfg.FilesRoot))
            throw new CliError("--out-dir must be under the serve root (so runs are reachable at /files/)");
        _listener.Prefixes.Add(BaseUrl);
    }

    public void Start()
    {
        _listener.Start();
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "tspi-serve" };
        _thread.Start();
    }

    public void Dispose()
    {
        if (_listener.IsListening) _listener.Stop();
        _listener.Close();
    }

    private void AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.Headers["Access-Control-Allow-Origin"] = "*";
            res.Headers["Cache-Control"] = "no-store";
            string path = req.Url?.AbsolutePath ?? "/";
            if (req.HttpMethod == "OPTIONS")
            {
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                res.StatusCode = 204;
                res.Close();
                return;
            }
            string name = path.TrimStart('/');
            if (req.HttpMethod == "GET" && StaticFiles.TryGetValue(name, out var file))
                ServeStatic(res, file);
            else if (req.HttpMethod == "GET" && path.StartsWith("/files/", StringComparison.Ordinal))
                ServeTspiFile(res, path["/files/".Length..]);
            else if (req.HttpMethod == "POST" && path == "/api/validate")
                ApiValidate(req, res);
            else if (req.HttpMethod == "POST" && path == "/api/run")
                ApiRun(req, res);
            else if (req.HttpMethod == "GET" && path == "/api/version")
                Json(res, 200, new { version = Tspi.Sim.SimInfo.Version });
            else
                Json(res, 404, new { error = "not found: " + req.HttpMethod + " " + path });
        }
        catch (Exception ex)
        {
            try { Json(res, 500, new { error = ex.Message }); } catch { /* client gone */ }
        }
    }

    private void ServeStatic(HttpListenerResponse res, string file)
    {
        byte[] body = File.ReadAllBytes(Path.Combine(_cfg.ViewerDir, file));
        res.ContentType = file.EndsWith(".js", StringComparison.Ordinal)
            ? "text/javascript; charset=utf-8" : "text/html; charset=utf-8";
        Write(res, 200, body);
    }

    /// <summary>Read-only .tspi access under FilesRoot; anything else is 404.</summary>
    private void ServeTspiFile(HttpListenerResponse res, string relEscaped)
    {
        string rel = Uri.UnescapeDataString(relEscaped);
        string full = Path.GetFullPath(Path.Combine(_cfg.FilesRoot, rel));
        if (rel.Length == 0 || !PathIsUnder(full, _cfg.FilesRoot)
            || !full.EndsWith(".tspi", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            Json(res, 404, new { error = "no such .tspi under the serve root" });
            return;
        }
        res.ContentType = "application/octet-stream";
        Write(res, 200, File.ReadAllBytes(full));
    }

    private void ApiValidate(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!TryLoadManifest(req, res, out var manifest, out _)) return;
        var v = ManifestValidator.Validate(manifest, BuildModels());
        Json(res, 200, new { valid = v.IsValid, errors = v.Errors, warnings = v.Warnings });
    }

    private void ApiRun(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!TryLoadManifest(req, res, out var manifest, out byte[] raw)) return;
        if (req.QueryString["seed"] is { } seedStr)
        {
            if (!ulong.TryParse(seedStr, out ulong seed))
            {
                Json(res, 400, new { error = "?seed must be a non-negative integer" });
                return;
            }
            manifest.Seed = seed;
        }
        var models = BuildModels();
        var v = ManifestValidator.Validate(manifest, models);
        if (!v.IsValid)
        {
            Json(res, 422, new { valid = false, errors = v.Errors, warnings = v.Warnings });
            return;
        }

        string safe = Sanitize(manifest.Name);
        string fileName = $"{safe}-seed{manifest.Seed}-{Interlocked.Increment(ref _runCounter):000}.tspi";
        Directory.CreateDirectory(_cfg.OutDir);
        string outPath = Path.Combine(_cfg.OutDir, fileName);
        var sw = Stopwatch.StartNew();
        var summary = SceneEngine.RunScenarioToFile(
            manifest, models, outPath, ManifestJson.Sha256Bytes(raw), ManifestJson.Sha256Hex(raw));
        sw.Stop();

        string fileUrl = "/files/" + Path.GetRelativePath(_cfg.FilesRoot, outPath).Replace('\\', '/');
        Json(res, 200, new
        {
            ok = true,
            file = fileUrl,
            viewer = "/?file=" + Uri.EscapeDataString(fileUrl),
            seed = manifest.Seed,
            entities = summary.EntityCount,
            samples = summary.SampleCount,
            size_bytes = new FileInfo(outPath).Length,
            elapsed_ms = sw.Elapsed.TotalMilliseconds,
            warnings = v.Warnings,
            events = summary.Events.Select(ev => new Dictionary<string, object?>
            {
                ["t_s"] = ev.TNs / 1e9,
                ["kind"] = ev.Kind,
                ["src"] = ev.SrcOrd.HasValue ? summary.OrdToId.GetValueOrDefault(ev.SrcOrd.Value) : null,
                ["dst"] = ev.DstOrd.HasValue ? summary.OrdToId.GetValueOrDefault(ev.DstOrd.Value) : null,
                ["miss_m"] = ev.Data.TryGetValue("miss_m", out var m) ? m : null,
            }),
        });
    }

    private bool TryLoadManifest(HttpListenerRequest req, HttpListenerResponse res,
        out ScenarioManifest manifest, out byte[] raw)
    {
        manifest = null!;
        raw = Array.Empty<byte>();
        if (req.ContentLength64 > MaxBodyBytes)
        {
            Json(res, 413, new { error = "manifest larger than 16 MiB" });
            return false;
        }
        using var ms = new MemoryStream();
        req.InputStream.CopyTo(ms);
        raw = ms.ToArray();
        try
        {
            var parsed = JsonSerializer.Deserialize<ScenarioManifest>(raw, ManifestJson.Options);
            if (parsed == null) throw new JsonException("manifest is JSON null");
            manifest = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            Json(res, 400, new { error = "manifest JSON: " + ex.Message });
            return false;
        }
    }

    /// <summary>Per request, so model-file edits are picked up mid-serve — same default
    /// search as the other verbs minus the manifest-adjacent dir (there is no manifest
    /// path; POSTed manifests resolve against --models, cwd, and the serve root).</summary>
    private ModelLibrary BuildModels()
    {
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(_cfg.ModelsOption))
            dirs.AddRange(_cfg.ModelsOption.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(Path.Combine(Directory.GetCurrentDirectory(), "models"));
        dirs.Add(Path.Combine(_cfg.FilesRoot, "models"));
        return new ModelLibrary(dirs.Distinct());
    }

    private static bool PathIsUnder(string full, string root)
    {
        return full == root
            || full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        string s = new string(chars).Trim('-', '.');
        return s.Length == 0 ? "scenario" : s;
    }

    private static void Json(HttpListenerResponse res, int status, object payload)
    {
        Write(res, status, JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts),
            "application/json; charset=utf-8");
    }

    private static void Write(HttpListenerResponse res, int status, byte[] body, string? contentType = null)
    {
        res.StatusCode = status;
        if (contentType != null) res.ContentType = contentType;
        res.ContentLength64 = body.Length;
        res.OutputStream.Write(body);
        res.Close();
    }
}
