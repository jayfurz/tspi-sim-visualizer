using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Tspi.Cli.Commands;
using Xunit;

namespace Tspi.Tests;

/// <summary>
/// Integration tests for the `tspi serve` http server: real HttpListener on a random
/// loopback port, real HttpClient, real sim runs. Rooted at the repo so the committed
/// golden fixture is servable and schemas/examples manifests are runnable.
/// </summary>
public sealed class ServeFixture : IDisposable
{
    public TspiHttpServer Server { get; }
    public HttpClient Client { get; }
    public string RepoRoot { get; }
    public string OutDir { get; }

    public ServeFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "schemas")))
            dir = dir.Parent;
        RepoRoot = dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        OutDir = Path.Combine(RepoRoot, "runs", "serve-tests-" + Guid.NewGuid().ToString("N"));

        var rng = new Random();
        TspiHttpServer? server = null;
        for (int attempt = 0; server == null; attempt++)
        {
            var cfg = new TspiHttpServer.Config
            {
                Port = rng.Next(20000, 45000),
                ViewerDir = Path.Combine(RepoRoot, "web", "viewer"),
                FilesRoot = RepoRoot,
                OutDir = OutDir,
            };
            try
            {
                server = new TspiHttpServer(cfg);
                server.Start();
            }
            catch (HttpListenerException) when (attempt < 25)
            {
                server?.Dispose();
                server = null; // port taken; roll again
            }
        }
        Server = server;
        Client = new HttpClient { BaseAddress = new Uri(Server.BaseUrl) };
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        if (Directory.Exists(OutDir)) Directory.Delete(OutDir, recursive: true);
    }
}

public class ServeTests : IClassFixture<ServeFixture>
{
    private readonly ServeFixture _f;

    public ServeTests(ServeFixture f) => _f = f;

    private static JsonElement ParseJson(string s) => JsonDocument.Parse(s).RootElement;

    private StringContent Manifest(string name) => new(
        File.ReadAllText(Path.Combine(_f.RepoRoot, "schemas", "examples", name)),
        Encoding.UTF8, "application/json");

    [Fact]
    public async Task ServesViewerStatics()
    {
        var index = await _f.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.StartsWith("text/html", index.Content.Headers.ContentType!.ToString());
        Assert.Contains("app.js", await index.Content.ReadAsStringAsync());

        var reader = await _f.Client.GetStringAsync("/tspi.js");
        Assert.Contains("TSPIFTR1", reader); // the actual format reader, not an error page
    }

    [Fact]
    public async Task ServesTspiFilesReadOnlyUnderRoot()
    {
        var bytes = await _f.Client.GetByteArrayAsync(
            "/files/tools/tspi_py/tests/data/golden-v1.tspi");
        Assert.Equal("TSPI", Encoding.ASCII.GetString(bytes, 0, 4));

        // Scenarios (.json) are exposed read-only too — the editor's ?scenario= source.
        var scen = await _f.Client.GetAsync("/files/schemas/examples/golden.json");
        Assert.Equal(HttpStatusCode.OK, scen.StatusCode);
        Assert.StartsWith("application/json", scen.Content.Headers.ContentType!.ToString());

        // Only .tspi/.json are exposed — an existing README must 404, as must escapes
        // from the root and POSTs to /files/.
        Assert.Equal(HttpStatusCode.NotFound,
            (await _f.Client.GetAsync("/files/README.md")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _f.Client.GetAsync("/files/%2e%2e/outside/x.tspi")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _f.Client.PostAsync("/files/tools/tspi_py/tests/data/golden-v1.tspi",
                new StringContent(""))).StatusCode);
    }

    [Fact]
    public async Task ValidateEndpointAcceptsGoldenRejectsGarbage()
    {
        var ok = await _f.Client.PostAsync("/api/validate", Manifest("golden.json"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(ParseJson(await ok.Content.ReadAsStringAsync()).GetProperty("valid").GetBoolean());

        var malformed = await _f.Client.PostAsync("/api/validate",
            new StringContent("{ not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        // Well-formed JSON that is not a valid scenario (unknown members are disallowed).
        var wrong = await _f.Client.PostAsync("/api/validate",
            new StringContent("{\"bogus_key\": 1}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [Fact]
    public async Task RunEndpointProducesServableFile()
    {
        var res = await _f.Client.PostAsync("/api/run?seed=777", Manifest("golden.json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = ParseJson(await res.Content.ReadAsStringAsync());

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(777ul, body.GetProperty("seed").GetUInt64());
        Assert.Equal(3, body.GetProperty("entities").GetInt32());
        Assert.True(body.GetProperty("events").GetArrayLength() >= 1);

        // The returned /files/ URL round-trips to a parseable .tspi.
        string fileUrl = body.GetProperty("file").GetString()!;
        Assert.StartsWith("/files/", fileUrl);
        var bytes = await _f.Client.GetByteArrayAsync(fileUrl);
        Assert.Equal("TSPI", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("TSPIFTR1", Encoding.ASCII.GetString(bytes, bytes.Length - 8, 8));

        // Deep-link helper points the served viewer at that file.
        Assert.StartsWith("/?file=", body.GetProperty("viewer").GetString());
    }

    [Fact]
    public async Task RunEndpointRejectsInvalidScenarioWith422()
    {
        // Structurally a scenario, semantically broken: references a missing model.
        string json = File.ReadAllText(Path.Combine(_f.RepoRoot, "schemas", "examples", "golden.json"))
            .Replace("\"generic-fighter\"", "\"no-such-model\"");
        var res = await _f.Client.PostAsync("/api/run",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        var body = ParseJson(await res.Content.ReadAsStringAsync());
        Assert.False(body.GetProperty("valid").GetBoolean());
        Assert.True(body.GetProperty("errors").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task VersionAndUnknownRoutes()
    {
        var version = ParseJson(await _f.Client.GetStringAsync("/api/version"));
        Assert.False(string.IsNullOrEmpty(version.GetProperty("version").GetString()));

        Assert.Equal(HttpStatusCode.NotFound, (await _f.Client.GetAsync("/api/nope")).StatusCode);
        // Static route is a whitelist, not a directory listing of web/viewer.
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Client.GetAsync("/tests/ref_dump.py")).StatusCode);
    }
}
