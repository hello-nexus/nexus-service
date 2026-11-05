using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Nexus.Service.Platform;
using Xunit;

namespace Nexus.Service.Tests.Platform;

public class ServiceLogLoggerProviderTests
{
    [Fact]
    public void ReplaceConsoleProvider_LeavesNoConsoleProvider_AndAddsOurs()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        Assert.Contains(builder.Services, d => d.ImplementationType == typeof(ConsoleLoggerProvider));

        ServiceLogLoggerProvider.ReplaceConsoleProvider(builder.Logging);

        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(ILoggerProvider) && d.ImplementationType == typeof(ConsoleLoggerProvider));
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(ILoggerProvider) && d.ImplementationInstance is ServiceLogLoggerProvider);
    }

    [Fact]
    public void ReplaceConsoleProvider_KeepsOtherProviders()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Services.AddSingleton<ILoggerProvider, KeptProvider>();

        ServiceLogLoggerProvider.ReplaceConsoleProvider(builder.Logging);

        Assert.Contains(builder.Services, d => d.ImplementationType == typeof(KeptProvider));
    }

    [Fact]
    public void SlimBuilder_RegisteredProviders_AreOnlyTheConsoleProvider()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        var types = builder.Services.Where(d => d.ServiceType == typeof(ILoggerProvider)).Select(d => d.ImplementationType).ToList();
        Assert.Equal(new Type?[] { typeof(ConsoleLoggerProvider) }, types);
    }

    private sealed class KeptProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
