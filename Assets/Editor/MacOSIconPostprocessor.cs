#if UNITY_EDITOR_OSX
using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class MacOSIconPostprocessor
{
    const string IconName = "macOS_icon_composer";
    const string IconComposerAssetPath = "Assets/MATE ENGINE - Icons/macOS_icon_composer.icon";
    const string LegacyDefaultPngAssetPath = "Assets/MATE ENGINE - Icons/macOS_icon_composer-macOS-Default-1024x1024@1x.png";

    [PostProcessBuild(1000)]
    public static void OnPostprocessBuild(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.StandaloneOSX)
            return;

        if (TryApplyIconComposerToXcodeProject(pathToBuiltProject))
            return;

        TryApplyLegacyIconToAppBundle(pathToBuiltProject);
    }

    static bool TryApplyIconComposerToXcodeProject(string pathToBuiltProject)
    {
        if (!TryFindXcodeProject(pathToBuiltProject, out string projectRoot, out string pbxProjectPath))
            return false;

        string sourceIconPath = Path.GetFullPath(IconComposerAssetPath);
        if (!Directory.Exists(sourceIconPath))
        {
            Debug.LogWarning("[MacOSIconPostprocessor] Icon Composer package not found: " + sourceIconPath);
            return false;
        }

        string projectIconPath = Path.Combine(projectRoot, IconName + ".icon");
        if (Directory.Exists(projectIconPath))
            Directory.Delete(projectIconPath, true);
        CopyDirectory(sourceIconPath, projectIconPath);
        ClearExtendedAttributes(projectIconPath);

        var project = new PBXProject();
        project.ReadFromString(File.ReadAllText(pbxProjectPath));

        string mainTargetGuid = project.GetUnityMainTargetGuid();
        string iconRelativePath = IconName + ".icon";
        string existingIconGuid = project.FindFileGuidByProjectPath(iconRelativePath);
        string iconGuid = string.IsNullOrEmpty(existingIconGuid)
            ? project.AddFile(iconRelativePath, iconRelativePath)
            : existingIconGuid;

        RemoveProjectFileIfExists(project, "Images.xcassets");
        RemoveProjectFileIfExists(project, "Unity-iPhone/Images.xcassets");
        RemoveProjectFileIfExists(project, "MateEngineX/PlugIns/libusearch_c.so");
        project.AddFileToBuild(mainTargetGuid, iconGuid);
        project.SetBuildProperty(mainTargetGuid, "ASSETCATALOG_COMPILER_APPICON_NAME", IconName);
        ConfigureLocalMacSigning(project, mainTargetGuid);
        File.WriteAllText(pbxProjectPath, NormalizeIconComposerFileType(project.WriteToString()));

        if (TryFindInfoPlist(projectRoot, out string plistPath))
        {
            var plist = new PlistDocument();
            plist.ReadFromString(File.ReadAllText(plistPath));
            plist.root.SetString("CFBundleIconFile", IconName);
            File.WriteAllText(plistPath, plist.WriteToString());
        }
        else
        {
            Debug.LogWarning("[MacOSIconPostprocessor] Could not find macOS Info.plist to set CFBundleIconFile.");
        }

        Debug.Log("[MacOSIconPostprocessor] Added Icon Composer package to macOS Xcode project: " + projectIconPath);
        return true;
    }

    static void ConfigureLocalMacSigning(PBXProject project, string targetGuid)
    {
        project.SetBuildProperty(targetGuid, "CODE_SIGN_STYLE", "Manual");
        project.SetBuildProperty(targetGuid, "CODE_SIGN_IDENTITY", "-");
        project.SetBuildProperty(targetGuid, "CODE_SIGN_IDENTITY[sdk=macosx*]", "-");
        project.SetBuildProperty(targetGuid, "DEVELOPMENT_TEAM", "");
        project.SetBuildProperty(targetGuid, "PROVISIONING_PROFILE_SPECIFIER", "");
        project.SetBuildProperty(targetGuid, "CODE_SIGN_INJECT_BASE_ENTITLEMENTS", "NO");
    }

    static string NormalizeIconComposerFileType(string projectText)
    {
        string fileName = IconName + ".icon";
        string oldEntry = "/* " + fileName + " */ = {isa = PBXFileReference; lastKnownFileType = file; path = " + fileName + "; sourceTree = SOURCE_ROOT; };";
        string newEntry = "/* " + fileName + " */ = {isa = PBXFileReference; lastKnownFileType = folder.iconcomposer.icon; path = " + fileName + "; sourceTree = SOURCE_ROOT; };";
        return projectText.Replace(oldEntry, newEntry);
    }

    static void RemoveProjectFileIfExists(PBXProject project, string projectPath)
    {
        string guid = project.FindFileGuidByProjectPath(projectPath);
        if (string.IsNullOrEmpty(guid))
            guid = project.FindFileGuidByRealPath(projectPath);

        if (!string.IsNullOrEmpty(guid))
            project.RemoveFile(guid);
    }

    static void TryApplyLegacyIconToAppBundle(string pathToBuiltProject)
    {
        string appBundlePath = ResolveAppBundlePath(pathToBuiltProject);
        if (string.IsNullOrEmpty(appBundlePath))
        {
            Debug.LogWarning("[MacOSIconPostprocessor] No Xcode project or .app bundle found. Skipping macOS icon setup.");
            return;
        }

        string sourcePng = Path.GetFullPath(LegacyDefaultPngAssetPath);
        if (!File.Exists(sourcePng))
        {
            Debug.LogWarning("[MacOSIconPostprocessor] Legacy pre-Tahoe icon PNG not found: " + sourcePng);
            return;
        }

        string resourcesDir = Path.Combine(appBundlePath, "Contents", "Resources");
        Directory.CreateDirectory(resourcesDir);

        string bundleIconName = GetAppBundleIconName(appBundlePath);
        string destinationIcns = Path.Combine(resourcesDir, EnsureIcnsExtension(bundleIconName));
        string tempRoot = Path.Combine("Library", "MateEngineMacOSIcon");
        string iconsetPath = Path.Combine(tempRoot, IconName + ".iconset");
        string tempIcns = Path.Combine(tempRoot, IconName + ".icns");

        try
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
            Directory.CreateDirectory(iconsetPath);

            CreateIconsetFromPng(sourcePng, iconsetPath);
            if (!RunTool("/usr/bin/iconutil", "-c icns " + Quote(iconsetPath) + " -o " + Quote(tempIcns), out string iconutilOutput))
            {
                Debug.LogWarning("[MacOSIconPostprocessor] iconutil failed:\n" + iconutilOutput);
                return;
            }

            File.Copy(tempIcns, destinationIcns, true);
            SetAppBundleIconFile(appBundlePath, RemoveIcnsExtension(bundleIconName));
            DeleteExtraLegacyIcon(resourcesDir, destinationIcns);
            ClearExtendedAttributes(appBundlePath);
            TouchAppBundle(appBundlePath);
            ResignAppBundle(appBundlePath);
            RegisterAppBundleWithLaunchServices(appBundlePath);
            Debug.Log("[MacOSIconPostprocessor] Replaced legacy pre-Tahoe macOS app icon: " + destinationIcns);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[MacOSIconPostprocessor] Failed to apply legacy macOS app icon: " + ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
            catch
            {
                // Temporary cleanup failure should not fail the build.
            }
        }
    }

    static bool TryFindXcodeProject(string pathToBuiltProject, out string projectRoot, out string pbxProjectPath)
    {
        projectRoot = string.Empty;
        pbxProjectPath = string.Empty;

        if (string.IsNullOrEmpty(pathToBuiltProject) || !Directory.Exists(pathToBuiltProject))
            return false;

        string directProjectPath = pathToBuiltProject.EndsWith(".xcodeproj", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(pathToBuiltProject, "project.pbxproj")
            : string.Empty;
        if (!string.IsNullOrEmpty(directProjectPath) && File.Exists(directProjectPath))
        {
            pbxProjectPath = directProjectPath;
            projectRoot = Directory.GetParent(pathToBuiltProject).FullName;
            return true;
        }

        string expectedProject = Path.Combine(
            pathToBuiltProject,
            Path.GetFileNameWithoutExtension(pathToBuiltProject) + ".xcodeproj",
            "project.pbxproj");
        if (File.Exists(expectedProject))
        {
            pbxProjectPath = expectedProject;
            projectRoot = pathToBuiltProject;
            return true;
        }

        string[] projects = Directory.GetFiles(pathToBuiltProject, "project.pbxproj", SearchOption.AllDirectories);
        for (int i = 0; i < projects.Length; i++)
        {
            string parent = Path.GetDirectoryName(projects[i]);
            if (parent != null && parent.EndsWith(".xcodeproj", StringComparison.OrdinalIgnoreCase))
            {
                pbxProjectPath = projects[i];
                projectRoot = Directory.GetParent(parent).FullName;
                return true;
            }
        }

        return false;
    }

    static bool TryFindInfoPlist(string projectRoot, out string plistPath)
    {
        string productPlist = Path.Combine(projectRoot, PlayerSettings.productName, "Info.plist");
        if (File.Exists(productPlist))
        {
            plistPath = productPlist;
            return true;
        }

        string rootPlist = Path.Combine(projectRoot, "Info.plist");
        if (File.Exists(rootPlist))
        {
            plistPath = rootPlist;
            return true;
        }

        string[] plistFiles = Directory.GetFiles(projectRoot, "Info.plist", SearchOption.AllDirectories);
        if (plistFiles.Length > 0)
        {
            plistPath = plistFiles[0];
            return true;
        }

        plistPath = string.Empty;
        return false;
    }

    static string ResolveAppBundlePath(string pathToBuiltProject)
    {
        if (!string.IsNullOrEmpty(pathToBuiltProject) &&
            Directory.Exists(pathToBuiltProject) &&
            pathToBuiltProject.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return pathToBuiltProject;
        }

        string buildDir = File.Exists(pathToBuiltProject)
            ? Path.GetDirectoryName(pathToBuiltProject)
            : pathToBuiltProject;

        if (string.IsNullOrEmpty(buildDir) || !Directory.Exists(buildDir))
            return string.Empty;

        string[] apps = Directory.GetDirectories(buildDir, "*.app", SearchOption.TopDirectoryOnly);
        return apps.Length > 0 ? apps[0] : string.Empty;
    }

    static string GetAppBundleIconName(string appBundlePath)
    {
        string plistPath = Path.Combine(appBundlePath, "Contents", "Info.plist");
        if (!File.Exists(plistPath))
            return "PlayerIcon";

        var plist = new PlistDocument();
        plist.ReadFromString(File.ReadAllText(plistPath));
        PlistElement iconElement = plist.root["CFBundleIconFile"];
        if (iconElement == null || string.IsNullOrWhiteSpace(iconElement.AsString()))
            return "PlayerIcon";

        return iconElement.AsString();
    }

    static void SetAppBundleIconFile(string appBundlePath, string iconName)
    {
        string plistPath = Path.Combine(appBundlePath, "Contents", "Info.plist");
        if (!File.Exists(plistPath))
            return;

        var plist = new PlistDocument();
        plist.ReadFromString(File.ReadAllText(plistPath));
        PlistElement current = plist.root["CFBundleIconFile"];
        if (current != null && string.Equals(current.AsString(), iconName, StringComparison.Ordinal))
            return;

        plist.root.SetString("CFBundleIconFile", iconName);
        File.WriteAllText(plistPath, plist.WriteToString());
    }

    static void TouchAppBundle(string appBundlePath)
    {
        DateTime now = DateTime.Now;
        Directory.SetLastWriteTime(appBundlePath, now);
        string contentsPath = Path.Combine(appBundlePath, "Contents");
        if (Directory.Exists(contentsPath))
            Directory.SetLastWriteTime(contentsPath, now);
    }

    static void ResignAppBundle(string appBundlePath)
    {
        if (!File.Exists("/usr/bin/codesign"))
            return;

        string args = "--force --deep --sign - --preserve-metadata=entitlements,requirements,flags,runtime " + Quote(appBundlePath);
        if (!RunTool("/usr/bin/codesign", args, out string output))
        {
            Debug.LogWarning("[MacOSIconPostprocessor] codesign failed after icon replacement:\n" + output);
            return;
        }

        Debug.Log("[MacOSIconPostprocessor] Re-signed macOS app bundle after icon replacement.");
    }

    static void RegisterAppBundleWithLaunchServices(string appBundlePath)
    {
        const string lsRegisterPath = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";
        if (!File.Exists(lsRegisterPath))
            return;

        if (!RunTool(lsRegisterPath, "-f " + Quote(appBundlePath), out string output))
            Debug.LogWarning("[MacOSIconPostprocessor] LaunchServices registration refresh failed:\n" + output);
    }

    static void ClearExtendedAttributes(string path)
    {
        if (!File.Exists("/usr/bin/xattr") || string.IsNullOrEmpty(path))
            return;

        if (!RunTool("/usr/bin/xattr", "-cr " + Quote(path), out string output))
            Debug.LogWarning("[MacOSIconPostprocessor] xattr cleanup failed for " + path + ":\n" + output);
    }

    static void DeleteExtraLegacyIcon(string resourcesDir, string destinationIcns)
    {
        string extraIconPath = Path.Combine(resourcesDir, IconName + ".icns");
        if (!PathsEqual(extraIconPath, destinationIcns) && File.Exists(extraIconPath))
            File.Delete(extraIconPath);
    }

    static string EnsureIcnsExtension(string iconName)
    {
        return iconName.EndsWith(".icns", StringComparison.OrdinalIgnoreCase)
            ? iconName
            : iconName + ".icns";
    }

    static string RemoveIcnsExtension(string iconName)
    {
        return iconName.EndsWith(".icns", StringComparison.OrdinalIgnoreCase)
            ? iconName.Substring(0, iconName.Length - ".icns".Length)
            : iconName;
    }

    static bool PathsEqual(string a, string b)
    {
        return string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    static void CreateIconsetFromPng(string sourcePng, string iconsetPath)
    {
        CreateIcon(sourcePng, iconsetPath, "icon_16x16.png", 16);
        CreateIcon(sourcePng, iconsetPath, "icon_16x16@2x.png", 32);
        CreateIcon(sourcePng, iconsetPath, "icon_32x32.png", 32);
        CreateIcon(sourcePng, iconsetPath, "icon_32x32@2x.png", 64);
        CreateIcon(sourcePng, iconsetPath, "icon_128x128.png", 128);
        CreateIcon(sourcePng, iconsetPath, "icon_128x128@2x.png", 256);
        CreateIcon(sourcePng, iconsetPath, "icon_256x256.png", 256);
        CreateIcon(sourcePng, iconsetPath, "icon_256x256@2x.png", 512);
        CreateIcon(sourcePng, iconsetPath, "icon_512x512.png", 512);
        File.Copy(sourcePng, Path.Combine(iconsetPath, "icon_512x512@2x.png"), true);
    }

    static void CreateIcon(string sourcePng, string iconsetPath, string fileName, int size)
    {
        string outputPath = Path.Combine(iconsetPath, fileName);
        string args = "-z " + size + " " + size + " " + Quote(sourcePng) + " --out " + Quote(outputPath);
        if (!RunTool("/usr/bin/sips", args, out string output))
            throw new InvalidOperationException("sips failed while creating " + fileName + ":\n" + output);
    }

    static void CopyDirectory(string sourceDir, string destinationDir)
    {
        var source = new DirectoryInfo(sourceDir);
        if (!source.Exists)
            throw new DirectoryNotFoundException("Source directory not found: " + source.FullName);

        Directory.CreateDirectory(destinationDir);

        foreach (FileInfo file in source.GetFiles())
        {
            if (file.Name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || file.Name == ".DS_Store")
                continue;

            file.CopyTo(Path.Combine(destinationDir, file.Name), true);
        }

        foreach (DirectoryInfo subDir in source.GetDirectories())
        {
            if (subDir.Name == ".git")
                continue;

            CopyDirectory(subDir.FullName, Path.Combine(destinationDir, subDir.Name));
        }
    }

    static bool RunTool(string fileName, string arguments, out string output)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using (var process = Process.Start(psi))
        {
            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
    }

    static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
#endif
