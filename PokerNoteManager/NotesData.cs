using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PokerNoteManager
{
    /// <summary>One stored note text with the time it was saved (used by the history list).</summary>
    public sealed class NoteSnapshot
    {
        [JsonPropertyName("when")] public string When { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
    }

    /// <summary>
    /// One player row. The property names match the on disk format that the earlier version of the
    /// program wrote, so existing notes files keep loading.
    /// </summary>
    public sealed class PlayerEntry
    {
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        [JsonPropertyName("note_history")] public List<NoteSnapshot>? NoteHistory { get; set; }
        [JsonPropertyName("aliases")] public List<string>? Aliases { get; set; }

        /// <summary>
        /// Stats from the old tracker version. They are never shown or changed any more, but they are
        /// kept as they are so that saving a note never destroys data that was collected before.
        /// Delete the fields by hand in the .json file if they should go away for good.
        /// </summary>
        [JsonPropertyName("stats")] public JsonNode? Stats { get; set; }

        /// <summary>Watchlist flag from the old version - kept for the same reason.</summary>
        [JsonPropertyName("watch")] public bool? Watch { get; set; }
    }

    /// <summary>The notes database file: { "version": 1, "players": { name: {...} } }.</summary>
    public sealed class NotesFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("players")] public Dictionary<string, PlayerEntry> Players { get; set; } = new();
    }

    /// <summary>A tag definition (same format as %AppData%\PokerNoteManager_Tags.json).</summary>
    public sealed class TagDef
    {
        public string TagKey { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ColorHex { get; set; } = "#808080";
    }

    /// <summary>
    /// File access for the notes database, the tag list and the small settings files. All writes are
    /// atomic (temp file + move) and keep one .bak copy of the previous database.
    /// </summary>
    public static class NotesStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string AppDataDir { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static string SettingsPath { get; } = Path.Combine(AppDataDir, "PokerNoteManager_Settings.txt");
        public static string TagsPath { get; } = Path.Combine(AppDataDir, "PokerNoteManager_Tags.json");
        public static string WindowStatePath { get; } = Path.Combine(AppDataDir, "PokerNotes_Window.txt");

        public static string DefaultDbPath()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string p = File.ReadAllText(SettingsPath).Trim();
                    if (p.Length > 0) return p;
                }
            }
            catch (Exception ex) { PvLog.Error("NotesStore.DefaultDbPath", ex); }

            return Path.Combine(AppContext.BaseDirectory, "players.json");
        }

        public static NotesFile Load(string path)
        {
            if (!File.Exists(path)) return new NotesFile();
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new NotesFile();
            NotesFile? file = JsonSerializer.Deserialize<NotesFile>(json, JsonOpts);
            if (file == null) return new NotesFile();
            file.Players ??= new Dictionary<string, PlayerEntry>();
            return file;
        }

        public static void Save(string path, NotesFile file)
        {
            string dir = Path.GetDirectoryName(path) ?? ".";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // Keep one backup of the previous state, then write atomically.
            try
            {
                if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            }
            catch (Exception ex) { PvLog.Error("NotesStore.Save backup", ex); }

            NotesFile ordered = new()
            {
                Version = file.Version,
                Players = file.Players
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            };

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(ordered, JsonOpts));
            File.Move(tmp, path, true);
        }

        public static void SaveSettings(string dbPath)
        {
            try { File.WriteAllText(SettingsPath, dbPath); }
            catch (Exception ex) { PvLog.Error("NotesStore.SaveSettings", ex); }
        }

        public static List<TagDef> LoadTags()
        {
            try
            {
                if (File.Exists(TagsPath))
                {
                    List<TagDef>? tags = JsonSerializer.Deserialize<List<TagDef>>(File.ReadAllText(TagsPath), JsonOpts);
                    if (tags != null) return tags.Where(t => !string.IsNullOrWhiteSpace(t.TagKey)).ToList();
                }
            }
            catch (Exception ex) { PvLog.Error("NotesStore.LoadTags", ex); }
            return new List<TagDef>();
        }

        public static void SaveTags(List<TagDef> tags)
        {
            try
            {
                string tmp = TagsPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(tags, JsonOpts));
                File.Move(tmp, TagsPath, true);
            }
            catch (Exception ex) { PvLog.Error("NotesStore.SaveTags", ex); }
        }
    }
}
