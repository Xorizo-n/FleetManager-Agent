using System.Security.AccessControl;
using System.Security.Principal;

namespace FleetManager.Agent.Core;

/// <summary>
/// Права папки данных (%ProgramData%\FleetManagerAgent). В agent.json лежит токен агента, а служба
/// от SYSTEM читает, пишет и удаляет файлы в этой папке. ProgramData даёт «Пользователям» чтение и
/// создание файлов, и папка, наследовавшая его права, отдавала токен любому студенту. Поэтому
/// наследование отключено: полный доступ только у SYSTEM и Администраторов, владелец — Администраторы.
/// Исключение — wallpapers: назначенные обои открывает Проводник в сеансе пользователя, там
/// «Пользователи» читают. Трей файлов отсюда не читает — состояние он получает через Named Pipe.
///
/// Те же права выставляет установщик (icacls в FleetManagerAgent.iss); служба проверяет их при
/// каждом старте, потому что агенты, поставленные раньше, получили права ProgramData. Ссылки
/// (junction, symlink) внутри папки удаляются: по ним служба писала бы и удаляла файлы от SYSTEM
/// там, куда указал тот, кто их подложил.
/// </summary>
public static class DataFolderSecurity
{
    public const string WallpapersFolder = "wallpapers";

    private const FileSystemRights ReadRights = FileSystemRights.ReadAndExecute;
    private const InheritanceFlags ToChildren = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <param name="Owner">Владелец, которого получают папка и всё, что в ней лежит у чужого владельца.</param>
    /// <param name="FullControl">У кого полный доступ; они же допустимые владельцы.</param>
    /// <param name="WallpaperReaders">Кто читает wallpapers.</param>
    internal sealed record Principals(SecurityIdentifier Owner, IReadOnlyList<SecurityIdentifier> FullControl, SecurityIdentifier WallpaperReaders);

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    internal static readonly Principals Machine = new(
        Administrators,
        new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), Administrators },
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));

    /// <summary>Приводит права папки данных к нужным. Возвращает, что пришлось исправить; пусто — всё было в порядке.</summary>
    public static IReadOnlyList<string> Repair(string dataDirectory) => Repair(dataDirectory, Machine);

    /// <summary>Создаёт папку обоев, которую читают пользователи (если её нет).</summary>
    public static void EnsureWallpapersFolder(string path) => EnsureWallpapersFolder(new DirectoryInfo(path), Machine);

    internal static IReadOnlyList<string> Repair(string dataDirectory, Principals principals)
    {
        var fixes = new List<string>();
        var root = new DirectoryInfo(dataDirectory);
        if (root.Exists && root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // Такую папку мог создать кто угодно ещё до установки: всё, что за ссылкой, чужое.
            var target = root.LinkTarget;
            root.Delete();
            fixes.Add($"{root.FullName} была ссылкой на {target} — ссылка удалена");
            root.Refresh();
        }
        root.Create();

        var rootSecurity = root.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!IsRootInPlace(rootSecurity, principals))
        {
            var security = new DirectorySecurity();
            security.SetOwner(principals.Owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in principals.FullControl)
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Allow));
            // Новые наследуемые права Windows сама разносит по вложенным файлам с наследованием.
            root.SetAccessControl(security);
            fixes.Add($"{root.FullName}: доступ только SYSTEM и Администраторам (было наследование от ProgramData)");
        }

        RepairChildren(root, principals, atRoot: true, wallpapers: false, fixes);
        EnsureWallpapersFolder(new DirectoryInfo(Path.Combine(root.FullName, WallpapersFolder)), principals);
        return fixes;
    }

    private static void RepairChildren(DirectoryInfo directory, Principals principals, bool atRoot, bool wallpapers, List<string> fixes)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false }))
        {
            try
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    var target = entry.LinkTarget;
                    // Удаляется сама ссылка, а не то, на что она указывает.
                    entry.Delete();
                    fixes.Add($"{entry.FullName}: удалена ссылка на {target}");
                    continue;
                }

                var isWallpapersFolder = atRoot && entry is DirectoryInfo &&
                    string.Equals(entry.Name, WallpapersFolder, StringComparison.OrdinalIgnoreCase);
                var readable = wallpapers || isWallpapersFolder;
                var expected = isWallpapersFolder ? new[] { ReadRule(principals) } : Array.Empty<FileSystemAccessRule>();

                FileSystemSecurity current = entry is DirectoryInfo dir
                    ? dir.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                    : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
                if (!IsChildInPlace(current, expected, readable, principals))
                {
                    if (entry is DirectoryInfo d) d.SetAccessControl((DirectorySecurity)Inheriting(new DirectorySecurity(), expected, principals.Owner));
                    else ((FileInfo)entry).SetAccessControl((FileSecurity)Inheriting(new FileSecurity(), expected, principals.Owner));
                    fixes.Add($"{entry.FullName}: права сброшены на наследуемые от папки агента");
                }

                if (entry is DirectoryInfo sub) RepairChildren(sub, principals, atRoot: false, readable, fixes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                fixes.Add($"{entry.FullName}: не удалось исправить права — {ex.Message}");
            }
        }
    }

    private static void EnsureWallpapersFolder(DirectoryInfo folder, Principals principals)
    {
        if (folder.Exists) return;
        folder.Create();
        folder.SetAccessControl((DirectorySecurity)Inheriting(new DirectorySecurity(), new[] { ReadRule(principals) }, owner: null));
    }

    internal static FileSystemAccessRule ReadRule(Principals principals) =>
        new(principals.WallpaperReaders, ReadRights, ToChildren, PropagationFlags.None, AccessControlType.Allow);

    // Только наследуемые права от папки агента плюс expected; явные права и чужой владелец уходят.
    private static FileSystemSecurity Inheriting(FileSystemSecurity security, IEnumerable<FileSystemAccessRule> expected, SecurityIdentifier? owner)
    {
        if (owner is not null) security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        foreach (var rule in expected) security.AddAccessRule(rule);
        return security;
    }

    internal static bool IsRootInPlace(FileSystemSecurity security, Principals principals)
    {
        if (!security.AreAccessRulesProtected || !IsAllowedOwner(security, principals)) return false;
        var expected = principals.FullControl
            .Select(sid => new FileSystemAccessRule(sid, FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Allow))
            .ToArray();
        return SameRules(Rules(security).ToList(), expected);
    }

    internal static bool IsChildInPlace(FileSystemSecurity security, IReadOnlyList<FileSystemAccessRule> expected, bool readable, Principals principals)
    {
        if (security.AreAccessRulesProtected || !IsAllowedOwner(security, principals)) return false;
        var rules = Rules(security).ToList();
        if (!SameRules(rules.Where(r => !r.IsInherited).ToList(), expected)) return false;
        // Унаследованное тоже проверяется: перенесённый сюда файл несёт права старой папки, помеченные как унаследованные.
        return rules.Where(r => r.IsInherited).All(r =>
            r.AccessControlType == AccessControlType.Allow &&
            (principals.FullControl.Contains(r.IdentityReference) ||
             readable && r.IdentityReference.Equals(principals.WallpaperReaders) && (Rights(r) & ~Rights(ReadRights)) == 0));
    }

    private static bool IsAllowedOwner(FileSystemSecurity security, Principals principals) =>
        security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && principals.FullControl.Contains(owner);

    private static IEnumerable<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();

    private static bool SameRules(IReadOnlyList<FileSystemAccessRule> actual, IReadOnlyList<FileSystemAccessRule> expected) =>
        actual.Count == expected.Count && expected.All(e => actual.Any(a =>
            a.IdentityReference.Equals(e.IdentityReference) &&
            a.AccessControlType == e.AccessControlType &&
            a.InheritanceFlags == e.InheritanceFlags &&
            a.PropagationFlags == e.PropagationFlags &&
            Rights(a) == Rights(e.FileSystemRights)));

    // Windows добавляет Synchronize к разрешающим правам сама — при сравнении его не учитываем.
    private static FileSystemRights Rights(FileSystemAccessRule rule) => Rights(rule.FileSystemRights);
    private static FileSystemRights Rights(FileSystemRights rights) => rights & ~FileSystemRights.Synchronize;
}
