using System;
using ClashXW.Services;
using Xunit;

namespace ClashXW.Tests.Services;

public sealed class TrayControllerTests
{
    [Fact]
    public void CreateDeferredHandler_QueuesActionInsteadOfRunningInline()
    {
        var ran = false;
        Action? queued = null;

        var handler = TrayController.CreateDeferredHandler(
            () => ran = true,
            action => queued = action);

        handler(null, EventArgs.Empty);

        Assert.False(ran);
        Assert.NotNull(queued);

        queued!();

        Assert.True(ran);
    }
}
