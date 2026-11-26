; UTF-8. Compile with scripts/build-installer.ps1.
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "Sections.nsh"
!include "x64.nsh"

!ifndef PRODUCT_VERSION
  !error "Use scripts/build-installer.ps1."
!endif
!ifndef INSTALLER_PROFILE
  !define INSTALLER_PROFILE "SaruMon"
!endif
!define APP_KEY "Software\${INSTALLER_PROFILE}"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${INSTALLER_PROFILE}"
!define PRODUCT_ID "${INSTALLER_PROFILE}-Local-Install-v1"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"

!if "${INSTALLER_PROFILE}" == "SaruMon"
  Name "SaruMon ${PRODUCT_VERSION}"
!else
  Name "SaruMon ${PRODUCT_VERSION}（検証用）"
!endif
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\${INSTALLER_PROFILE}"
InstallDirRegKey HKCU "${APP_KEY}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID zlib
SetOverwrite on
BrandingText "SaruMon"
VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=1041 "ProductName" "SaruMon"
VIAddVersionKey /LANG=1041 "FileDescription" "SaruMon Windows x64 セットアップ"
VIAddVersionKey /LANG=1041 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1041 "LegalCopyright" "SaruMon contributors"

!define MUI_ABORTWARNING
!define MUI_ICON "../assets/SaruMon.ico"
!define MUI_UNICON "../assets/SaruMon.ico"
!if "${INSTALLER_PROFILE}" == "SaruMon"
  !define MUI_WELCOMEPAGE_TEXT "SaruMon を現在のユーザー用にインストールします。管理者権限は不要です。$\r$\n$\r$\n更新する場合は、通知領域の SaruMon アイコンを右クリックし、「終了」を選んでください。ウィンドウを閉じるだけでは終了しません。"
!else
  !define MUI_WELCOMEPAGE_TITLE "SaruMon セットアップ（検証用）"
  !define MUI_WELCOMEPAGE_TEXT "これはインストーラーの検証用です。通常利用には正式な SaruMon セットアップを使用してください。$\r$\n$\r$\n検証用の登録名:$\r$\n${INSTALLER_PROFILE}$\r$\n$\r$\n完了画面からアプリは起動しません。"
!endif
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!if "${INSTALLER_PROFILE}" == "SaruMon"
  !define MUI_FINISHPAGE_RUN "$INSTDIR\SaruMon.exe"
  !define MUI_FINISHPAGE_RUN_TEXT "SaruMon を起動する"
  !define MUI_FINISHPAGE_RUN_NOTCHECKED
!endif
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "Japanese"

Function ValidateInstallDirectory
  ; GetFullPathNameW also handles destinations whose parent does not exist yet.
  System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i.r1'
  ${If} $1 == 0
  ${OrIf} $1 >= ${NSIS_MAX_STRLEN}
    Goto bad_directory
  ${EndIf}
  StrCpy $INSTDIR $0
  ${GetRoot} "$INSTDIR" $0
  ${If} $INSTDIR == $0
  ${OrIf} $INSTDIR == "$0\"
    Goto bad_directory
  ${EndIf}
  IfFileExists "$INSTDIR\*" 0 done_directory
  ReadINIStr $0 "$INSTDIR\SaruMon.install.ini" "Install" "Product"
  ${If} $0 != "${PRODUCT_ID}"
    ; Check only immediate entries. Never traverse an unrelated directory.
    FindFirst $1 $0 "$INSTDIR\*"
    ${DoWhile} $0 != ""
      ${If} $0 != "."
      ${AndIf} $0 != ".."
        FindClose $1
        Goto bad_directory
      ${EndIf}
      FindNext $1 $0
    ${Loop}
    FindClose $1
    Goto done_directory
  ${EndIf}
  IfFileExists "$INSTDIR\SaruMon.exe" 0 done_directory
  ClearErrors
  FileOpen $0 "$INSTDIR\SaruMon.exe" a
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "SaruMon を終了してから、セットアップを再実行してください。$\r$\n通知領域のアイコンを右クリック →「終了」を選びます。ウィンドウの × では終了しません。$\r$\n終了済みの場合は、このフォルダーの書き込み権限を確認してください。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  FileClose $0
  Goto done_directory
bad_directory:
  MessageBox MB_OK|MB_ICONSTOP "空のフォルダー、または SaruMon をインストール済みのフォルダーを選択してください。ドライブのルートにはインストールできません。" /SD IDOK
  SetErrorLevel 2
  Abort
done_directory:
FunctionEnd

Section "SaruMon 本体（必須）" SEC_APP
  SectionIn RO
  Call ValidateInstallDirectory
  ClearErrors
  SetOutPath "$INSTDIR"
  File "${PAYLOAD_DIR}/SaruMon.exe"
  File "../README.md"
  SetOutPath "$INSTDIR\docs"
  File "../docs/development.md"
  File "../docs/releasing.md"
  File "../docs/verification.md"
  File "../docs/third-party-notices.md"
  SetOutPath "$INSTDIR"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "ファイルを配置できませんでした。SaruMon が終了していることと、保存先への書き込み権限を確認してください。" /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  WriteINIStr "$INSTDIR\SaruMon.install.ini" "Install" "Product" "${PRODUCT_ID}"
  WriteINIStr "$INSTDIR\SaruMon.install.ini" "Install" "Version" "${PRODUCT_VERSION}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${APP_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${INSTALLER_PROFILE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "SaruMon"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" '"$INSTDIR\SaruMon.exe",0'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0
  CreateDirectory "$SMPROGRAMS\${INSTALLER_PROFILE}"
  CreateShortcut "$SMPROGRAMS\${INSTALLER_PROFILE}\SaruMon.lnk" "$INSTDIR\SaruMon.exe"
  CreateShortcut "$SMPROGRAMS\${INSTALLER_PROFILE}\アンインストール.lnk" "$INSTDIR\Uninstall.exe"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "インストールの登録に失敗しました。保存先とユーザー設定への書き込み権限を確認してください。" /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
SectionEnd

Section "Windows ログイン時に起動" SEC_STARTUP
  ClearErrors
  WriteRegStr HKCU "${RUN_KEY}" "${INSTALLER_PROFILE}" '"$INSTDIR\SaruMon.exe" --tray'
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "Windows ログイン時の起動を登録できませんでした。ユーザー設定への書き込み権限を確認してください。" /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
SectionEnd

Section /o "デスクトップショートカット" SEC_DESKTOP
  CreateShortcut "$DESKTOP\${INSTALLER_PROFILE}.lnk" "$INSTDIR\SaruMon.exe"
  WriteINIStr "$INSTDIR\SaruMon.install.ini" "Install" "DesktopShortcut" "1"
SectionEnd

Section "-Apply startup opt-out"
  ; Deselected visible sections do not run. Apply an explicit opt-out here,
  ; without removing the startup registration of a different installed/portable copy.
  SectionGetFlags ${SEC_STARTUP} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ${If} $0 == 0
    ReadRegStr $0 HKCU "${RUN_KEY}" "${INSTALLER_PROFILE}"
    StrCmpS $0 '"$INSTDIR\SaruMon.exe" --tray' 0 startup_option_done
    ClearErrors
    DeleteRegValue HKCU "${RUN_KEY}" "${INSTALLER_PROFILE}"
    ${If} ${Errors}
      MessageBox MB_OK|MB_ICONSTOP "Windows ログイン時の起動を解除できませんでした。ユーザー設定への書き込み権限を確認してください。" /SD IDOK
      SetErrorLevel 3
      Abort
    ${EndIf}
  ${EndIf}
startup_option_done:
  ClearErrors
SectionEnd

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "SaruMon には 64 ビットの Windows が必要です。" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetShellVarContext current
  SetRegView 64
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/DesktopShortcut" $1
  ${IfNot} ${Errors}
    SectionSetFlags ${SEC_DESKTOP} ${SF_SELECTED}
  ${EndIf}
  ClearErrors
  ${GetOptions} $0 "/NoStartup" $1
  ${IfNot} ${Errors}
    SectionSetFlags ${SEC_STARTUP} 0
  ${EndIf}
!if "${INSTALLER_PROFILE}" != "SaruMon"
  ; Interactive inspection must not schedule a test copy for the next login.
  ; Silent integration tests retain the production default to exercise registration.
  IfSilent test_profile_startup_done
  SectionSetFlags ${SEC_STARTUP} 0
test_profile_startup_done:
!endif
FunctionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_APP} "通信モニターと .NET Runtime をインストールします。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_STARTUP} "次回の Windows ログインから通知領域で SaruMon を起動します。インストール後もアプリから変更できます。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "現在のユーザーのデスクトップにショートカットを作成します。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ${GetRoot} "$INSTDIR" $0
  ${If} $INSTDIR == $0
  ${OrIf} $INSTDIR == "$0\"
    Goto invalid_install
  ${EndIf}
  ReadINIStr $0 "$INSTDIR\SaruMon.install.ini" "Install" "Product"
  ${If} $0 != "${PRODUCT_ID}"
    Goto invalid_install
  ${EndIf}
  IfFileExists "$INSTDIR\SaruMon.exe" 0 done_check
  ClearErrors
  FileOpen $0 "$INSTDIR\SaruMon.exe" a
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "SaruMon を終了してから、アンインストールを再実行してください。$\r$\n通知領域のアイコンを右クリック →「終了」を選びます。ウィンドウの × では終了しません。$\r$\n終了済みの場合は、このフォルダーの書き込み権限を確認してください。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  FileClose $0
  Goto done_check
invalid_install:
  MessageBox MB_OK|MB_ICONSTOP "SaruMon のインストール先を確認できません。削除を中止します。" /SD IDOK
  SetErrorLevel 2
  Abort
done_check:
FunctionEnd

Section "Uninstall"
  ; Exact known-file removal only; user files and AppData settings survive.
  ClearErrors
  IfFileExists "$INSTDIR\SaruMon.exe" 0 +2
  Delete "$INSTDIR\SaruMon.exe"
  IfFileExists "$INSTDIR\README.md" 0 +2
  Delete "$INSTDIR\README.md"
  IfFileExists "$INSTDIR\docs\development.md" 0 +2
  Delete "$INSTDIR\docs\development.md"
  IfFileExists "$INSTDIR\docs\releasing.md" 0 +2
  Delete "$INSTDIR\docs\releasing.md"
  IfFileExists "$INSTDIR\docs\verification.md" 0 +2
  Delete "$INSTDIR\docs\verification.md"
  IfFileExists "$INSTDIR\docs\third-party-notices.md" 0 +2
  Delete "$INSTDIR\docs\third-party-notices.md"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "ファイルを削除できませんでした。SaruMon を終了してから再実行してください。" /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  ReadRegStr $0 HKCU "${RUN_KEY}" "${INSTALLER_PROFILE}"
  StrCmpS $0 '"$INSTDIR\SaruMon.exe" --tray' 0 run_value_done
  DeleteRegValue HKCU "${RUN_KEY}" "${INSTALLER_PROFILE}"
run_value_done:
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 == $INSTDIR
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
    Delete "$SMPROGRAMS\${INSTALLER_PROFILE}\SaruMon.lnk"
    Delete "$SMPROGRAMS\${INSTALLER_PROFILE}\アンインストール.lnk"
    RMDir "$SMPROGRAMS\${INSTALLER_PROFILE}"
    ReadINIStr $1 "$INSTDIR\SaruMon.install.ini" "Install" "DesktopShortcut"
    ${If} $1 == "1"
      Delete "$DESKTOP\${INSTALLER_PROFILE}.lnk"
    ${EndIf}
  ${EndIf}
  ReadRegStr $0 HKCU "${APP_KEY}" "InstallDir"
  ${If} $0 == $INSTDIR
    DeleteRegValue HKCU "${APP_KEY}" "InstallDir"
    DeleteRegKey /ifempty HKCU "${APP_KEY}"
  ${EndIf}
  Delete "$INSTDIR\SaruMon.install.ini"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR\docs"
  RMDir "$INSTDIR"
SectionEnd
