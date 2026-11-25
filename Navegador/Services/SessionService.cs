namespace Navegador.Services;

public sealed class SessionData
{
    public List<string> Urls { get; set; } = new();
    public int Selected { get; set; }
}

/// <summary>Guarda las pestañas abiertas (sin incógnito) para restaurarlas al abrir.</summary>
public sealed class SessionService
{
    private const string FileName = "session.json";

    public SessionData Load() => JsonStore.Load<SessionData>(FileName);

    public void Save(IEnumerable<string> urls, int selected) =>
        JsonStore.Save(FileName, new SessionData { Urls = urls.ToList(), Selected = selected });
}
