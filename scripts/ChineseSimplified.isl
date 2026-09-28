; ══════════════════════════════════════════════════════════════
;  简体中文语言覆盖层（自制）
;
;  用法：MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"
;        ⇒ 以官方 Default.isl 为【基底】提供全部必需消息，
;          本文件只【覆盖】安装向导里用户会看到的那些。
;  ⚠️ Inno Setup 未随包提供简体中文（属非官方翻译），
;     而 GitHub raw / jsdelivr / ghproxy 等在本机均不可达 ⇒ 自建。
;  ⚠️ 分两段：向导消息写 [Messages]；{cm:...} 引用的写 [CustomMessages]。
;     写错段落会被「not recognized」警告忽略（图标/运行两项曾因此仍显示英文）。
;  ⚠️ 各页【标题】是独立的 Wizard* 消息（WizardReady 等），
;     不覆盖它们会让页面标题残留英文。
; ══════════════════════════════════════════════════════════════

[LangOptions]
LanguageName=简体中文
LanguageID=$0804
LanguageCodePage=936
DialogFontName=Microsoft YaHei UI
DialogFontSize=9
WelcomeFontName=Microsoft YaHei UI
WelcomeFontSize=12

[Messages]
; ── 窗口与按钮 ──
SetupAppTitle=安装
SetupWindowTitle=安装 - %1
UninstallAppTitle=卸载
UninstallAppFullTitle=卸载 %1
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonCancel=取消
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&R)...
ButtonYes=是(&Y)
ButtonNo=否(&N)
ButtonOK=确定
InformationTitle=信息
ConfirmTitle=确认
ErrorTitle=错误

; ── 向导各页【标题】──
WizardLicense=许可协议
WizardInfoBefore=信息
WizardInfoAfter=信息
WizardUserInfo=用户信息
WizardSelectDir=选择安装位置
WizardSelectComponents=选择组件
WizardSelectTasks=选择附加任务
WizardSelectProgramGroup=选择开始菜单文件夹
WizardReady=准备安装
WizardPreparing=正在准备安装
WizardInstalling=正在安装
WizardUninstalling=卸载状态

; ── 欢迎页 ──
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=即将在你的电脑上安装 [name/ver]。%n%n继续前建议关闭其他所有程序，以免安装过程中出现文件冲突。%n%n点击「下一步」继续。

; ── 许可协议页 ──
LicenseLabel=请阅读以下许可协议。安装前你需要接受本协议的全部条款。
LicenseLabel3=请阅读以下许可协议。安装前你需要接受本协议的全部条款。
LicenseAccepted=我接受本协议(&A)
LicenseNotAccepted=我不接受本协议(&D)

; ── 选择安装位置页 ──
SelectDirDesc=本程序应安装到哪里？
SelectDirLabel3=安装程序会把 [name] 安装到下面的文件夹。
SelectDirBrowseLabel=点击「下一步」继续。如果你想更改安装位置，点击「浏览」。
DiskSpaceMBLabel=至少需要 [mb] MB 的可用磁盘空间。
DiskSpaceGBLabel=至少需要 [gb] GB 的可用磁盘空间。

; ── 选择附加任务页 ──
SelectTasksDesc=你还想执行哪些附加任务？
SelectTasksLabel2=请选择安装 [name] 时要一并执行的附加任务，然后点击「下一步」。

; ── 选择组件 / 开始菜单文件夹 ──
SelectComponentsDesc=应该安装哪些组件？
SelectComponentsLabel2=请选择要安装的组件；清除不想安装的组件前的勾选，然后点击「下一步」。
SelectProgramGroupDesc=安装程序应把快捷方式放在哪里？
SelectProgramGroupLabel=安装程序会在下面的开始菜单文件夹里创建快捷方式。若要使用其他文件夹，点击「浏览」。

; ── 准备安装页 ──
ReadyLabel1=安装程序已准备好，可以开始安装 [name] 到你的电脑。
ReadyLabel2a=点击「安装」开始安装；若想检查或更改设置，点击「上一步」。
ReadyLabel2b=点击「安装」开始安装。
ReadyMemoDir=安装位置：
ReadyMemoTasks=附加任务：
ReadyMemoGroup=开始菜单文件夹：

; ── 安装中 ──
PreparingDesc=安装程序正在准备安装，请稍候。
InstallingLabel=正在安装 [name]，请稍候…

; ── 完成页 ──
FinishedLabel=安装程序已在你的电脑上安装完成 [name]。%n%n点击「完成」退出。
FinishedLabelNoIcons=安装程序已在你的电脑上安装完成 [name]。%n%n点击「完成」退出。
ClickFinish=点击「完成」退出安装程序。
FinishedRestartLabel=要完成 [name] 的安装，必须重新启动电脑。现在重新启动吗？
FinishedRestartMessage=要完成 [name] 的安装，必须重新启动电脑。%n%n现在重新启动吗？

; ── 卸载 ──
ConfirmUninstall=确定要完全卸载 %1 及其所有组件吗？
UninstallStatusLabel=正在从你的电脑上删除 %1，请稍候…
UninstalledAll=%1 已成功卸载。
UninstalledMost=%1 卸载完成。%n%n部分文件未能删除，你可以手动删除它们。

; ── 常见错误 ──
ErrorTooManyFilesInDir=该文件夹中文件太多，无法复制到目标位置。
ErrorCopying=复制文件时出错：%n%n%1%n%n点击「重试」重试，点击「跳过」忽略该文件，或点击「取消」退出安装。
ErrorCreatingDir=安装程序无法创建文件夹 "%1"。

[CustomMessages]
; ⚠️ 这些通过 {cm:...} 引用，必须放在本段，放 [Messages] 会被忽略。
AdditionalIcons=附加图标：
CreateDesktopIcon=创建桌面快捷方式(&D)
UninstallProgram=卸载 %1
LaunchProgram=运行 %1(&L)
