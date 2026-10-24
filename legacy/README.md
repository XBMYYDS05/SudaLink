# legacy/campusnet · 2.0 原型（仅存档）

这是项目最早的可维护版本，程序名还叫 **CampusNet**（3.0 才改名 SudaLink）。

保留它的原因只有一个：它是这条线的起点 —— 反编译原程序、确认 eportal 协议、
第一次把密码换成 DPAPI 加密，都发生在这里。

- 目标框架 `net8.0-windows`，WPF + WinForms 托盘
- **不属于当前解决方案**，不参与构建，依赖若干后来修掉的历史写法
- 想知道它做过什么，看 [`../docs/versions/v2.0.md`](../docs/versions/v2.0.md)

如果要单独编译它：

```bash
dotnet build legacy/campusnet/CampusNet.csproj -c Release
```
