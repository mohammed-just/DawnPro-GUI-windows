using Microsoft.Testing.Platform.Builder;
using Moondrop.PhysicalWatchdog;

namespace Moondrop.PhysicalTests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
        SelfRegisteredExtensions.AddSelfRegisteredExtensions(builder, args);
        using ITestApplication app = await builder.BuildAsync().ConfigureAwait(false);
        return await app.RunAsync().ConfigureAwait(false);
    }
}
