; ───────────────────────────────────────────────────────────────────────────
;  eBackup — установщик (Inno Setup). Стилизован под приложение (аврора-баннер).
;  Собирается build-installer.ps1: сначала dotnet publish → installer\..\publish,
;  затем ISCC по этому скрипту. Версия задаётся параметром /DAppVersion.
; ───────────────────────────────────────────────────────────────────────────

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define AppName "eBackup"
#define AppPublisher "Erney"
#define AppExe "eBackup.App.exe"
#define AppUrl "https://github.com/erneywhite/eBackup"

[Setup]
AppId={{8F3D2A14-6E1B-4C7A-9D2E-EB0C0FFEE001}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableReadyPage=no
; Установка для всех пользователей (Program Files) — нужны права администратора.
PrivilegesRequired=admin
; Автозапуск намеренно per-user (HKCU) — совпадает с тумблером в Настройках приложения.
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=dist
OutputBaseFilename=eBackup-setup-{#AppVersion}-x64
SetupIconFile=..\src\eBackup.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardImageFile=wizard-banner.bmp
WizardSmallImageFile=wizard-small.bmp
WizardImageStretch=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Запускать eBackup при входе в Windows (свёрнуто в трей)"; GroupDescription: "Дополнительно:"
Name: "assoc"; Description: "Открывать файлы .ebk через eBackup"; GroupDescription: "Дополнительно:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Удалить {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Автозапуск — для пользователя, запускающего установку (per-user, как и настройки приложения).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "eBackup"; ValueData: """{app}\{#AppExe}"" --minimized"; \
  Flags: uninsdeletevalue; Tasks: autostart

; Ассоциация .ebk → eBackup (двойной клик открывает браузер архива).
Root: HKA; Subkey: "Software\Classes\.ebk"; ValueType: string; ValueName: ""; \
  ValueData: "eBackup.Archive"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\eBackup.Archive"; ValueType: string; ValueName: ""; \
  ValueData: "Архив eBackup"; Flags: uninsdeletekey; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\eBackup.Archive\DefaultIcon"; ValueType: string; \
  ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\eBackup.Archive\shell\open\command"; ValueType: string; \
  ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assoc

[Run]
Filename: "{app}\{#AppExe}"; Description: "Запустить eBackup"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Закрыть работающий экземпляр перед удалением (тихо, без ошибки если не запущен).
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
// При удалении предлагаем убрать настройки/историю (бэкап-архивы НЕ трогаем).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppData, LocalAppData: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    if MsgBox('Удалить настройки, расписания и историю eBackup?' + #13#10 +
              '(Созданные архивы-бэкапы НЕ удаляются.)',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      AppData := ExpandConstant('{userappdata}\eBackup');
      LocalAppData := ExpandConstant('{localappdata}\eBackup');
      DelTree(AppData, True, True, True);
      DelTree(LocalAppData, True, True, True);
    end;
  end;
end;
