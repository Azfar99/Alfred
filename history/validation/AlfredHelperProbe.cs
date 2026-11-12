using System;
using System.IO;
using System.Reflection;
using System.Text;

static class AlfredHelperProbe {
    static void Main() {
        string root = @"F:\Alfred";
        string sandbox = AppDomain.CurrentDomain.BaseDirectory;
        string data = Path.Combine(sandbox, "data");
        StringBuilder report = new StringBuilder();
        try {
            Assembly app = Assembly.LoadFrom(Path.Combine(root, "Alfred.exe"));
            Type helpers = app.GetType("Alfred.Helpers", true);
            MethodInfo save = helpers.GetMethod("SaveNote", BindingFlags.Public | BindingFlags.Static);
            PropertyInfo notes = helpers.GetProperty("NotesFile", BindingFlags.Public | BindingFlags.Static);
            save.Invoke(null, new object[] { "ALFRED_ISOLATED_NOTE_CHECK" });
            string notesPath = (string)notes.GetValue(null, null);
            if (!notesPath.StartsWith(data, StringComparison.OrdinalIgnoreCase)) throw new Exception("Notes escaped the isolated F: test root");
            if (!File.ReadAllText(notesPath).Contains("ALFRED_ISOLATED_NOTE_CHECK")) throw new Exception("Saved note did not persist");
            report.AppendLine("PASS note save/persistence inside isolated F: data root");

            MethodInfo find = helpers.GetMethod("FindApp", BindingFlags.Public | BindingFlags.Static);
            string[] names = { "Discord", "Steam", "VALORANT", "Grand Theft Auto V Enhanced", "Word", "Excel", "PowerPoint", "Visual Studio Code", "Wondershare Filmora 13", "VLC media player", "Audacity", "Adobe Acrobat", "Task Manager", "Rockstar Games Launcher" };
            foreach (string name in names) {
                string shortcut = (string)find.Invoke(null, new object[] { name });
                if (String.IsNullOrEmpty(shortcut) || !File.Exists(shortcut)) throw new Exception("Shortcut missing: " + name);
                report.AppendLine("PASS installed shortcut: " + name);
            }

            string memory = (string)helpers.GetMethod("MemoryInfo", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            string disk = (string)helpers.GetMethod("DiskInfo", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            if (String.IsNullOrEmpty(memory) || String.IsNullOrEmpty(disk)) throw new Exception("Resource query returned no text");
            report.AppendLine("PASS RAM query: " + memory);
            report.AppendLine("PASS F: disk query: " + disk);
            File.WriteAllText(Path.Combine(root, "build", "helper-integration-check.txt"), report.ToString(), Encoding.UTF8);
            Console.Write(report.ToString());
        } finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
    }
}
