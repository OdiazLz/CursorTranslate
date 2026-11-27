using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;

namespace CursorTranslate;

public class Settings
{
    public bool Enabled { get; set; } = true;
    public bool CtrlOnly { get; set; } = false;
    public string Mode { get; set; } = "sentence";
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "es";
    public string DeepLApiKey { get; set; } = "";

    static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CursorTranslate");
    static readonly string filePath = Path.Combine(FolderPath, "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(filePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(filePath)) ?? new Settings();
        }catch { Console.WriteLine("Error al obtener el archivo json "); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(filePath, JsonSerializer.Serialize(this, Options));
        }
        catch { Console.WriteLine("Error al guardar los ajustes"); }
    }
}