!ifndef PRODUCT_VERSION
  !error "PRODUCT_VERSION is required."
!endif

!ifndef PUBLISH_DIR
  !error "PUBLISH_DIR is required."
!endif

!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required."
!endif

Unicode true

!include "MUI2.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"

!define PRODUCT_NAME "EasyProxy"
!define COMPANY_NAME "EasyProxy"
!define APP_EXE "EasyProxy.exe"
!define UNINSTALL_EXE "uninstall.exe"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\EasyProxy"

Name "${PRODUCT_NAME}"
OutFile "${OUTPUT_FILE}"
InstallDir "$PROGRAMFILES64\EasyProxy"
InstallDirRegKey HKLM "Software\EasyProxy" "InstallDir"
RequestExecutionLevel admin

SetCompressor /SOLID lzma
BrandingText "${PRODUCT_NAME} ${PRODUCT_VERSION}"
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=2052 "CompanyName" "${COMPANY_NAME}"
VIAddVersionKey /LANG=2052 "FileDescription" "${PRODUCT_NAME} Installer"
VIAddVersionKey /LANG=2052 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Copyright (C) ${COMPANY_NAME}"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"

Function EnsureAdmin
  SetRegView 64
  ClearErrors
  WriteRegStr HKLM "Software\EasyProxy\ElevationTest" "CanWrite" "1"
  ${If} ${Errors}
    ${GetParameters} $0
    ClearErrors
    ExecShell "runas" "$EXEPATH" "$0"
    ${If} ${Errors}
      MessageBox MB_ICONSTOP "Administrator permission is required. Please approve the UAC prompt or run this setup as administrator."
    ${EndIf}
    Quit
  ${EndIf}
  DeleteRegKey HKLM "Software\EasyProxy\ElevationTest"
FunctionEnd

Function .onInit
  Call EnsureAdmin

  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "EasyProxy requires 64-bit Windows."
    Abort
  ${EndIf}
FunctionEnd

Function un.EnsureAdmin
  SetRegView 64
  ClearErrors
  WriteRegStr HKLM "Software\EasyProxy\ElevationTest" "CanWrite" "1"
  ${If} ${Errors}
    ${GetParameters} $0
    ClearErrors
    ExecShell "runas" "$EXEPATH" "$0"
    ${If} ${Errors}
      MessageBox MB_ICONSTOP "Administrator permission is required. Please approve the UAC prompt or run uninstall.exe as administrator."
    ${EndIf}
    Quit
  ${EndIf}
  DeleteRegKey HKLM "Software\EasyProxy\ElevationTest"
FunctionEnd

Function un.onInit
  Call un.EnsureAdmin
FunctionEnd

Section "" SecMain
  SectionIn RO

  SetShellVarContext all
  SetRegView 64

  SetOutPath "$INSTDIR"
  File "/oname=${APP_EXE}" "${PUBLISH_DIR}\${APP_EXE}"
  File "/oname=WinDivert.dll" "${PUBLISH_DIR}\WinDivert.dll"
  File "/oname=WinDivert64.sys" "${PUBLISH_DIR}\WinDivert64.sys"
  File "/oname=app-proxy.example.json" "${PUBLISH_DIR}\app-proxy.example.json"

  WriteUninstaller "$INSTDIR\${UNINSTALL_EXE}"

  CreateDirectory "$SMPROGRAMS\EasyProxy"
  CreateShortcut "$SMPROGRAMS\EasyProxy\EasyProxy.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$SMPROGRAMS\EasyProxy\Uninstall EasyProxy.lnk" "$INSTDIR\${UNINSTALL_EXE}"
  CreateShortcut "$DESKTOP\EasyProxy.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}"

  WriteRegStr HKLM "Software\EasyProxy" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "${COMPANY_NAME}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\${UNINSTALL_EXE}"'
  WriteRegStr HKLM "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\${UNINSTALL_EXE}" /S'
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext all
  SetRegView 64

  Delete "$DESKTOP\EasyProxy.lnk"
  Delete "$SMPROGRAMS\EasyProxy\EasyProxy.lnk"
  Delete "$SMPROGRAMS\EasyProxy\Uninstall EasyProxy.lnk"
  RMDir "$SMPROGRAMS\EasyProxy"

  Delete "$INSTDIR\app-proxy.example.json"
  Delete "$INSTDIR\WinDivert64.sys"
  Delete "$INSTDIR\WinDivert.dll"
  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\${UNINSTALL_EXE}"
  RMDir "$INSTDIR"

  DeleteRegKey HKLM "${UNINSTALL_KEY}"
  DeleteRegKey HKLM "Software\EasyProxy"
SectionEnd
