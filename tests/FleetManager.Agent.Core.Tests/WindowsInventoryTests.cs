using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public sealed class WindowsInventoryTests
{
    [Fact]
    public async Task PowerShellOutput_keepsCyrillic()
    {
        // Регрессия: PowerShell 5.1 писал в перенаправленный stdout в OEM-кодировке, и на сервер
        // уходило «Windows 11 Pro ��� ��ࠧ���⥫��� ��०» и «1�:�।���⨥ 8».
        if (!OperatingSystem.IsWindows()) return;

        var output = await WindowsInventoryCollector.RunPowerShellAsync(
            "[pscustomobject]@{ Name = '1С:Предприятие 8 «тест» ё' } | ConvertTo-Json -Compress", CancellationToken.None);

        Assert.Equal("{\"Name\":\"1С:Предприятие 8 «тест» ё\"}", output.Trim());
    }
}
