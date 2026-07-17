# DocVista

DocVista 是轻量、只读的 Windows 文档查看器，首版支持 PDF、Word、PowerPoint、Excel 和 CSV。

Setup 安装 `DocVista.exe` 及正式卸载组件。应用不包含在线检查更新功能，后续版本通过新版 Setup 覆盖安装。

## 开发

需要 Windows 10/11、.NET 8 SDK 和 WebView2 Runtime。

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\dev.ps1
.\scripts\dev.ps1 -Document .\sample.pdf
```

## 打包

```powershell
.\scripts\package.ps1 -Version 0.1.5
```

本地打包需要 Inno Setup 6。正式发布时通过 `-SignParams` 传入 `signtool` 参数。稳定通道使用 `win`，测试通道使用 `beta`。本地交付目录为 `artifacts\installer`，其中只保留 Setup EXE。

Setup 使用经典 Windows 分步安装向导，包含欢迎、安装说明、桌面快捷方式选项、准备安装、进度和完成页面。应用固定安装到当前用户的本地应用目录；开始菜单快捷方式固定创建。应用侧栏可启动官方卸载程序，卸载应用和快捷方式，不会改动用户文档。

向 GitHub 推送 `v*` 标签会自动创建 Release，并且只发布 `DocVista-win-Setup.exe`。

## 格式说明

- PDF 由 WebView2 Runtime 查看。
- CSV、XLS 和 XLSX 可使用内置数据视图，无需安装 Office。
- DOC、DOCX、PPT 和 PPTX 优先使用系统 Preview Handler；系统没有可用组件时切换到内置兼容视图。
- 系统预览可保留原始 Office 排版；内置兼容视图侧重稳定读取内容，不承诺复杂图表、宏、艺术字和旧版二进制格式的 1:1 排版。
- 应用不修改源文件。
