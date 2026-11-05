; ============================================================================
;  风灵月影宗储物袋 · 安装包（NSIS 3 / Unicode）
;
;  界面：100% 自绘的 QQ 风格窗口 —— 蓝色渐变顶栏、整窗位图、圆角图片按钮、
;        自绘进度条、无标准向导页、无免责声明页。
;        窗口由 nsDialogs 创建，NSIS 自带的 MUI2/欢迎页/许可页一个都不用。
;
;  页面：① 安装位置/选项  →  ② 安装进度（自动执行）→ 完成
;
;  本文件不单独编译：由 publish.ps1 复制到临时目录，并在旁边生成
;  build-config.nsh（版本号、待打包目录、图标、界面位图、输出路径都在里面）。
; ============================================================================

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!include "nsDialogs.nsh"
!include "WinMessages.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "build-config.nsh"

!define APPNAME   "风灵月影宗储物袋"
!define PUBLISHER "风灵月影宗"
!define APPEXE    "风灵月影宗储物袋.exe"
!define UNKEY     "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"

; 窗口几何（像素）；界面位图也是这个尺寸
!define WIN_W 680
!define WIN_H 460

Name "${APPNAME}"
OutFile "${OUTFILE}"
Caption " "
BrandingText " "
Icon "${ICONFILE}"
UninstallIcon "${ICONFILE}"

; 程序会在安装目录写 dbs\ 和 f\，放 Program Files 普通用户没权限，因此每用户安装
InstallDir "$LOCALAPPDATA\Programs\${APPNAME}"
InstallDirRegKey HKCU "Software\${APPNAME}" "InstallDir"
RequestExecutionLevel user
AutoCloseWindow true
ShowInstDetails nevershow
ShowUninstDetails nevershow

LoadLanguageFile "${NSISDIR}\Contrib\Language files\SimpChinese.nlf"

VIProductVersion "${APPVERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName"      "${APPNAME}"
VIAddVersionKey /LANG=2052 "CompanyName"      "${PUBLISHER}"
VIAddVersionKey /LANG=2052 "FileDescription"  "${APPNAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion"      "${APPVERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion"   "${APPVERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright"   "${PUBLISHER}"

; ---------------------------------------------------------------- 页面

Page custom PageOptionsCreate PageOptionsLeave
Page custom PageProgressCreate PageProgressLeave
UninstPage uninstConfirm
UninstPage instfiles

; ---------------------------------------------------------------- 变量

Var Dialog
Var Bg
Var BgImage
Var BtnNext
Var BtnMain
Var MainImage
Var BtnClose
Var CloseImage
Var BtnBrowse
Var BrowseImage
Var PathEdit
Var ChkDesktop
Var ChkRun
Var ProgressBar
Var WantDesktop
Var WantRun
Var PathFont

; ---------------------------------------------------------------- 初始化

Function .onInit
    SetShellVarContext current
    InitPluginsDir

    ; 界面位图在编译期嵌进安装包，运行时释放到 $PLUGINSDIR
    File /oname=$PLUGINSDIR\page-options.bmp  "${ARTDIR}\page-options.bmp"
    File /oname=$PLUGINSDIR\page-progress.bmp "${ARTDIR}\page-progress.bmp"
    File /oname=$PLUGINSDIR\page-finish.bmp   "${ARTDIR}\page-finish.bmp"
    File /oname=$PLUGINSDIR\btn-install.bmp   "${ARTDIR}\btn-install.bmp"
    File /oname=$PLUGINSDIR\btn-wait.bmp      "${ARTDIR}\btn-wait.bmp"
    File /oname=$PLUGINSDIR\btn-finish.bmp    "${ARTDIR}\btn-finish.bmp"
    File /oname=$PLUGINSDIR\btn-browse.bmp    "${ARTDIR}\btn-browse.bmp"
    File /oname=$PLUGINSDIR\btn-close.bmp     "${ARTDIR}\btn-close.bmp"

    ; 程序还开着时 exe 被占用会覆盖失败，用自己的单实例互斥体检测
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

; ---------------------------------------------------------------- 窗口外壳

!macro HIDE_STD ID
    GetDlgItem $R0 $HWNDPARENT ${ID}
    ShowWindow $R0 ${SW_HIDE}
!macroend

; 隐藏 NSIS 自带的所有标准控件（标题、页眉、Branding、上一步、取消）
Function HideStdControls
    LockWindow on
    !insertmacro HIDE_STD 1034
    !insertmacro HIDE_STD 1035
    !insertmacro HIDE_STD 1036
    !insertmacro HIDE_STD 1037
    !insertmacro HIDE_STD 1038
    !insertmacro HIDE_STD 1039
    !insertmacro HIDE_STD 1256
    !insertmacro HIDE_STD 1028
    !insertmacro HIDE_STD 3
    !insertmacro HIDE_STD 2
    LockWindow off
FunctionEnd

; 外壳只做一次：去标题栏、固定尺寸、把内层对话框占位铺满整窗
; 注意：NSIS 会在 .onGUIInit 之后用 0x7FFF 哨兵值重设窗口位置，
;       所以「居中」不能在这里做，要放到每个页面显示时（CenterAndSkin）。
Function .onGUIInit
    Call HideStdControls

    ; 去掉 WS_CAPTION(0x00C00000) 与 WS_SYSMENU(0x00080000)，自己做关闭按钮
    System::Call 'user32::GetWindowLong(i $HWNDPARENT, i -16) i.s'
    Pop $R0
    IntOp $R1 $R0 & 0xFF37FFFF
    System::Call 'user32::SetWindowLong(i $HWNDPARENT, i -16, i $R1)'

    ; 只改尺寸，不动位置（SWP_NOMOVE）
    System::Call 'user32::SetWindowPos(i $HWNDPARENT, i 0, i 0, i 0, i ${WIN_W}, i ${WIN_H}, i 0x16)'

    ; 内层对话框占位铺满，后面的百分比坐标才跟位图对齐
    GetDlgItem $R0 $HWNDPARENT 1018
    System::Call 'user32::SetWindowPos(i $R0, i 0, i 0, i 0, i ${WIN_W}, i ${WIN_H}, i 0x14)'
FunctionEnd

; 每个页面显示时重新居中 + 隐藏 NSIS 又显示出来的标准控件
Function CenterAndSkin
    Call HideStdControls
    System::Call 'user32::GetSystemMetrics(i 0) i.s'
    Pop $R0
    System::Call 'user32::GetSystemMetrics(i 1) i.s'
    Pop $R1
    IntOp $R2 $R0 - ${WIN_W}
    IntOp $R2 $R2 / 2
    IntOp $R3 $R1 - ${WIN_H}
    IntOp $R3 $R3 / 2
    System::Call 'user32::SetWindowPos(i $HWNDPARENT, i 0, i $R2, i $R3, i ${WIN_W}, i ${WIN_H}, i 0x34)'
FunctionEnd

; 自带「下一步」按钮只用来触发翻页，挪到窗口外；界面上显示的是自绘图片按钮
Function SkinNextButton
    GetDlgItem $BtnNext $HWNDPARENT 1
    EnableWindow $BtnNext 1
    System::Call 'user32::SetWindowPos(i $BtnNext, i 0, i -300, i -300, i 10, i 10, i 0x10)'
FunctionEnd

; $R9 = 位图文件名；换整窗背景
Function SetBackground
    StrCpy $0 "$PLUGINSDIR\$R9"
    System::Call 'user32::LoadImage(p 0, t r0, i 0, i 0, i 0, i 0x10) p.s'
    Pop $1
    ${If} $BgImage P<> 0
        System::Call 'gdi32::DeleteObject(p $BgImage)'
    ${EndIf}
    StrCpy $BgImage $1
    SendMessage $Bg ${STM_SETIMAGE} 0 $BgImage
FunctionEnd

; $R9 = 位图文件名；换右下角主按钮
Function SetMainImage
    StrCpy $0 "$PLUGINSDIR\$R9"
    System::Call 'user32::LoadImage(p 0, t r0, i 0, i 0, i 0, i 0x10) p.s'
    Pop $1
    ${If} $MainImage P<> 0
        System::Call 'gdi32::DeleteObject(p $MainImage)'
    ${EndIf}
    StrCpy $MainImage $1
    SendMessage $BtnMain ${BM_SETIMAGE} 0 $MainImage
FunctionEnd

; 创建右下角自绘主按钮（$R9 = 初始位图文件名）
Function CreateMainButton
    ${NSD_CreateButton} 76% 88% 18% 8% ""
    Pop $BtnMain
    ${NSD_AddStyle} $BtnMain ${BS_BITMAP}
    Call SetMainImage
    ${NSD_OnClick} $BtnMain OnNext
FunctionEnd

; 点击自绘主按钮 → 模拟点了一下 NSIS 自带的下一步，页面才会往下走
Function OnNext
    SendMessage $BtnNext 0x00F5 0 0
FunctionEnd

; 立刻重绘整窗（安装过程占用主线程时靠它刷新进度）
Function ForceRedraw
    System::Call 'user32::RedrawWindow(i $HWNDPARENT, i 0, i 0, i 0x181)'
FunctionEnd

Function FixZOrder
    System::Call 'user32::SetWindowPos(i $Bg, i 1, i 0, i 0, i 0, i 0, i 0x13)'
FunctionEnd

; 右上角自绘关闭按钮（位图从顶栏裁出来，颜色无缝）
Function CreateCloseButton
    ${NSD_CreateButton} 95% 2% 4% 5% ""
    Pop $BtnClose
    ${NSD_AddStyle} $BtnClose ${BS_BITMAP}
    StrCpy $0 "$PLUGINSDIR\btn-close.bmp"
    System::Call 'user32::LoadImage(p 0, t r0, i 0, i 0, i 0, i 0x10) p.s'
    Pop $CloseImage
    SendMessage $BtnClose ${BM_SETIMAGE} 0 $CloseImage
    ${NSD_OnClick} $BtnClose OnClose
FunctionEnd

Function OnClose
    SendMessage $HWNDPARENT ${WM_CLOSE} 0 0
FunctionEnd

Function OnBrowse
    nsDialogs::SelectFolderDialog "选择安装位置" "$INSTDIR"
    Pop $0
    ${If} $0 != "error"
        ${NSD_SetText} $PathEdit $0
    ${EndIf}
FunctionEnd

; ---------------------------------------------------------------- 页面 ① 安装位置 / 选项

Function PageOptionsCreate
    nsDialogs::Create 1018
    Pop $Dialog
    Call CenterAndSkin
    Call SkinNextButton

    ; 整窗背景（先建，再由 FixZOrder 压到最底层）
    ${NSD_CreateBitmap} 0 0 100% 100% ""
    Pop $Bg
    ${NSD_AddStyle} $Bg ${SS_REALSIZECONTROL}
    StrCpy $R9 "page-options.bmp"
    Call SetBackground
    SetCtlColors $Dialog 0 0xffffff

    ; 安装路径
    ${NSD_CreateText} 6% 52% 76% 8% "$INSTDIR"
    Pop $PathEdit
    CreateFont $PathFont "Microsoft YaHei" 10 400
    SendMessage $PathEdit ${WM_SETFONT} $PathFont 1
    SetCtlColors $PathEdit 0x3D2D1F 0xffffff

    ; 「浏览」图片按钮
    ${NSD_CreateButton} 84% 52% 10% 8% ""
    Pop $BtnBrowse
    ${NSD_AddStyle} $BtnBrowse ${BS_BITMAP}
    StrCpy $0 "$PLUGINSDIR\btn-browse.bmp"
    System::Call 'user32::LoadImage(p 0, t r0, i 0, i 0, i 0, i 0x10) p.s'
    Pop $BrowseImage
    SendMessage $BtnBrowse ${BM_SETIMAGE} 0 $BrowseImage
    ${NSD_OnClick} $BtnBrowse OnBrowse

    ; 桌面快捷方式（说明文字画在位图里，控件只要一个小方框）
    ${NSD_CreateCheckBox} 6% 72% 3% 4% ""
    Pop $ChkDesktop
    SetCtlColors $ChkDesktop 0xffffff transparent
    ${NSD_Check} $ChkDesktop

    StrCpy $R9 "btn-install.bmp"
    Call CreateMainButton
    Call CreateCloseButton
    Call FixZOrder
    nsDialogs::Show
FunctionEnd

Function PageOptionsLeave
    ${NSD_GetText} $PathEdit $0
    StrCpy $INSTDIR $0
    ${If} $INSTDIR == ""
        MessageBox MB_OK|MB_ICONEXCLAMATION "请先选择安装位置。"
        Abort
    ${EndIf}
    ${NSD_GetState} $ChkDesktop $WantDesktop
FunctionEnd

; ---------------------------------------------------------------- 页面 ② 进度 → 完成

Function PageProgressCreate
    nsDialogs::Create 1018
    Pop $Dialog
    Call CenterAndSkin
    Call SkinNextButton

    ${NSD_CreateBitmap} 0 0 100% 100% ""
    Pop $Bg
    ${NSD_AddStyle} $Bg ${SS_REALSIZECONTROL}
    StrCpy $R9 "page-progress.bmp"
    Call SetBackground
    SetCtlColors $Dialog 0 0xffffff

    ; 自绘进度条：去 3D 边框、去主题，才能用 QQ 蓝
    ${NSD_CreateProgressBar} 6% 65% 88% 3% ""
    Pop $ProgressBar
    System::Call 'user32::SetWindowLong(i $ProgressBar, i -20, i 0)'
    System::Call 'uxtheme::SetWindowTheme(i $ProgressBar, w "", w "")'
    SendMessage $ProgressBar ${PBM_SETRANGE32} 0 100
    SendMessage $ProgressBar ${PBM_SETBARCOLOR} 0 0xF5B712
    SendMessage $ProgressBar ${PBM_SETBKCOLOR} 0 0xF9EFE4

    ; 完成页才出现的「立即运行」勾选框
    ${NSD_CreateCheckBox} 6% 70% 3% 4% ""
    Pop $ChkRun
    SetCtlColors $ChkRun 0xffffff transparent
    ShowWindow $ChkRun 0

    Call CreateCloseButton

    ; 安装期间主按钮置灰；定时器在窗口画出来之后开始干活
    StrCpy $R9 "btn-wait.bmp"
    Call CreateMainButton
    EnableWindow $BtnMain 0
    Call FixZOrder
    ${NSD_CreateTimer} DoInstall 350

    nsDialogs::Show
FunctionEnd

Function PageProgressLeave
    ${NSD_GetState} $ChkRun $WantRun
    ${If} $WantRun = 1
        Exec '"$INSTDIR\${APPEXE}"'
    ${EndIf}
FunctionEnd

; ---------------------------------------------------------------- 实际安装动作

Function DoInstall
    ${NSD_KillTimer} DoInstall
    SetShellVarContext current

    SendMessage $ProgressBar ${PBM_SETPOS} 10 0
    Call ForceRedraw
    Sleep 250

    SetOutPath "$INSTDIR"
    ; 把 .pro\<版本号>\ 里的 .NET 构建产物整个打进来。
    ; 排除历史安装包、旧命名的 exe、调试符号、以及程序运行时自己建的数据目录，
    ; 这样同一版本号重复发布也不会把上一次的几百 MB 成品再打一遍。
    File /r /x "*-安装包-*.exe" /x "${APPNAME}-*.exe" /x "*-绿色版-*.exe" /x "*.pdb" /x "*.log" /x "dbs" /x "dbs\*" /x "f" /x "f\*" "${SOURCEDIR}\*.*"
    SendMessage $ProgressBar ${PBM_SETPOS} 65 0
    Call ForceRedraw

    CreateDirectory "$SMPROGRAMS\${APPNAME}"
    CreateShortCut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${APPEXE}"
    CreateShortCut "$SMPROGRAMS\${APPNAME}\卸载 ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"
    ${If} $WantDesktop = 1
        CreateShortCut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPEXE}"
    ${EndIf}
    SendMessage $ProgressBar ${PBM_SETPOS} 82 0

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

    SendMessage $ProgressBar ${PBM_SETPOS} 100 0
    Call ForceRedraw
    Sleep 350

    ; 换成完成页
    ShowWindow $ProgressBar 0
    StrCpy $R9 "page-finish.bmp"
    Call SetBackground
    ShowWindow $ChkRun 5
    ${NSD_Check} $ChkRun
    StrCpy $R9 "btn-finish.bmp"
    Call SetMainImage
    EnableWindow $BtnMain 1
    Call ForceRedraw
FunctionEnd

; ---------------------------------------------------------------- 空安装节（动作都在 DoInstall 里）

Section "-Install" SEC_MAIN
SectionEnd

; ---------------------------------------------------------------- 卸载

Section "Uninstall"
    SetShellVarContext current

    Delete "$DESKTOP\${APPNAME}.lnk"
    Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
    Delete "$SMPROGRAMS\${APPNAME}\卸载 ${APPNAME}.lnk"
    RMDir "$SMPROGRAMS\${APPNAME}"

    DeleteRegKey HKCU "${UNKEY}"
    DeleteRegKey HKCU "Software\${APPNAME}"

    MessageBox MB_YESNO|MB_ICONQUESTION \
        "是否同时删除已经下载的修改器和本地数据？$\r$\n$\r$\n选「否」会保留，以后重装还能接着用。" \
        IDNO KeepData

    RMDir /r "$INSTDIR\f"
    RMDir /r "$INSTDIR\dbs"

    KeepData:
    ; 只删安装目录下的程序文件，数据子目录按上面的选择保留
    Delete "$INSTDIR\*.*"
    RMDir /r "$INSTDIR\Assets"
    RMDir "$INSTDIR"
SectionEnd
