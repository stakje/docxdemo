# DocVista 架构

DocVista 是 Windows 10/11 x64 只读文档查看器。界面使用 WPF，PDF 由 WebView2 渲染，表格格式由内置解析器渲染，Office 二进制与演示文档由 Windows Preview Handler 承载。

## 项目边界

- `DocVista.Core`：格式识别、最近文件与用户设置，不依赖 UI。
- `DocVista.Rendering`：CSV/XLSX 解析与表格模型。
- `DocVista.App`：WPF 界面、WebView2、COM 预览宿主、设置与卸载入口。

源文件始终以只读或共享读取模式打开。设置仅写入 `%LOCALAPPDATA%\DocVista`，WebView2 数据写入同一应用目录。

## 更新流程

`scripts/package.ps1` 先发布自包含的 `win-x64` 应用，由 Velopack 生成内部安装引导程序，再由 Inno Setup 包装为面向用户的经典单 EXE 安装向导。Velopack 仅负责安装结构与正式卸载，不提供用户可见的在线更新入口。正式发布必须向脚本传入代码签名参数。

Setup 只安装主程序及卸载所需组件。新版本通过新版 Setup 覆盖安装。

安装与更新钩子只把 DocVista 注册到受支持格式的“打开方式”列表，不修改文件格式的默认打开程序；卸载时删除自身注册项。
