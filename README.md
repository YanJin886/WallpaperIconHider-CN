**语言 / Language：** [简体中文](README.md) | [English](README.en.md)
# Wallpaper Icon Hider 简体中文版

版本：1.3.0-zh-CN.1。基于上游 v1.3.0 源码制作的本地汉化版本。

## 安装和使用

1. 运行 `WallpaperIconHider.exe`，选择安装文件夹，点击“安装”。
2. 在“安装完成”窗口选择是否创建桌面快捷方式、立即启动，点击“完成”。
3. 启动后，右键点击任务栏通知区域中的眼睛图标，打开菜单。
4. 在“设置”中选择 1–10 秒隐藏延迟，并按需要启用“随 Windows 启动”和“隐藏图标时同时隐藏鼠标指针”。
5. 鼠标停止移动达到设定时间后自动隐藏图标；再次移动鼠标即可恢复。“停用”和“退出”也会恢复图标及鼠标指针。

需要 Windows 10/11 x64，以及通过 Steam 安装并正常运行的 Wallpaper Engine。汉化版已包含 .NET 运行库，使用时无需另外安装 .NET。本程序的 EXE 同时承担安装器和后台应用的角色；首次运行会复制到所选安装目录，属于原项目的安装方式。

## 已安装原版时如何替换

1. 从原版托盘菜单退出程序。
2. 备份安装目录中的 `WallpaperIconHider.exe`，以及 `%LOCALAPPDATA%\WallpaperIconHider\settings.json`。
3. 将汉化版 EXE 复制到原安装目录，替换同名文件，再从原快捷方式启动。

请保留文件名 `WallpaperIconHider.exe`。若原版仍然存在，直接从下载目录双击新 EXE，原有逻辑会跳转到已经安装的旧 EXE；需要按上面的步骤替换安装目录中的文件。原设置和原快捷方式可继续使用。恢复原版时，退出汉化版后换回备份的 EXE 即可。

## 汉化范围

已翻译安装窗口、目录选择说明、按钮、托盘三级菜单、启动通知、应用自身的错误说明和快捷方式描述。正文使用微软雅黑 UI，符号图标保留原字体，并增加了标题、说明和通知的空间。

产品名 Wallpaper Icon Hider、Wallpaper Engine、Steam 保留原名。诊断日志、配置字段、文件路径和 Wallpaper Engine 控制命令保留原格式。Windows 自带按钮与系统异常详情遵循系统语言。未增加语言切换。

配置文件：`%LOCALAPPDATA%\WallpaperIconHider\settings.json`。

诊断日志：`%LOCALAPPDATA%\WallpaperIconHider\log.txt`。

## 从源码构建

源码文件位于随附的源码压缩包中。解压后，需要 Windows 和 .NET SDK 10.0.403；`global.json` 将 SDK 固定在该版本系列，允许同系列后续补丁。

在 PowerShell 中进入源码目录，运行：

```powershell
.\build-zh-CN.ps1
```

使用独立 SDK 时，可传入其 `dotnet.exe` 的绝对路径：

```powershell
.\build-zh-CN.ps1 -Dotnet 'C:\完整路径\dotnet.exe' -OutputDirectory 'C:\完整路径\输出目录'
```

脚本生成 Windows x64 自包含单文件版本，附上许可证，并输出 EXE 的 SHA-256。原代码存在一条 `CS0414` 未使用字段警告，原版和汉化版均有，不影响构建。

## 来源和许可

- 上游项目：https://github.com/Especiel-EA/WallpaperIconHider
- 基准标签：v1.3.0
- 基准提交：`b0f7227e7199e19a493f92d99c3f1e41c06bdab0`
- 原作者：Especiel_EA
- 许可证：MIT，完整原文见 `LICENSE`。

本地汉化版本保留原作者署名及许可证。验证范围与结果见交付包中的 `验证报告.md`。
