; Installateur de Discord Voice Translator (Inno Setup 6).
; Construit par build\release.ps1, qui prépare d'abord artifacts\publish.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
; Identité fixe : une nouvelle version s'installe par-dessus l'ancienne (même dossier, une seule
; entrée dans « Applications installées »). Surchargeables seulement pour tester l'installateur.
#ifndef AppGuid
  #define AppGuid "7D3F2B1C-5E8A-4C2B-9F61-2A9E8C4B7D10"
#endif
#ifndef AppName
  #define AppName "Discord Voice Translator"
#endif
; Dossier des données de l'appli (réglages et clés dans %APPDATA%, modèles dans %LOCALAPPDATA%).
#ifndef DataDir
  #define DataDir "DiscordVoiceTranslator"
#endif
#define AppExe "DiscordVoiceTranslator.exe"

[Setup]
AppId={{{#AppGuid}}
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
; L'appli ouverte est fermée par le code plus bas, avant d'installer ou de désinstaller.
CloseApplications=no
; Langue de Windows d'office ; la liste n'apparaît que si ce n'est ni l'anglais ni le français.
ShowLanguageDialog=auto
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

[CustomMessages]
en.UpdateInfo=Update from version %1 to %2.%nYour settings, API keys and downloaded speech models are kept.
fr.UpdateInfo=Mise à jour de la version %1 vers la %2.%nTes réglages, clés API et modèles de reconnaissance téléchargés sont conservés.
en.NewerInstalled=A newer version (%1) is already installed. Replace it with version %2 anyway?
fr.NewerInstalled=Une version plus récente (%1) est déjà installée. La remplacer quand même par la %2 ?
en.DeleteSettings=Also delete your settings and saved API keys (DeepL, Discord)?%n%nKeep them if you plan to reinstall later.
fr.DeleteSettings=Supprimer aussi tes réglages et tes clés API enregistrées (DeepL, Discord) ?%n%nGarde-les si tu comptes réinstaller plus tard.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Fichiers de l'ancienne version : ceux que la nouvelle n'a plus ne doivent pas traîner.
Type: filesandordirs; Name: "{app}\runtimes"
Type: filesandordirs; Name: "{app}\Models"
Type: files; Name: "{app}\*.dll"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Les modèles Whisper téléchargés (près d'1 Go chacun). Les réglages et clés chiffrées
; (%APPDATA%) ne partent que si on le demande, voir CurUninstallStepChanged.
Type: filesandordirs; Name: "{localappdata}\{#DataDir}\models"

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1';
  InstanceMutex = 'Local\DiscordVoiceTranslator';
  QuitEvent = 'Local\DiscordVoiceTranslator.Quit';
  EVENT_MODIFY_STATE = $0002;

var
  InstalledVersion: String;

function OpenEvent(DesiredAccess: LongWord; InheritHandle: BOOL; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

{ Ferme l'appli si elle tourne (mise à jour ou désinstallation) : d'abord en le lui demandant,
  pour qu'elle enregistre ses réglages et retire son icône, puis de force si elle ne répond pas
  (versions d'avant la 1.4.0, qui ne connaissent pas cette demande). }
procedure CloseRunningApp();
var
  Event: THandle;
  ResultCode, Waited: Integer;
begin
  if not CheckForMutexes(InstanceMutex) then
    Exit;
  Event := OpenEvent(EVENT_MODIFY_STATE, False, QuitEvent);
  if Event <> 0 then
  begin
    SetEvent(Event);
    CloseHandle(Event);
  end;
  Waited := 0;
  while CheckForMutexes(InstanceMutex) and (Waited < 5000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  if CheckForMutexes(InstanceMutex) then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;
end;

function IsUpdate(): Boolean;
begin
  Result := InstalledVersion <> '';
end;

function InitializeSetup(): Boolean;
var
  Installed, New: Int64;
begin
  Result := True;
  if not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', InstalledVersion) then
    InstalledVersion := '';
  if IsUpdate() and StrToVersion(InstalledVersion, Installed) and StrToVersion('{#AppVersion}', New)
     and (ComparePackedVersion(Installed, New) > 0) then
    Result := SuppressibleMsgBox(FmtMessage(CustomMessage('NewerInstalled'), [InstalledVersion, '{#AppVersion}']),
                                 mbConfirmation, MB_YESNO, IDYES) = IDYES;
end;

{ Mise à jour : les options de la première installation (icône sur le Bureau) sont reprises. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpSelectTasks) and IsUpdate();
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  if IsUpdate() then
    Result := FmtMessage(CustomMessage('UpdateInfo'), [InstalledVersion, '{#AppVersion}'])
  else
    Result := MemoDirInfo;
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  CloseRunningApp();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent() and
     (MsgBox(CustomMessage('DeleteSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    DelTree(ExpandConstant('{userappdata}\{#DataDir}'), True, True, True);
end;
