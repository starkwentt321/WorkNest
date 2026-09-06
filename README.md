# WorkNest

WorkNest 是一个面向 Windows 的个人工作空间导航器，用于将常用目录、文件、程序和网站按工作区集中管理，并从一个窗口快速查找和启动。

## 当前状态

版本：`0.1.0`。项目仍处于早期版本，主要面向 Windows 11 用户。

当前包含的能力：

- 工作区和资源管理；
- 搜索、置顶、排序和最近使用记录；
- 目录浏览、文件夹和文件拖入新增；
- 托盘常驻与全局快捷键；
- 浅色/深色主题和背景设置；
- 本地 SQLite 数据、备份、配置导入与导出。

## 环境要求

- Windows 11；
- .NET 10 SDK；
- WPF 桌面运行环境。

## 构建与测试

在仓库根目录执行：

```powershell
dotnet restore WorkNest.slnx
dotnet build WorkNest.slnx
dotnet test WorkNest.slnx
```

启动应用：

```powershell
dotnet run --project src/WorkNest.App/WorkNest.App.csproj
```

## 数据与隐私

应用采用离线优先设计，运行数据保存在当前 Windows 用户的本地应用数据目录中，不需要登录或云端服务。

## 项目结构

```text
src/
  WorkNest.App/              WPF 应用与界面
  WorkNest.Application/      应用服务与用例
  WorkNest.Domain/            领域模型
  WorkNest.Infrastructure/   SQLite、迁移、日志与备份
  WorkNest.Platform.Windows/  Windows 平台适配
tests/                        单元、应用层与集成测试
```

## 许可证

本项目采用 [MIT License](LICENSE)。
