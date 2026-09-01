# 版本与发布

产品版本只由 `Tools/Directory.Build.props` 的 `TraceSoul2Version` 决定。日常修改和推送不改版本；只有决定集成一个正式版本时才执行：

```powershell
pwsh scripts/Set-Version.ps1 0.2.0
git add Tools/Directory.Build.props
git commit -m "release: 0.2.0"
git tag v0.2.0
git push origin main --tags
```

推送 `v*` 标签后，GitHub Actions 会验证标签和源码版本一致，构建 Windows x64、Linux x64 与 Linux arm64 发布包，生成 SHA-256，并创建 GitHub Release。普通 commit 不触发产品发布，也不会让已安装电脑看到更新。

## 已安装电脑的一键更新

WebUI「系统更新」填写一次 `owner/repository`。检查更新只读取该仓库最新的正式 Release；点击安装后会：

1. 按当前系统下载 `tracesoul2-<runtime>-v<版本>.zip` 和同名 `.sha256`；
2. 校验 SHA-256 并在家目录 `updates/` 中解包；
3. 从软件目录外启动更新器，退出旧宿主；
4. 整体替换应用目录并重启；
5. 保留旧应用目录作为可回滚备份。

`souls/`、`plugins_data/` 和 `home.json` 都在应用目录外，不会被更新包覆盖。仓库内维护的官方插件代码包会随 Release 一起升级：更新器先备份旧插件包，再替换 DLL、清单和默认资源；失败时与 App 一起回滚。独立安装的第三方插件不受影响。

Docker 部署使用宿主机 `runtime/App`、`runtime/Plugins` 和 `runtime/plugins_data`，因此也是同一套更新流程：容器内的外置更新器替换 `App`、升级 Release 声明的官方插件包，入口 supervisor 随后启动新版本，不需要挂载 Docker Socket。`runtime/plugins_data` 始终保留。

当仓库可以公开读取、但还没有正式 Release 时，GitHub 的 `releases/latest` 接口也会返回 HTTP 404。更新页会进一步检查仓库本身并明确提示“还没有正式 Release”；普通 `main` 提交不会成为可安装更新。

更新检查不保存 GitHub Token，因此 Release 仓库必须允许匿名读取。若源码需要保持私有，可以把构建产物发布到另一个公开的 Release 仓库，并在 WebUI 填那个仓库。

## 本地生成发布包

```powershell
pwsh scripts/Publish-Release.ps1
```

输出在 `artifacts/<版本>/`。软件 ZIP 同时包含 Host、每日迁移管线、外置更新器与仓库内维护的官方插件包；PluginApi 另外生成独立 `.nupkg`。ONNX 模型由 Git LFS 管理，生成发布包前必须已拉取完整文件。
