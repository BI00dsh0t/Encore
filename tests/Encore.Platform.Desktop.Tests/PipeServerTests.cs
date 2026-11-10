using System.ComponentModel;
using System.IO;
using System.IO.Pipes;

namespace Encore.Platform.Desktop.Tests;

public sealed class PipeServerTests
{
    [Fact]
    public async Task TheServerOfAPipeServedInThisProcessIsThisProcess()
    {
        var name = "Encore-Test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await client.ConnectAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await accepted;

        Assert.Equal(Environment.ProcessId, PipeServer.ProcessId(client.SafePipeHandle));
    }

    [Fact]
    public void AHandleThatIsNoPipeThrows()
    {
        using var file = File.OpenHandle(typeof(PipeServerTests).Assembly.Location);
        using var notAPipe = new Microsoft.Win32.SafeHandles.SafePipeHandle(file.DangerousGetHandle(), ownsHandle: false);

        Assert.Throws<Win32Exception>(() => PipeServer.ProcessId(notAPipe));
    }
}
