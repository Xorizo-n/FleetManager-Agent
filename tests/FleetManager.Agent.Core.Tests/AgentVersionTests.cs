using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public sealed class AgentVersionTests
{
    [Fact]
    public void Normalize_dropsBuildMetadata()
    {
        Assert.Equal("2025.08.14.12", AgentVersion.Normalize("2025.08.14.12+9f3c1a2"));
    }

    [Fact]
    public void Normalize_trimsWhitespace()
    {
        Assert.Equal("1.2.3", AgentVersion.Normalize("  1.2.3  "));
    }

    [Fact]
    public void Normalize_treatsBlankValuesAsUnknown()
    {
        Assert.Null(AgentVersion.Normalize(null));
        Assert.Null(AgentVersion.Normalize("   "));
        Assert.Null(AgentVersion.Normalize("+only-metadata"));
    }

    [Fact]
    public void Current_isAlwaysReportable()
    {
        // Сервер использует значение как строку версии — оно не должно быть пустым
        // даже в dev-сборке без -p:Version (тогда это версия сборки по умолчанию).
        Assert.False(string.IsNullOrWhiteSpace(AgentVersion.Current));
    }
}
