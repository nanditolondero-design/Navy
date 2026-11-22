using System.Text.Json;

namespace Navegador.Services;

internal static class JsonStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Navegador");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string fileName) where T : new()
    {
        try
        {
            var path = Path.Combine(Dir, fileName);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
        }
        catch
        {
            // archivo corrupto: se empieza de cero
        }
        return new T();
    }

    public static void Save<T>(string fileName, T data)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, fileName), JsonSerializer.Serialize(data, Options));
        }
        catch
        {
        }
    }
}
