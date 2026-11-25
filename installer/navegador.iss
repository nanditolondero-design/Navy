; Instalador de Navegador (Inno Setup). Se compila en GitHub Actions después de publicar la app.
#define AppName "Navegador"
#define AppVersion "1.0.0"

[Setup]
AppId={{8D6B6F0E-4B1C-4E6A-9C2E-5A1F3C7D9B21}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Navegador
DefaultDirName={autopf}\Navegador
DefaultGroupName=Navegador
DisableProgramGroupPage=yes
OutputDir=..\installer-output
OutputBaseFilename=Navegador-Setup
SetupIconFile=..\Navegador\Assets\icon.ico
UninstallDisplayIcon={app}\Navegador.exe
Compression=lzma2
SolidCompression=yes
; Instalación por usuario: no pide permisos de administrador
PrivilegesRequired=lowest
WizardStyle=modern

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Navegador"; Filename: "{app}\Navegador.exe"
Name: "{autodesktop}\Navegador"; Filename: "{app}\Navegador.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Navegador.exe"; Description: "Abrir Navegador"; Flags: nowait postinstall skipifsilent
