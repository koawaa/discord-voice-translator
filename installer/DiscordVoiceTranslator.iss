; Installateur de Discord Voice Translator (Inno Setup 6).
; Construit par build\release.ps1, qui prépare d'abord artifacts\publish.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Discord Voice Translator"
#define AppExe "DiscordVoiceTranslator.exe"

[Setup]
AppId={{7D3F2B1C-5E8A-4C2B-9F61-2A9E8C4B7D10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
; Installation pour l'utilisateur seul : aucune demande de droits administrateur.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
; La capture du son d'un seul programme (process loopback) n'existe qu'à partir de Windows 11.
MinVersion=10.0.22000
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Si l'appli tourne (mise à jour), l'installateur propose de la fermer.
AppMutex=Local\DiscordVoiceTranslator
SetupIconFile=..\src\DiscordVoiceTranslator.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=..\artifacts
OutputBaseFilename=DiscordVoiceTranslator-Setup-{#AppVersion}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Le modèle Whisper téléchargé au premier lancement (834 Mo). Les réglages et clés chiffrées
; (%APPDATA%\DiscordVoiceTranslator) restent, pour une éventuelle réinstallation.
Type: filesandordirs; Name: "{localappdata}\DiscordVoiceTranslator\models"
