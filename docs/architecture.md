# DocVista 架构

DocVista 是 Windows 10/11 x64 只读文档查看器。界面使用 WPF，PDF 由 WebView2 渲染，表格格式由内置解析器渲染，Office 二进制与演示文档由 Windows Preview Handler 承载。

## 项目边界

- `DocVista.Core`：格式识别、最近文件与用户设置，不依赖 UI。
- `DocVista.Rendering`：CSV/XLSX 解析与表格模型。
- `DocVista.App`：WPF 界面、WebView2、COM 预览宿主和更新程序入口。
- `DocVista.Updater`：独立更新界面、差分包下载、安装和主程序重启。

源文件始终以只读或共享读取模式打开。设置仅写入 `%LOCALAPPDATA%\DocVista`，WebView2 数据写入同一应用目录。

## 更新流程

`scripts/package.ps1` 先发布自包含的 `win-x64` 应用，再由 Velopack 生成安装器、便携包、完整包和差分包。应用从环境变量 `DOCVISTA_UPDATE_FEED` 或安装目录的 `update-feed.txt` 读取 HTTPS 更新源。正式发布必须向脚本传入代码签名参数。

打包脚本将主程序和独立更新程序发布到同一目录，Setup 会同时安装两者。主程序只负责启动更新程序；网络检查、下载和安装逻辑全部位于 `DocVista.Updater.exe`。

安装与更新钩子只把 DocVista 注册到受支持格式的“打开方式”列表，不修改文件格式的默认打开程序；卸载时删除自身注册项。
