; ============================================================================
;  风灵月影宗储物袋 · 安装包（NSIS 3 / Unicode）
;
;  界面：QQ 风格的中文向导 —— 欢迎 → 免责声明 → 安装选项 → 安装路径
;        → 安装进度 → 完成（可直接运行）
;
;  本文件不单独编译：由 publish.ps1 复制到临时目录，并在旁边生成
;  build-config.nsh（版本号、待打包的 exe、图标、图、输出路径都在里面）。
; ============================================================================

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "build-config.nsh"

!define APPNAME   "风灵月影宗储物袋"
!define PUBLISHER "风灵月影宗"
!define APPEXE    "风灵月影宗储物袋.exe"
!define UNKEY     "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"

Name "${APPNAME}"
OutFile "${OUTFILE}"

; 装到当前用户目录：程序会在自己所在目录建 dbs\ 和 f\（缓存 + 下载的修改器），
; 放 Program Files 普通用户没有写权限，所以走「每用户安装」，也不需要管理员权限
InstallDir "$LOCALAPPDATA\Programs\${APPNAME}"
InstallDirRegKey HKCU "Software\${APPNAME}" "InstallDir"
RequestExecutionLevel user

BrandingText "${APPNAME} 安装程序 v${APPVERSION}"

VIProductVersion "${APPVERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName"      "${APPNAME}"
VIAddVersionKey /LANG=2052 "CompanyName"      "${PUBLISHER}"
VIAddVersionKey /LANG=2052 "FileDescription"  "${APPNAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion"      "${APPVERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion"   "${APPVERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright"   "${PUBLISHER}"

; ---------------------------------------------------------------- 向导外观

!define MUI_ABORTWARNING
!define MUI_ICON   "${ICONFILE}"
!define MUI_UNICON "${ICONFILE}"

!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "${HEADERBMP}"

!define MUI_WELCOMEFINISHPAGE_BITMAP    "${WELCOMEBMP}"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP  "${WELCOMEBMP}"

!define MUI_WELCOMEPAGE_TITLE "欢迎安装 ${APPNAME}"
!define MUI_WELCOMEPAGE_TEXT "这是一个 Steam 单机游戏修改器的本地库：游戏数据、封面、修改器都放在本机，打开就能用。$\r$\n$\r$\n点「下一步」开始安装。"
!define MUI_WELCOMEPAGE_TITLE_3LINES

!define MUI_LICENSEPAGE_TEXT_TOP "安装前请先读一下下面的免责声明。"
!define MUI_LICENSEPAGE_TEXT_BOTTOM "同意后点「我接受」继续。"
!define MUI_LICENSEPAGE_CHECKBOX
!define MUI_LICENSEPAGE_CHECKBOX_TEXT "我已阅读并同意上述声明"

!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_DIRECTORYPAGE_TEXT_TOP "选择安装位置。安装目录需要可写权限（本程序会在里面建 dbs、f 两个数据文件夹）。"
!define MUI_DIRECTORYPAGE_VERIFYONLEAVE

!define MUI_INSTFILESPAGE_COLORS "101820 FFFFFF"
!define MUI_INSTFILESPAGE_PROGRESSBAR colored

!define MUI_FINISHPAGE_TITLE "安装完成"
!define MUI_FINISHPAGE_TEXT "${APPNAME} v${APPVERSION} 已经装好。$\r$\n$\r$\n关闭窗口时程序会缩到右下角托盘继续运行，右键托盘图标可以随时退出。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APPEXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行 ${APPNAME}"
!define MUI_FINISHPAGE_LINK "访问官网 ${WEBSITE}"
!define MUI_FINISHPAGE_LINK_LOCATION "${WEBSITE}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${LICENSEFILE}"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

; ---------------------------------------------------------------- 安装前检查

; 程序还开着的话，exe 被占用会覆盖失败（它关窗口只是缩到托盘，进程还在）。
; 用程序自己的单实例互斥体判断，"这个"进程才准；检测到就请用户确认后结束它。
Function .onInit
    SetShellVarContext current

    System::Call 'kernel32::OpenMutex(i 0x00100000, i 0, w "Local\FlingTrainerX.FlingTrainer.Storebag") i .r1'
    IntCmp $1 0 done

    System::Call 'kernel32::CloseHandle(i r1)'

    MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION \
        "检测到 ${APPNAME} 正在运行（可能缩在右下角托盘里）。$\r$\n$\r$\n继续安装需要先关掉它，点「确定」会自动关闭。" \
        IDOK closeIt

    Abort

    closeIt:
    nsExec::ExecToStack 'taskkill /F /IM "${APPEXE}"'
    Pop $0
    Pop $0
    Sleep 1200

    done:
FunctionEnd

; ---------------------------------------------------------------- 安装

Section "主程序（必装）" SecMain
    SectionIn RO
    SetShellVarContext current

    SetOutPath "$INSTDIR"
    File "/oname=${APPEXE}" "${SOURCEDIR}\${SRCEXE}"

    SetOutPath "$INSTDIR\Assets"
    File "${SOURCEDIR}\Assets\免责声明.txt"

    SetOutPath "$INSTDIR"

    CreateDirectory "$SMPROGRAMS\${APPNAME}"
    CreateShortCut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${APPEXE}"
    CreateShortCut "$SMPROGRAMS\${APPNAME}\卸载 ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"

    WriteUninstaller "$INSTDIR\Uninstall.exe"

    WriteRegStr HKCU "Software\${APPNAME}" "InstallDir" "$INSTDIR"
    WriteRegStr HKCU "Software\${APPNAME}" "Version" "${APPVERSION}"

    WriteRegStr   HKCU "${UNKEY}" "DisplayName"     "${APPNAME}"
    WriteRegStr   HKCU "${UNKEY}" "DisplayVersion"  "${APPVERSION}"
    WriteRegStr   HKCU "${UNKEY}" "Publisher"       "${PUBLISHER}"
    WriteRegStr   HKCU "${UNKEY}" "DisplayIcon"     "$INSTDIR\${APPEXE}"
    WriteRegStr   HKCU "${UNKEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr   HKCU "${UNKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr   HKCU "${UNKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
    WriteRegDWORD HKCU "${UNKEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNKEY}" "NoRepair" 1

    ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
    IntFmt $0 "0x%08X" $0
    WriteRegDWORD HKCU "${UNKEY}" "EstimatedSize" "$0"
SectionEnd

Section "创建桌面快捷方式" SecDesktop
    SetShellVarContext current
    CreateShortCut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPEXE}"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
    !insertmacro MUI_DESCRIPTION_TEXT ${SecMain}    "程序本体 + 免责声明，必装。"
    !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "在桌面上放一个启动图标，不需要可以取消勾选。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------------------------------------------------------------- 卸载

Section "Uninstall"
    SetShellVarContext current

    Delete "$DESKTOP\${APPNAME}.lnk"
    Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
    Delete "$SMPROGRAMS\${APPNAME}\卸载 ${APPNAME}.lnk"
    RMDir  "$SMPROGRAMS\${APPNAME}"

    Delete "$INSTDIR\${APPEXE}"
    Delete "$INSTDIR\Assets\免责声明.txt"
    RMDir  "$INSTDIR\Assets"
    Delete "$INSTDIR\Uninstall.exe"

    DeleteRegKey HKCU "${UNKEY}"
    DeleteRegKey HKCU "Software\${APPNAME}"

    MessageBox MB_YESNO|MB_ICONQUESTION \
        "是否同时删除已经下载的修改器和本地数据？$\r$\n$\r$\n$INSTDIR\f（修改器）$\r$\n$INSTDIR\dbs（游戏列表）$\r$\n$\r$\n选「否」会保留，以后重装还能接着用。" \
        IDNO KeepData

    RMDir /r "$INSTDIR\f"
    RMDir /r "$INSTDIR\dbs"

    KeepData:
    RMDir "$INSTDIR"
SectionEnd
