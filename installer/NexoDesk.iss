; Instalador único do NexoDesk (o mesmo para ADMIN e PC REMOTO).
; Compilar: iscc /DAppVersion=0.3.0 installer\NexoDesk.iss   (depois de publicar em publish\app)

#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif

#define AppName "NexoDesk"
#define AppExe "NexoDesk.exe"
#define FirewallRule "NexoDesk (Tailscale)"
#define AppPort "47115"

[Setup]
AppId={{8C5E2B7A-4E1D-4C1B-9B0A-6E6E2D7F3A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Nexotool
AppCopyright=Nexotool
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=NexoDesk-Setup-{#AppVersion}
SetupIconFile=..\src\NexoDesk.App\Assets\nexodesk.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Administrador só para instalar em Arquivos de Programas e criar a regra de firewall. O programa roda sem elevação.
PrivilegesRequired=admin
MinVersion=10.0.17763
CloseApplications=force
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; GroupDescription: "Atalhos:"

[Files]
; Publicação self-contained: não precisa instalar o .NET.
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Regra de entrada: só o NexoDesk, só TCP {#AppPort}, só endereços da rede Tailscale. Usada no modo PC REMOTO.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden waituntilterminated; StatusMsg: "Configurando o firewall..."
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow program=""{app}\{#AppExe}"" protocol=TCP localport={#AppPort} remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48 profile=any"; Flags: runhidden waituntilterminated; StatusMsg: "Configurando o firewall..."
; Abre o programa como o usuário que instalou (nunca elevado).
Filename: "{app}\{#AppExe}"; Description: "Abrir o NexoDesk agora"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden waituntilterminated; RunOnceId: "FecharNexoDesk"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoverFirewall"

[Registry]
; Remove o início automático (criado pelo próprio programa no modo PC REMOTO) ao desinstalar.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "NexoDesk"; Flags: uninsdeletevalue

[Messages]
brazilianportuguese.WelcomeLabel2=Isto vai instalar o [name/ver] neste computador.%n%nO mesmo instalador serve para os dois lados: na primeira abertura você escolhe se este computador é o ADMIN (quem acessa) ou um PC REMOTO (quem é acessado).%n%nO Tailscale precisa estar instalado e conectado nos dois computadores.
