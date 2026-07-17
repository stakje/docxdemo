# DocVista 产物清单

生成日期：2026-07-17（Asia/Shanghai）

## 本地交付产物

| 项目 | 内容 |
| --- | --- |
| 文件 | `installer/DocVista-win-Setup.exe` |
| 类型 | Windows x64 经典分步安装向导（单 EXE） |
| 版本 | `0.1.5` |
| 文件大小 | 83,062,366 字节（79.21 MB） |
| SHA-256 | `5464A595EA306247593AE1253407DAEDF44DDA03C4D28327FF71FF29F465F013` |
| 构建源码提交 | `6ec8cc699e934069b54ee1585a212d47e5f20e6d` |
| 生成时间 | 2026-07-17 13:11:18 +08:00 |
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

- 目标 Release：`v0.1.5`
- Release 只发布 `DocVista-win-Setup.exe`。
- 不发布在线更新清单、完整更新包或差分更新包。

## 验证状态

- Release 解决方案构建成功，0 个编译错误。
- 格式识别、设置归一化、CSV、XLSX、DOCX、PPTX、XLS 和旧版 Office 共 8 项测试通过。
- DOCX 测试覆盖段落/表格顺序、常用文字样式、单元格和内嵌图片。
- 实际安装后主程序版本为 `0.1.5`，应用启动并保持响应。
- 桌面和开始菜单快捷方式目标与图标已验证。
- 设置、缩放、最近记录删除和应用内卸载入口已验证，“检查更新”入口不存在。
- 正式卸载后安装目录、快捷方式和卸载登记均已移除，本机保持未安装状态。

## 注意事项

Setup 尚未代码签名，Windows 可能显示“未知发布者”或 SmartScreen 提示。正式对外分发前应使用可信代码签名证书重新构建。
