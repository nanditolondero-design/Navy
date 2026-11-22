# Navegador

Navegador nativo para Windows (WinUI 3 + WebView2, .NET 8).

- Pestañas, barra de direcciones (URL o búsqueda en Google), atrás/adelante/recargar
- Modo incógnito (Ctrl+Shift+N o menú): perfil temporal separado que se borra al cerrar
- Adblock por dominio (lista incluida + lista pública que se actualiza sola)

Compilar local: `dotnet publish Navegador.csproj -c Release -r win-x64 -p:Platform=x64 -o publish`
