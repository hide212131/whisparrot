using System.Diagnostics;

namespace Whisparrot.Infrastructure.Recognition;

internal sealed record WorkerStartInfo(string Backend, string PipeName, string Nonce, string ModelPath);

/// <summary>起動した認識子の最小ハンドル。テストでは fake に差し替える。</summary>
internal interface IWorkerProcess : IDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    void Kill();
    Task WaitForExitAsync(CancellationToken ct);
}

internal interface IWorkerLauncher
{
    IWorkerProcess Launch(WorkerStartInfo info);
}

/// <summary>同じ exe を <c>--recognizer-worker</c> で起動し、Job Object に入れる。</summary>
internal sealed class ProcessWorkerLauncher : IWorkerLauncher
{
    public IWorkerProcess Launch(WorkerStartInfo info)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath なし"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(WorkerHost.Flag);
        psi.ArgumentList.Add("--pipe"); psi.ArgumentList.Add(info.PipeName);
        psi.ArgumentList.Add("--nonce"); psi.ArgumentList.Add(info.Nonce);
        psi.ArgumentList.Add("--backend"); psi.ArgumentList.Add(info.Backend);
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(info.ModelPath);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("認識子を起動できません");
        JobObject.Assign(p);
        return new RealWorker(p);
    }

    private sealed class RealWorker(Process p) : IWorkerProcess
    {
        public bool HasExited => p.HasExited;
        public int? ExitCode => p.HasExited ? p.ExitCode : null;

        public void Kill()
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }

        public Task WaitForExitAsync(CancellationToken ct) => p.WaitForExitAsync(ct);
        public void Dispose() => p.Dispose();
    }
}
