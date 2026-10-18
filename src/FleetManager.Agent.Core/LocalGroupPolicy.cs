using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace FleetManager.Agent.Core;

/// <summary>Одна запись Registry.pol: раздел, имя значения, тип (REG_SZ = 1, REG_DWORD = 4) и данные как есть.</summary>
public sealed record PolicyEntry(string Key, string ValueName, uint Type, byte[] Data)
{
    public const uint RegSz = 1;
    public const uint RegDword = 4;

    public static PolicyEntry String(string key, string valueName, string value) =>
        new(key, valueName, RegSz, Encoding.Unicode.GetBytes(value + "\0"));

    public string? StringValue => Type is RegSz or 2 ? Encoding.Unicode.GetString(Data).TrimEnd('\0') : null;

    public bool Matches(string key, string valueName) =>
        string.Equals(Key, key, StringComparison.OrdinalIgnoreCase) && string.Equals(ValueName, valueName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Файл Registry.pol локальной групповой политики: «PReg», версия 1, затем записи
/// [раздел;значение;тип;размер;данные] в UTF-16LE. Обои и экран блокировки на ПК из образа
/// заданы именно здесь (SU5/SU7 — экран блокировки, MR32 — ещё и обои для всех пользователей),
/// и при обновлении политики Windows восстанавливает значения из этого файла. Поэтому агент
/// меняет записи в нём, а не только в реестре. Чужие записи сохраняются байт в байт.
/// </summary>
public static class PolicyFile
{
    private static readonly byte[] Header = { (byte)'P', (byte)'R', (byte)'e', (byte)'g', 1, 0, 0, 0 };

    /// <summary>Записи файла; пустой список, если файла нет. Испорченный файл — InvalidDataException: такой файл агент не трогает.</summary>
    public static List<PolicyEntry> Parse(byte[] data)
    {
        if (data.Length == 0) return new List<PolicyEntry>();
        if (data.Length < Header.Length || !data.AsSpan(0, Header.Length).SequenceEqual(Header))
            throw new InvalidDataException("Registry.pol: неизвестный заголовок.");
        var entries = new List<PolicyEntry>();
        var pos = Header.Length;
        while (pos < data.Length)
        {
            Expect(data, ref pos, '[');
            var key = ReadString(data, ref pos);
            Expect(data, ref pos, ';');
            var valueName = ReadString(data, ref pos);
            Expect(data, ref pos, ';');
            var type = ReadUInt32(data, ref pos);
            Expect(data, ref pos, ';');
            var size = (int)ReadUInt32(data, ref pos);
            Expect(data, ref pos, ';');
            if (size < 0 || pos + size > data.Length) throw new InvalidDataException("Registry.pol: данные за концом файла.");
            var value = data.AsSpan(pos, size).ToArray();
            pos += size;
            Expect(data, ref pos, ']');
            entries.Add(new PolicyEntry(key, valueName, type, value));
        }
        return entries;
    }

    public static byte[] Write(IEnumerable<PolicyEntry> entries)
    {
        using var stream = new MemoryStream();
        stream.Write(Header);
        foreach (var e in entries)
        {
            WriteChar(stream, '[');
            stream.Write(Encoding.Unicode.GetBytes(e.Key + "\0"));
            WriteChar(stream, ';');
            stream.Write(Encoding.Unicode.GetBytes(e.ValueName + "\0"));
            WriteChar(stream, ';');
            WriteUInt32(stream, e.Type);
            WriteChar(stream, ';');
            WriteUInt32(stream, (uint)e.Data.Length);
            WriteChar(stream, ';');
            stream.Write(e.Data);
            WriteChar(stream, ']');
        }
        return stream.ToArray();
    }

    /// <summary>Ставит значение (null — удаляет запись), сохраняя порядок остальных.</summary>
    public static List<PolicyEntry> Set(IEnumerable<PolicyEntry> entries, string key, string valueName, PolicyEntry? value)
    {
        var result = entries.ToList();
        var index = result.FindIndex(e => e.Matches(key, valueName));
        if (value is null)
        {
            if (index >= 0) result.RemoveAt(index);
        }
        else if (index >= 0) result[index] = value;
        else result.Add(value);
        return result;
    }

    private static void Expect(byte[] data, ref int pos, char c)
    {
        if (pos + 2 > data.Length || data[pos] != (byte)c || data[pos + 1] != 0)
            throw new InvalidDataException($"Registry.pol: ожидался «{c}» в позиции {pos}.");
        pos += 2;
    }

    private static string ReadString(byte[] data, ref int pos)
    {
        var start = pos;
        while (pos + 1 < data.Length && (data[pos] != 0 || data[pos + 1] != 0)) pos += 2;
        if (pos + 1 >= data.Length) throw new InvalidDataException("Registry.pol: строка без завершающего нуля.");
        var text = Encoding.Unicode.GetString(data, start, pos - start);
        pos += 2;
        return text;
    }

    private static uint ReadUInt32(byte[] data, ref int pos)
    {
        if (pos + 4 > data.Length) throw new InvalidDataException("Registry.pol: обрезанное число.");
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;
        return value;
    }

    private static void WriteChar(Stream stream, char c) => stream.Write(new[] { (byte)c, (byte)0 });

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}

/// <summary>
/// gpt.ini локальной политики. Version — (версия пользовательской части &lt;&lt; 16) | версия
/// компьютерной: без её увеличения Windows не перечитывает Registry.pol. Список расширений
/// должен содержать расширение реестра, иначе часть политики не применяется вовсе — на SU5
/// пользовательской части в образе нет, и обои для пользователей без этого не заработали бы.
/// </summary>
public static class GptIni
{
    // Расширение реестра и оснастка «Административные шаблоны» для компьютера и для пользователя
    internal const string MachineExtensions = "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{D02B1F72-3407-48AE-BA88-E8213C6761F1}]";
    internal const string UserExtensions = "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{D02B1F73-3407-48AE-BA88-E8213C6761F1}]";
    private const string RegistryExtension = "{35378EAC-683F-11D2-A89A-00C04FBBCFA2}";

    public static string Bump(string? content, bool machine, bool user)
    {
        var text = string.IsNullOrWhiteSpace(content) ? "[General]\r\n" : content.Replace("\r\n", "\n").Replace("\n", "\r\n");
        if (!Regex.IsMatch(text, @"^\[General\]", RegexOptions.Multiline | RegexOptions.IgnoreCase)) text = "[General]\r\n" + text;
        // Только до конца строки: \s* захватывал и перевод строки, и следующая строка приклеивалась к Version
        var versionMatch = Regex.Match(text, @"^Version=(\d+)[^\r\n]*", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var version = versionMatch.Success && uint.TryParse(versionMatch.Groups[1].Value, out var v) ? v : 0u;
        var machinePart = (version & 0xFFFF) + (machine ? 1u : 0u);
        var userPart = (version >> 16) + (user ? 1u : 0u);
        var next = ((userPart & 0xFFFF) << 16) | (machinePart & 0xFFFF);
        text = versionMatch.Success
            ? text.Remove(versionMatch.Index, versionMatch.Length).Insert(versionMatch.Index, $"Version={next}")
            : InsertAfterGeneral(text, $"Version={next}");
        if (machine) text = EnsureExtensions(text, "gPCMachineExtensionNames", MachineExtensions);
        if (user) text = EnsureExtensions(text, "gPCUserExtensionNames", UserExtensions);
        return text.EndsWith("\r\n") ? text : text + "\r\n";
    }

    private static string EnsureExtensions(string text, string name, string extensions)
    {
        var match = Regex.Match(text, $@"^{name}=([^\r\n]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!match.Success) return InsertAfterGeneral(text, $"{name}={extensions}");
        if (match.Groups[1].Value.Contains(RegistryExtension, StringComparison.OrdinalIgnoreCase)) return text;
        // Пары [CSE{оснастка}] должны идти по возрастанию GUID расширения; реестр — {35…}, обычно первый
        var line = $"{name}={extensions}{match.Groups[1].Value.Trim()}";
        return text.Remove(match.Index, match.Length).Insert(match.Index, line);
    }

    private static string InsertAfterGeneral(string text, string line)
    {
        var general = Regex.Match(text, @"^\[General\]\r\n", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return text.Insert(general.Index + general.Length, line + "\r\n");
    }
}
