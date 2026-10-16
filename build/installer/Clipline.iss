; The exe installer, made with Inno Setup 6 from the publish folder.
; build/build.ps1 compiles it; to run ISCC by hand:
;   ISCC /DAppVersion=0.1.0 /DArch=x64 /DSourceDir=<publish folder> /DOutputDir=<folder> build\installer\Clipline.iss
;
; It installs per user by default (no administrator prompt) into
; %LOCALAPPDATA%\Programs\Clipline, and offers all users as an option.
;
; The app does not carry the Windows App Runtime. Given RuntimeName,
; RuntimeChannel, RuntimeMinVersion and RuntimeUrl (build.ps1 passes them),
; setup checks for it and, if it is missing, downloads Microsoft's installer
; and runs it.

#ifndef AppVersion
  #error Pass the version: /DAppVersion=x.y.z
#endif
#ifndef SourceDir
  #error Pass the publish folder: /DSourceDir=...
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef RepositoryUrl
  #define RepositoryUrl ""
#endif
; "0.2.0-beta.1" -> "0.2.0" for the file's version resource.
#define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion + "-") - 1)

[Setup]
; Never change AppId: it is how a new version finds the old one to upgrade.
AppId={{E692167C-28BA-4F00-9A77-C162F253EDA6}
AppName=Clipline
AppVersion={#AppVersion}
AppVerName=Clipline {#AppVersion}
AppPublisher=Clipline
#if RepositoryUrl != ""
AppPublisherURL={#RepositoryUrl}
AppSupportURL={#RepositoryUrl}/issues
AppUpdatesURL={#RepositoryUrl}/releases
#endif
VersionInfoVersion={#NumericVersion}
DefaultDirName={autopf}\Clipline
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0.19041
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
; Clipline holds this mutex while it runs, so setup and uninstall ask the
; user to quit it first rather than fail on files in use.
AppMutex=Clipline.SingleInstance
CloseApplications=yes
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename=Clipline-{#AppVersion}-{#Arch}-setup
SetupIconFile=..\..\src\Clipline\Assets\Clipline.ico
UninstallDisplayIcon={app}\Clipline.exe
UninstallDisplayName=Clipline
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
ShowLanguageDialog=auto

[Languages]
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#else
Name: "zh"; MessagesFile: "ChineseSimplified.isl"
#endif
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Clipline"; Filename: "{app}\Clipline.exe"
Name: "{autodesktop}\Clipline"; Filename: "{app}\Clipline.exe"; Tasks: desktopicon

[Registry]
; Clipline writes this value itself when "launch at sign-in" is on; only
; uninstalling removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Clipline"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\Clipline.exe"; Description: "{cm:LaunchProgram,Clipline}"; Flags: nowait postinstall skipifsilent

; Settings and clipboard screenshots in %LOCALAPPDATA%\Clipline are the
; user's, and stay after uninstalling. So does the Windows App Runtime,
; which other apps share.

#ifdef RuntimeName
[CustomMessages]
zh.RuntimeTitle=下载 Windows App Runtime
zh.RuntimeDescription=Clipline 需要 Windows App Runtime {#RuntimeChannel}，这台电脑还没有。正在从微软下载，装好后其他应用也能用。
zh.RuntimeInstalling=正在安装 Windows App Runtime {#RuntimeChannel}…
zh.RuntimeFailed=没能安装 Windows App Runtime {#RuntimeChannel}（%1）。%n%nClipline 仍会装好。第一次启动时 Windows 会提示你下载它，也可以稍后手动安装：%n{#RuntimeUrl}
en.RuntimeTitle=Downloading the Windows App Runtime
en.RuntimeDescription=Clipline needs the Windows App Runtime {#RuntimeChannel}, which this PC does not have yet. Downloading it from Microsoft; other apps can use it too.
en.RuntimeInstalling=Installing the Windows App Runtime {#RuntimeChannel}…
en.RuntimeFailed=The Windows App Runtime {#RuntimeChannel} could not be installed (%1).%n%nClipline is installed anyway. Windows will offer to download the runtime when Clipline first starts, or install it later from:%n{#RuntimeUrl}

[Code]
const
  RuntimeFile = 'WindowsAppRuntimeInstall.exe';

var
  DownloadPage: TDownloadWizardPage;
  RuntimeNeeded, RuntimeTried, RuntimeDownloaded: Boolean;

// Whether dotted version V ("7000.785.2325.0") is at least Min.
function VersionAtLeast(V, Min: String): Boolean;
var
  I, A, B, Dot: Integer;
begin
  for I := 1 to 4 do begin
    Dot := Pos('.', V + '.');
    A := StrToIntDef(Copy(V, 1, Dot - 1), 0);
    Delete(V, 1, Dot);
    Dot := Pos('.', Min + '.');
    B := StrToIntDef(Copy(Min, 1, Dot - 1), 0);
    Delete(Min, 1, Dot);
    if A <> B then begin
      Result := A > B;
      Exit;
    end;
  end;
  Result := True;
end;

// The runtime is a framework package, registered per user, and each one
// this user has is a key named after its full name,
// "<name>_<version>_<arch>__<publisher id>", under the AppModel repository.
// Read there rather than with Get-AppxPackage: setup starting a hidden
// PowerShell is what antivirus behaviour detection holds against it.
function RuntimeInstalled: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  Name, Prefix: String;
begin
  Result := False;
  if not RegGetSubkeyNames(HKCU, 'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages', Names) then
    Exit;
  Prefix := Lowercase('{#RuntimeName}_');
  for I := 0 to GetArrayLength(Names) - 1 do begin
    Name := Lowercase(Names[I]);
    if (Pos(Prefix, Name) = 1) and (Pos(Lowercase('_{#Arch}_'), Name) > 0) then begin
      Delete(Name, 1, Length(Prefix));
      if VersionAtLeast(Copy(Name, 1, Pos('_', Name) - 1), '{#RuntimeMinVersion}') then begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  RuntimeNeeded := not RuntimeInstalled;
  Log(Format('Windows App Runtime {#RuntimeChannel} needed: %d', [Ord(RuntimeNeeded)]));
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(CustomMessage('RuntimeTitle'), CustomMessage('RuntimeDescription'), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

// Into {tmp}. A failed download is reported, never fatal: Clipline still
// installs, and asks for the runtime itself when it starts.
function DownloadRuntime(WithPage: Boolean): Boolean;
begin
  Result := False;
  RuntimeTried := True;
  try
    if WithPage then begin
      DownloadPage.Clear;
      DownloadPage.Add('{#RuntimeUrl}', RuntimeFile, '');
      DownloadPage.Show;
      try
        DownloadPage.Download;
      finally
        DownloadPage.Hide;
      end;
    end else
      DownloadTemporaryFile('{#RuntimeUrl}', RuntimeFile, '', nil);
    Result := True;
  except
    if not (WithPage and DownloadPage.AbortedByUser) then
      SuppressibleMsgBox(FmtMessage(CustomMessage('RuntimeFailed'), [GetExceptionMessage]), mbError, MB_OK, IDOK);
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if (CurPageID = wpReady) and RuntimeNeeded then
    RuntimeDownloaded := DownloadRuntime(True);
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep <> ssPostInstall) or not RuntimeNeeded then
    Exit;
  // In case setup went past the Ready page without downloading: one try
  // only, whether or not the first one worked.
  if not RuntimeTried then
    RuntimeDownloaded := DownloadRuntime(False);
  if not RuntimeDownloaded then
    Exit;
  WizardForm.StatusLabel.Caption := CustomMessage('RuntimeInstalling');
  // In plain sight, with its own progress: a downloaded program run hidden
  // is what antivirus behaviour detection takes setup for a dropper by.
  if not Exec(ExpandConstant('{tmp}\' + RuntimeFile), '', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode)
     or (ResultCode <> 0) then
    SuppressibleMsgBox(FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ResultCode)]), mbError, MB_OK, IDOK);
end;
#endif
