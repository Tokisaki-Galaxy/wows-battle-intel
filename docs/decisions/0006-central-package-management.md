# ADR 0006：集中式包版本管理

- 状态：已采纳
- 日期：2026-10-14

## 背景

.NET 解决方案会包含多个项目：核心库、客户端、测试等。如果每个项目各自声明 NuGet 包版本，容易出现同一依赖在不同项目里版本不一致，导致还原冲突与行为差异。

## 决策

使用 `Directory.Packages.props` 集中管理所有包版本，配合 `ManagePackageVersionsCentrally`。

`Directory.Build.props` 集中管理编译属性：语言版本、可空性、警告即错误、分析级别。

## 理由

**版本一致。** 所有项目引用同一版本的依赖，不会出现版本漂移。

**升级一处生效。** 升级包只需改一个文件。

**项目文件干净。** `csproj` 只声明用了哪些包，不写版本号，可读性更好。

**编译属性统一。** `TreatWarningsAsErrors`、`Nullable enable`、`LangVersion` 等在一个地方定义，所有项目一致，避免某个新项目漏配。

## 代价

**新依赖要改两处。** 加包时要在 `Directory.Packages.props` 加版本，在项目里加引用。

**对工具链有要求。** 需要较新的 SDK。本项目用 .NET 10，不是问题。

## 实现要点

`Directory.Packages.props`：

```xml
<ItemGroup>
  <PackageVersion Include="Newtonsoft.Json" Version="13.0.4" />
</ItemGroup>
```

项目文件：

```xml
<ItemGroup>
  <PackageReference Include="Newtonsoft.Json" />
</ItemGroup>
```

注意项目引用不带 `Version` 属性。

## 编译属性

`Directory.Build.props` 里的关键项：

| 属性 | 值 | 作用 |
|---|---|---|
| LangVersion | latest | 用最新 C# 语言特性 |
| Nullable | enable | 可空引用类型检查 |
| ImplicitUsings | enable | 隐式全局 using |
| TreatWarningsAsErrors | true | 警告即错误，及早发现问题 |
| EnforceCodeStyleInBuild | true | 构建时执行代码风格分析 |
| GenerateDocumentationFile | true | 启用 IDE0005 分析，同时抑制 CS1591 |
