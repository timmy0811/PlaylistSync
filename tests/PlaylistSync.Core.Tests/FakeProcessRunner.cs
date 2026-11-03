using PlaylistSync.Core.Tools;

namespace PlaylistSync.Core.Tests;

internal sealed class FakeProcessRunner(Func<ProcessRequest, ProcessResult> respond) : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = [];

    public static ProcessResult Ok(string stdout) => new(0, stdout, "", TimeSpan.Zero);
    public static ProcessResult Fail(string stderr, int exitCode = 1) => new(exitCode, "null\n", stderr, TimeSpan.Zero);

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
