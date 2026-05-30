using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

public static class MateEnginePaths
{
    public static string DataRoot => EnsureDirectory(Application.persistentDataPath);
    public static string CacheRoot => EnsureDirectory(Application.temporaryCachePath);
    public static string AvatarsJsonPath => Path.Combine(DataRoot, "avatars.json");
    public static string ThumbnailsDir => EnsureDirectory(Path.Combine(DataRoot, "Thumbnails"));
    public static string ImportedModelsDir => EnsureDirectory(Path.Combine(DataRoot, "ImportedModels"));
    public static string ModsDir => EnsureDirectory(Path.Combine(DataRoot, "Mods"));
    public static string WorkshopDir => EnsureDirectory(Path.Combine(DataRoot, "Steam Workshop"));
    public static string BlendshapesDir => EnsureDirectory(Path.Combine(DataRoot, "Blendshapes"));
    public static string MEValueChangerDir => EnsureDirectory(Path.Combine(DataRoot, "MEValueChanger"));
    public static string SyncDir => EnsureDirectory(Path.Combine(DataRoot, "Sync"));
    public static string ModCacheDir => EnsureDirectory(Path.Combine(CacheRoot, "ME_Cache"));

    public static string DefaultImportDirectory
    {
        get
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents) && Directory.Exists(documents))
                return documents;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
                return home;

            return DataRoot;
        }
    }

    public static string SettingsDirectory(string customDataDir)
    {
        if (string.IsNullOrWhiteSpace(customDataDir))
            return DataRoot;

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        return SafePersistentSubdirectory(customDataDir);
#else
        return Path.IsPathRooted(customDataDir)
            ? EnsureDirectory(customDataDir)
            : EnsureDirectory(Path.Combine(DataRoot, customDataDir));
#endif
    }

    public static string SettingsFilePath(string customDataDir, string fileName)
    {
        return Path.Combine(SettingsDirectory(customDataDir), SanitizeFileName(fileName, "settings.json"));
    }

    public static string DataFilePath(string fileName)
    {
        return Path.Combine(DataRoot, SanitizeFileName(fileName, "data.json"));
    }

    public static string ImportModelToManagedStorage(string sourcePath)
    {
        return CopyExternalFileToManagedStorage(sourcePath, ImportedModelsDir);
    }

    public static string CopyExternalFileToManagedStorage(string sourcePath, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return sourcePath;

        try
        {
            string fullSource = Path.GetFullPath(sourcePath);
            if (IsUnderDirectory(fullSource, DataRoot))
                return fullSource;

            string targetDir = EnsureDirectory(targetDirectory);
            string baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(fullSource), "model");
            string extension = Path.GetExtension(fullSource);
            string hash = ShortFileHash(fullSource);
            string destination = Path.Combine(targetDir, $"{baseName}_{hash}{extension}");

            if (!File.Exists(destination) || new FileInfo(destination).Length != new FileInfo(fullSource).Length)
                File.Copy(fullSource, destination, true);

            return destination;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[MateEnginePaths] Could not copy imported file into app storage: " + e.Message);
            return sourcePath;
        }
    }

    public static string SafePersistentSubdirectory(string relativePath)
    {
        string current = DataRoot;
        foreach (var segment in SafeSegments(relativePath))
            current = Path.Combine(current, segment);

        return EnsureDirectory(current);
    }

    public static string EnsureDirectory(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
            Directory.CreateDirectory(path);

        return path;
    }

    public static string SanitizeFileName(string value, string fallback)
    {
        string fileName = Path.GetFileName((value ?? "").Replace('\\', '/'));
        foreach (char c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');

        return string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
    }

    public static bool IsUnderDirectory(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            return false;

        string fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] SafeSegments(string relativePath)
    {
        string normalized = (relativePath ?? "").Replace('\\', '/');
        string[] rawSegments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        var safe = new System.Collections.Generic.List<string>(rawSegments.Length);

        foreach (string raw in rawSegments)
        {
            if (raw == "." || raw == ".." || raw == "~")
                continue;

            string segment = SanitizeFileName(raw, "");
            if (!string.IsNullOrWhiteSpace(segment))
                safe.Add(segment);
        }

        return safe.ToArray();
    }

    private static string ShortFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha1 = SHA1.Create();
        byte[] hash = sha1.ComputeHash(stream);
        var sb = new StringBuilder(16);

        for (int i = 0; i < Math.Min(8, hash.Length); i++)
            sb.Append(hash[i].ToString("x2"));

        return sb.ToString();
    }
}
