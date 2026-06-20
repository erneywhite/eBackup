; ───────────────────────────────────────────────────────────────────────────
;  eBackup — установщик (Inno Setup). Стилизован под приложение (аврора-баннер).
;  Собирается build-installer.ps1: сначала dotnet publish → installer\..\publish,
;  затем ISCC по этому скрипту. Версия задаётся параметром /DAppVersion.
; ───────────────────────────────────────────────────────────────────────────

#ifndef AppVersion
  #define AppVersion "1.3.0"
#endif

#define AppName "eBackup"
#define AppPublisher "Erney White"
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
; Служба eBackup под LocalSystem (автозапуск при загрузке — бэкапы без входа в систему,
; чтение системных файлов вроде хост-ключей OpenSSH). Создаётся и запускается сразу.
; ВАЖНО: после "=" в sc обязателен пробел.
Filename: "{sys}\sc.exe"; \
  Parameters: "create eBackup binPath= ""{app}\service\eBackup.Service.exe"" start= auto obj= LocalSystem DisplayName= ""eBackup"""; \
  Flags: runhidden
Filename: "{sys}\sc.exe"; \
  Parameters: "description eBackup ""Привилегированные бэкапы eBackup: чтение системных файлов и расписания без входа в систему."""; \
  Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start eBackup"; Flags: runhidden

; Обычная установка — кнопка «Запустить eBackup» на финальной странице.
Filename: "{app}\{#AppExe}"; Description: "Запустить eBackup"; \
  Flags: nowait postinstall skipifsilent
; Тихая установка (из автообновления приложения) — сами перезапускаем приложение
; от имени обычного пользователя (установщик идёт с правами администратора).
Filename: "{app}\{#AppExe}"; Parameters: "--minimized"; \
  Flags: nowait runasoriginaluser; Check: IsSilentInstall

[UninstallRun]
; Остановить и удалить службу перед снятием файлов (иначе exe залочен).
Filename: "{sys}\sc.exe"; Parameters: "stop eBackup"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete eBackup"; Flags: runhidden; RunOnceId: "DelSvc"
; Закрыть работающий экземпляр GUI перед удалением (тихо, без ошибки если не запущен).
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
// true при тихой установке (/SILENT|/VERYSILENT) — отличает автообновление от ручной.
function IsSilentInstall: Boolean;
begin
  Result := WizardSilent();
end;

// Перед копированием файлов: при апгрейде остановить и удалить старую службу и закрыть GUI,
// иначе их exe залочены. Для чистой установки эти команды просто отрабатывают вхолостую.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop eBackup', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete eBackup', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(1500); // дать SCM освободить exe службы перед перезаписью
  Result := '';
end;

// При удалении предлагаем убрать настройки/историю/секреты (бэкап-архивы НЕ трогаем).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if MsgBox('Удалить настройки, расписания, историю и сохранённые пароли eBackup?' + #13#10 +
              '(Созданные архивы-бэкапы НЕ удаляются.)',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\eBackup'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\eBackup'), True, True, True);
      DelTree(ExpandConstant('{commonappdata}\eBackup'), True, True, True); // машинный ключ + конфиг службы
    end;
  end;
end;
