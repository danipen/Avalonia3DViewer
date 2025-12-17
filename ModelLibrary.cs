using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace Avalonia3DViewer;

public class ModelEntry
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    
    public ModelEntry() { }
    
    public ModelEntry(string path)
    {
        Path = path;
        var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        var parentDir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? "");
        Name = string.IsNullOrEmpty(parentDir) ? fileName : $"{parentDir}/{fileName}";
    }
    
    public override string ToString() => Name;
}

public class ModelLibrary
{
    private static readonly string ConfigPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Avalonia3DViewer",
        "models.json");
    
    public ObservableCollection<ModelEntry> Models { get; } = new();
    
    public ModelLibrary()
    {
        Load();
    }
    
    public void Add(string path)
    {
        if (ContainsPath(path)) return;
        
        Models.Add(new ModelEntry(path));
        Save();
    }

    private bool ContainsPath(string path)
    {
        foreach (var model in Models)
        {
            if (string.Equals(model.Path, path, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
    
    public void Remove(ModelEntry entry)
    {
        Models.Remove(entry);
        Save();
    }
    
    public void Save()
    {
        try
        {
            EnsureConfigDirectoryExists();
            var json = JsonSerializer.Serialize(Models, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelLibrary] Error saving: {ex.Message}");
        }
    }

    private static void EnsureConfigDirectoryExists()
    {
        var directory = System.IO.Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }
    
    public void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

            var json = File.ReadAllText(ConfigPath);
            var models = JsonSerializer.Deserialize<List<ModelEntry>>(json);
            if (models == null) return;

            Models.Clear();
            foreach (var model in models)
            {
                if (File.Exists(model.Path))
                    Models.Add(new ModelEntry(model.Path));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelLibrary] Error loading: {ex.Message}");
        }
    }
}
