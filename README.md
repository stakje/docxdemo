# DocVista

DocVista 是轻量、只读的 Windows 文档查看器，首版支持 PDF、Word、PowerPoint、Excel 和 CSV。

Setup 会同时安装 `DocVista.exe` 和独立的 `DocVista.Updater.exe`。主程序的“检查更新”会启动更新程序，由更新程序下载完整或差分包、显示进度并完成重启。

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
.\scripts\package.ps1 -Version 0.1.2 -UpdateFeedUrl https://updates.example.com/docvista/win
```

正式发布时通过 `-SignParams` 传入 `signtool` 参数。稳定通道使用 `win`，测试通道使用 `beta`。生成物位于 `artifacts\releases`。

向 GitHub 推送 `v*` 标签会自动创建 Release，并把更新源写为 `https://github.com/stakje/docxdemo/releases/latest/download`。独立更新程序从该地址读取 `releases.win.json` 并下载完整包或差分包。

## 格式说明

- PDF 由 WebView2 Runtime 查看。
- CSV 和 XLSX 无需安装 Office。
- DOC、DOCX、PPT、PPTX 和 XLS 使用系统 Preview Handler，需要 Office 或兼容组件。
- 应用不修改源文件。
