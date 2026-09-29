Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

!define PRODUCT "Root Six Player"
!define REGKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\RootSixPlayer"
Name "${PRODUCT}"
OutFile "${OUTPUT}"
RequestExecutionLevel user
SetCompressor /SOLID lzma
InstallDir "$LOCALAPPDATA\Programs\Root Six Player"
Icon "${ICON}"
UninstallIcon "${ICON}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${PRODUCT}"
VIAddVersionKey "FileDescription" "${PRODUCT} Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "Root Six Player contributors"

Var Updating
Var ParentPID
Var Failure
Var HadLauncher
Var HadUninstaller
Var SetupMutex
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\RootSixPlayer.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Open Root Six Player"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!macro AcquireInstallerMutex
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\RootSixPlayerSetup") p.r0 ?e'
  StrCpy $SetupMutex $0
  Pop $1
  ${If} $SetupMutex == 0
  ${OrIf} $1 == 183
    MessageBox MB_OK|MB_ICONSTOP "Another Root Six Player installer is open. Close it and try again."
    SetErrorLevel 1
    Quit
  ${EndIf}
!macroend

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "Root Six Player requires 64-bit Windows."
    SetErrorLevel 1
    Quit
  ${EndIf}
  !insertmacro AcquireInstallerMutex
  SetShellVarContext current
  SetRegView 64
  ; A fixed per-user location keeps updates and shortcuts on the same executable.
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\Root Six Player"
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/UPDATE" $1
  StrCpy $Updating 0
  ${IfNot} ${Errors}
    StrCpy $Updating 1
  ${EndIf}
  ${GetOptions} $0 "/PARENT=" $ParentPID
FunctionEnd

Function Fail
  ; Also display failures during automatic updates so the launcher cannot
  ; disappear silently. Do not terminate any running game or launcher.
  MessageBox MB_OK|MB_ICONSTOP "$Failure$\r$\n$\r$\nYour game files and saved matches are unchanged. Close the launcher and run Setup again."
  SetErrorLevel 1
  Quit
FunctionEnd

Section "Install"
  ${If} $ParentPID != ""
    System::Call 'kernel32::OpenProcess(i 0x100000, i 0, i $ParentPID) p.r0'
    ${If} $0 != 0
      System::Call 'kernel32::WaitForSingleObject(p r0, i 30000) i.r1'
      System::Call 'kernel32::CloseHandle(p r0)'
      ${If} $1 != 0
        StrCpy $Failure "The launcher has not closed. The update was not installed."
        Call Fail
      ${EndIf}
    ${EndIf}
  ${EndIf}
  ; Recover a previous file swap interrupted before the replacement was renamed.
  IfFileExists "$INSTDIR\RootSixPlayer.exe" +2 0
    Rename "$INSTDIR\RootSixPlayer.exe.previous" "$INSTDIR\RootSixPlayer.exe"
  IfFileExists "$INSTDIR\Uninstall.exe" +2 0
    Rename "$INSTDIR\Uninstall.exe.previous" "$INSTDIR\Uninstall.exe"
  ClearErrors
  SetOutPath "$INSTDIR"
  File /oname=RootSixPlayer.exe.new "${LAUNCHER}"
  WriteUninstaller "$INSTDIR\Uninstall.exe.new"
  ${If} ${Errors}
    StrCpy $Failure "Setup could not write to $INSTDIR. The installed launcher was not replaced."
    Call Fail
  ${EndIf}

  StrCpy $HadLauncher 0
  StrCpy $HadUninstaller 0
  Delete "$INSTDIR\RootSixPlayer.exe.previous"
  Delete "$INSTDIR\Uninstall.exe.previous"
  ClearErrors
  ${If} ${FileExists} "$INSTDIR\RootSixPlayer.exe"
    Rename "$INSTDIR\RootSixPlayer.exe" "$INSTDIR\RootSixPlayer.exe.previous"
    ${If} ${Errors}
      StrCpy $Failure "The installed launcher is still in use. The update was not installed."
      Call Fail
    ${EndIf}
    StrCpy $HadLauncher 1
  ${EndIf}
  ${If} ${FileExists} "$INSTDIR\Uninstall.exe"
    Rename "$INSTDIR\Uninstall.exe" "$INSTDIR\Uninstall.exe.previous"
    ${If} ${Errors}
      Goto rollback
    ${EndIf}
    StrCpy $HadUninstaller 1
  ${EndIf}
  Rename "$INSTDIR\RootSixPlayer.exe.new" "$INSTDIR\RootSixPlayer.exe"
  Rename "$INSTDIR\Uninstall.exe.new" "$INSTDIR\Uninstall.exe"
  ${If} ${Errors}
    Goto rollback
  ${EndIf}
  Delete "$INSTDIR\RootSixPlayer.exe.previous"
  Delete "$INSTDIR\Uninstall.exe.previous"
  ClearErrors
  CreateShortcut "$SMPROGRAMS\Root Six Player.lnk" "$INSTDIR\RootSixPlayer.exe"
  WriteRegStr HKCU "${REGKEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr HKCU "${REGKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${REGKEY}" "DisplayIcon" "$INSTDIR\RootSixPlayer.exe"
  WriteRegStr HKCU "${REGKEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${REGKEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${REGKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${REGKEY}" "NoRepair" 1
  ${If} ${Errors}
    StrCpy $Failure "The launcher was installed, but Setup could not create its Start menu shortcut or registration."
    Call Fail
  ${EndIf}
  ${If} $Updating == 1
    ClearErrors
    Exec '"$INSTDIR\RootSixPlayer.exe" --no-update'
    ${If} ${Errors}
      StrCpy $Failure "The update was installed, but the launcher could not reopen. Open Root Six Player from the Start menu."
      Call Fail
    ${EndIf}
  ${EndIf}
  SetErrorLevel 0
  Goto done

  rollback:
    ${If} $HadLauncher == 1
      Delete "$INSTDIR\RootSixPlayer.exe"
      Rename "$INSTDIR\RootSixPlayer.exe.previous" "$INSTDIR\RootSixPlayer.exe"
    ${EndIf}
    ${If} $HadUninstaller == 1
      Delete "$INSTDIR\Uninstall.exe"
      Rename "$INSTDIR\Uninstall.exe.previous" "$INSTDIR\Uninstall.exe"
    ${EndIf}
    StrCpy $Failure "Setup could not replace the launcher. Run Setup again to complete installation."
    Call Fail
  done:
SectionEnd

Function un.onInit
  !insertmacro AcquireInstallerMutex
  SetShellVarContext current
  SetRegView 64
FunctionEnd

Section "Uninstall"
  ClearErrors
  Delete "$INSTDIR\RootSixPlayer.exe"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "Close Root Six Player and run Uninstall again. Your game files and saved matches are unchanged."
    SetErrorLevel 1
    Quit
  ${EndIf}
  Delete "$SMPROGRAMS\Root Six Player.lnk"
  DeleteRegKey HKCU "${REGKEY}"
  Delete "$INSTDIR\RootSixPlayer.exe.new"
  Delete "$INSTDIR\RootSixPlayer.exe.previous"
  Delete "$INSTDIR\Uninstall.exe.new"
  Delete "$INSTDIR\Uninstall.exe.previous"
  Delete "$INSTDIR\Uninstall.exe"
  ; Never recursively remove the install directory or touch RootSixPlayer data.
  RMDir "$INSTDIR"
  SetErrorLevel 0
SectionEnd
