# DocVista 产物清单

生成日期：2026-07-19（Asia/Shanghai）

## 本地交付产物

| 项目 | 内容 |
| --- | --- |
| 文件 | `installer/DocVista-win-Setup.exe` |
| 类型 | Windows x64 经典分步安装向导（单 EXE） |
| 版本 | `0.1.6` |
| 文件大小 | 83,070,682 字节（79.22 MB） |
| SHA-256 | `4B48C9F72AD23176E4783C92F1E1494178A9E5920E0DA6E7A20CE3A9E9890556` |
| 构建源码提交 | `dc5c5390a517afaa5cec70d07cade41a71b74c86` |
| 生成时间 | 2026-07-19 21:01:05 +08:00 |
| 代码签名 | 未签名 |

`artifacts/installer` 目录只保留上述 Setup EXE，不保留 Portable、nupkg、独立更新程序或打包中间文件。

## 安装行为

- 使用 Inno Setup 经典 Windows 安装向导。
- 固定安装到当前用户的 `%LOCALAPPDATA%\DocVista`。
- 固定创建开始菜单快捷方式，可选择创建桌面快捷方式。
- 安装主程序 `DocVista.exe` 和正式卸载组件，不安装 `DocVista.Updater.exe`。
- 在 Windows“设置 > 应用”登记正式卸载项。
- 应用侧栏提供“设置”和“卸载 DocVista”，不包含“检查更新”。
- 后续版本通过新版 Setup 覆盖安装；安装和卸载不会修改用户文档。

## 发布内容

- 目标 Release：`v0.1.6`
- Release 只发布 `DocVista-win-Setup.exe`。
- 不发布在线更新清单、完整更新包或差分更新包。

## 验证状态

- Release 解决方案构建成功，0 个警告、0 个错误。
- 格式识别、设置归一化、CSV、XLSX、DOCX、PPTX、XLS、旧版 Office、PDF 文件源、CSV 渐进列探测和解析取消共 11 项测试通过。
- DOCX 测试覆盖段落/表格顺序、常用文字样式、单元格、内嵌图片及图片尺寸单位。
- 有效 PDF 已通过本机 WebView2 真实导航验证；窗口级截图确认纯白文档页、浅灰工作区及图片尺寸正确。
- Setup 文件版本和产品版本均为 `0.1.6`，Velopack 与 Inno Setup 打包流程成功完成。
- `artifacts/installer` 中仅保留一个 Setup EXE，发布和 publish 中间目录均已清理。

## 注意事项

Setup 尚未代码签名，Windows 可能显示“未知发布者”或 SmartScreen 提示。正式对外分发前应使用可信代码签名证书重新构建。
