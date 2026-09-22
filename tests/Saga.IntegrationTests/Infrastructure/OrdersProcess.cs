using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Saga.IntegrationTests.Infrastructure;

// An Orders node in its own OS process, running the real Orders.dll, so a test can kill
// it outright. An in-process host can only be stopped politely: StopAsync releases its
// envelopes, and skipping StopAsync leaves its threads running. Neither is a crash.
public sealed class OrdersProcess : IAsyncDisposable
{
    private readonly Process _process;

    private OrdersProcess(Process process, Uri baseAddress)
    {
        _process = process;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public static async Task<OrdersProcess> Start(IReadOnlyDictionary<string, string> environment)
    {
        var port = FreePort();
        // The test assembly and Orders are built together: same configuration, same TFM.
        var output = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var dll = Path.Combine(RepoRoot(), "src", "Orders", "bin", output.Parent!.Name, output.Name, "Orders.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("build src/Orders first", dll);

        var start = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        foreach (var (key, value) in environment) start.Environment[key] = value;

        var process = Process.Start(start)!;
        // Drain the pipes so a chatty child never blocks on a full buffer.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var node = new OrdersProcess(process, new Uri($"http://127.0.0.1:{port}"));
        try
        {
            await node.WaitUntilHealthy();
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
        return node;
    }

    // No shutdown hooks, no StopAsync, no chance to release anything: the OS takes the
    // process away, as a crash, an OOM kill or a pulled plug would.
    public void Kill()
    {
        _process.Kill(entireProcessTree: true);
        _process.WaitForExit();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited) Kill();
        _process.Dispose();
        await Task.CompletedTask;
    }

    private async Task WaitUntilHealthy()
    {
        using var http = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(2) };
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < until)
        {
            if (_process.HasExited) throw new InvalidOperationException($"Orders process exited with code {_process.ExitCode}");
            try
            {
                if ((await http.GetAsync("/health")).IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException("Orders process never became healthy");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SagaInProduction.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root (SagaInProduction.slnx) not found");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
