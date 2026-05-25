#if UNITY_STANDALONE_OSX

using System;
using System.Collections.Generic;
using System.IO;
using Kirurobo;
using UnityEngine;

namespace SFB {
    public class StandaloneFileBrowserMac : IStandaloneFileBrowser {
        public string[] OpenFilePanel(string title, string directory, ExtensionFilter[] extensions, bool multiselect) {
            string[] result = Array.Empty<string>();
            var settings = new FilePanel.Settings {
                title = title,
                initialDirectory = NormalizeDirectory(directory),
                filters = ConvertFilters(extensions),
                flags = multiselect ? FilePanel.Flag.AllowMultipleSelection : FilePanel.Flag.None
            };

            FilePanel.OpenFilePanel(settings, paths => {
                result = NormalizeResult(paths);
            });

            return result;
        }

        public void OpenFilePanelAsync(string title, string directory, ExtensionFilter[] extensions, bool multiselect, Action<string[]> cb) {
            cb?.Invoke(OpenFilePanel(title, directory, extensions, multiselect));
        }

        public string[] OpenFolderPanel(string title, string directory, bool multiselect) {
            Debug.LogWarning("[StandaloneFileBrowserMac] Folder selection is not supported by the macOS backend.");
            return Array.Empty<string>();
        }

        public void OpenFolderPanelAsync(string title, string directory, bool multiselect, Action<string[]> cb) {
            cb?.Invoke(OpenFolderPanel(title, directory, multiselect));
        }

        public string SaveFilePanel(string title, string directory, string defaultName, ExtensionFilter[] extensions) {
            string[] result = Array.Empty<string>();
            var settings = new FilePanel.Settings {
                title = title,
                initialDirectory = NormalizeDirectory(directory),
                initialFile = defaultName ?? "",
                filters = ConvertFilters(extensions),
                flags = FilePanel.Flag.CanCreateDirectories | FilePanel.Flag.OverwritePrompt
            };

            FilePanel.SaveFilePanel(settings, paths => {
                result = NormalizeResult(paths);
            });

            return result.Length > 0 ? result[0] : "";
        }

        public void SaveFilePanelAsync(string title, string directory, string defaultName, ExtensionFilter[] extensions, Action<string> cb) {
            cb?.Invoke(SaveFilePanel(title, directory, defaultName, extensions));
        }

        private static FilePanel.Filter[] ConvertFilters(ExtensionFilter[] extensions) {
            if (extensions == null || extensions.Length == 0)
                return new[] { new FilePanel.Filter("All files", "*") };

            var filters = new List<FilePanel.Filter>(extensions.Length + 1) {
                new FilePanel.Filter("All files", "*")
            };
            foreach (var extension in extensions) {
                if (extension.Extensions == null || extension.Extensions.Length == 0)
                    continue;

                var normalizedExtensions = new List<string>(extension.Extensions.Length);
                foreach (string raw in extension.Extensions) {
                    string value = (raw ?? "").Trim().TrimStart('.');
                    if (!string.IsNullOrWhiteSpace(value))
                        normalizedExtensions.Add(value);
                }

                if (normalizedExtensions.Count > 0)
                    filters.Add(new FilePanel.Filter(extension.Name ?? "", normalizedExtensions.ToArray()));
            }

            return filters.ToArray();
        }

        private static string NormalizeDirectory(string directory) {
            if (string.IsNullOrWhiteSpace(directory))
                return "";

            try {
                if (Directory.Exists(directory))
                    return directory;

                if (File.Exists(directory))
                    return Path.GetDirectoryName(directory) ?? "";
            }
            catch {
                return "";
            }

            return "";
        }

        private static string[] NormalizeResult(string[] paths) {
            if (paths == null || paths.Length == 0)
                return Array.Empty<string>();

            var result = new List<string>(paths.Length);
            foreach (string path in paths) {
                if (!string.IsNullOrWhiteSpace(path))
                    result.Add(path);
            }

            return result.ToArray();
        }
    }
}

#endif
