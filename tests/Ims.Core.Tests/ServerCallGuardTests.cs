using FluentAssertions;
using Ims.Core.Diagnostics;
using Xunit;

namespace Ims.Core.Tests;

/// <summary>
/// <see cref="ServerCallGuard"/> holds process-wide state, so these run alone.
/// </summary>
[Collection(nameof(ServerCallGuardTests))]
[CollectionDefinition(nameof(ServerCallGuardTests), DisableParallelization = true)]
public class ServerCallGuardTests : IDisposable
{
    public void Dispose()
    {
        ServerCallGuard.ResetForTesting();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Does_nothing_when_no_detector_is_installed()
    {
        // Tests and the smoke-test console have no dispatcher; the guard must be inert.
        ServerCallGuard.ResetForTesting();

        ServerCallGuard.IsOnUiThread.Should().BeFalse();

        Action act = () => ServerCallGuard.AssertNotOnUiThread("connect");

        act.Should().NotThrow();
    }

    [Fact]
    public void Allows_a_server_call_off_the_UI_thread()
    {
        ServerCallGuard.ConfigureUiThreadDetector(() => false);

        Action act = () => ServerCallGuard.AssertNotOnUiThread("execute");

        act.Should().NotThrow();
    }

    [Fact]
    public void Throws_when_a_server_call_would_block_the_UI()
    {
        // NFR-1 calls this a functional requirement, so it fails loudly rather than
        // degrading quietly into the slowness RSK-3 is about.
        ServerCallGuard.ConfigureUiThreadDetector(() => true);

        Action act = () => ServerCallGuard.AssertNotOnUiThread("execute");

        act.Should().Throw<UiThreadBlockedException>()
            .Which.Operation.Should().Be("execute");
    }

    [Fact]
    public void Rejects_a_null_detector()
    {
        Action act = () => ServerCallGuard.ConfigureUiThreadDetector(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
