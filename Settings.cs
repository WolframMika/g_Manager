using System;
using System.IO;
using System.Text;

namespace g_Manager
{
    public sealed class Settings
    {
        public const string DefaultSettingsFileName = "settings.txt";

        public string SettingsFilePath { get; private set; }

        public string FilePath { get; set; }

        public string Arguments { get; set; }

        public Settings()
            : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultSettingsFileName))
        {
        }

        public Settings(string settingsFilePath)
        {
            if (string.IsNullOrWhiteSpace(settingsFilePath))
                throw new ArgumentException("Settings file path cannot be empty.", "settingsFilePath");

            SettingsFilePath = settingsFilePath;
            FilePath = string.Empty;
            Arguments = string.Empty;
        }

        public void Load()
        {
            EnsureSettingsFileExists();

            FilePath = string.Empty;
            Arguments = string.Empty;

            string[] lines = File.ReadAllLines(SettingsFilePath, Encoding.UTF8);

            foreach (string rawLine in lines)
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                string line = rawLine.Trim();

                if (line.StartsWith("#"))
                    continue;

                int equalsIndex = line.IndexOf('=');

                if (equalsIndex <= 0)
                    continue;

                string key = line.Substring(0, equalsIndex).Trim();
                string value = line.Substring(equalsIndex + 1).Trim();

                if (key.Equals("FilePath", StringComparison.OrdinalIgnoreCase))
                {
                    FilePath = value;
                }
                else if (key.Equals("Arguments", StringComparison.OrdinalIgnoreCase))
                {
                    Arguments = value;
                }
            }
        }

        public void Save()
        {
            string directory = Path.GetDirectoryName(SettingsFilePath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            StringBuilder builder = new StringBuilder();

            builder.AppendLine("# Console wrapper settings");
            builder.AppendLine("# FilePath should point to the target .exe");
            builder.AppendLine("# Arguments are passed to the target process at startup");
            builder.AppendLine("FilePath=" + (FilePath ?? string.Empty));
            builder.AppendLine("Arguments=" + (Arguments ?? string.Empty));

            File.WriteAllText(SettingsFilePath, builder.ToString(), Encoding.UTF8);
        }

        public void SaveFilePath(string filePath)
        {
            Load();
            FilePath = filePath ?? string.Empty;
            Save();
        }

        public void SaveArguments(string arguments)
        {
            Load();
            Arguments = arguments ?? string.Empty;
            Save();
        }

        private void EnsureSettingsFileExists()
        {
            if (File.Exists(SettingsFilePath))
                return;

            FilePath = string.Empty;
            Arguments = string.Empty;
            Save();
        }
    }
}