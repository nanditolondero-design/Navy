# Navegador

Navegador nativo para Windows (WinUI 3 + WebView2, .NET 8).

- Pestañas integradas en la barra de título, barra de direcciones (URL o búsqueda), atrás/adelante/recargar
- Modo incógnito (Ctrl+Shift+N o menú): perfil temporal separado que se borra al cerrar; no guarda historial
- Descargas: pestaña propia (Ctrl+J), con selector para elegir dónde guardar cada archivo
- Escudos estilo Brave: bloqueo de anuncios/rastreadores, HTTPS automático, limpieza de parámetros de seguimiento,
  señales GPC/DNT, prevención de seguimiento y apagado por sitio (botón 🛡)
- Historial (Ctrl+H), marcadores (estrella / Ctrl+D) y configuración
- Importar marcadores e historial desde Google Chrome (Configuración)

Compilar local: `dotnet publish Navegador.csproj -c Release -r win-x64 -p:Platform=x64 -o publish`
