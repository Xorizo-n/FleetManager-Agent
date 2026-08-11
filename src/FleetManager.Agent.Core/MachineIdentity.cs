namespace FleetManager.Agent.Core;

public static class MachineIdentity
{
    public static string GetOrCreate(string? dataDirectory = null)
    {
        var file = AgentConfiguration.MachineIdentityFile(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        if (File.Exists(file))
        {
            var existing = File.ReadAllText(file).Trim();
            if (Guid.TryParse(existing, out _))
            {
                return existing;
            }
        }

        var id = Guid.NewGuid().ToString("D");
        File.WriteAllText(file, id + Environment.NewLine);
        return id;
    }
}
